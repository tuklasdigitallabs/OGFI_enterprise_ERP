using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Persistence;

namespace Ogfi.Workers;

public sealed class TenantWorkerRunner(
    IServiceScopeFactory scopeFactory,
    WorkerHeartbeatReporter heartbeats,
    TimeProvider timeProvider)
{
    public async Task RunAsync(
        string workerCode,
        Func<IServiceProvider, Guid, CancellationToken, Task<ProcessorIterationResult>> processor,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var tenantId in await GetTenantIdsAsync(stoppingToken))
                    await RunTenantAsync(workerCode, tenantId, processor, logger, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Worker {WorkerCode} tenant discovery failed; retrying with bounded delay", workerCode);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public async Task<ProcessorIterationResult> RunTenantAsync(
        string workerCode,
        Guid tenantId,
        Func<IServiceProvider, Guid, CancellationToken, Task<ProcessorIterationResult>> processor,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        HeartbeatIteration iteration;
        try
        {
            iteration = await heartbeats.RecordStartAsync(tenantId, workerCode, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Heartbeat start persistence failed for {WorkerCode} tenant {TenantId}", workerCode, tenantId);
            iteration = new HeartbeatIteration(tenantId, workerCode, timeProvider.GetUtcNow(), Guid.NewGuid());
        }

        var result = ProcessorIterationResult.Empty;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>().SetCandidateTenant(tenantId);
            result = await processor(scope.ServiceProvider, tenantId, cancellationToken);
            try
            {
                await heartbeats.RecordSuccessAsync(iteration, result, cancellationToken);
            }
            catch (Exception heartbeatError) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(heartbeatError, "Heartbeat result persistence failed for {WorkerCode} tenant {TenantId}", workerCode, tenantId);
            }
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var safeCode = ex.GetType().Name.ToUpperInvariant();
            try
            {
                await heartbeats.RecordFailureAsync(iteration, result, safeCode, cancellationToken);
            }
            catch (Exception heartbeatError)
            {
                logger.LogError(heartbeatError, "Heartbeat failure persistence failed for {WorkerCode} tenant {TenantId}", workerCode, tenantId);
            }
            logger.LogError(ex, "Worker {WorkerCode} iteration failed for tenant {TenantId}", workerCode, tenantId);
            return result with { LastSafeErrorCode = safeCode };
        }
    }

    private async Task<Guid[]> GetTenantIdsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FoundationDbContext>()
            .Tenants.AsNoTracking().Select(x => x.Id).Take(10_000).ToArrayAsync(cancellationToken);
    }
}
