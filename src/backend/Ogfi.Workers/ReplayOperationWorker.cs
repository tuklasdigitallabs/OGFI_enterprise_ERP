using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.DurableOperations;
using Ogfi.Modules.DurableOperations.Persistence;

namespace Ogfi.Workers;

public sealed class ReplayOperationWorker(
    TenantWorkerRunner runner,
    ReplayWorkerOptions options,
    ILogger<ReplayOperationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => runner.RunAsync(WorkerCodes.Replay, ProcessTenantOnceAsync, logger, stoppingToken);

    public async Task<ProcessorIterationResult> ProcessTenantOnceAsync(
        IServiceProvider services, Guid tenantId, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<DurableOperationsDbContext>();
        var operations = await db.Operations.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && (x.Status == OperationStatuses.Queued
                            || x.Status == OperationStatuses.Running
                            || x.Status == OperationStatuses.CancelRequested))
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Take(Math.Clamp(options.BatchSize, 1, 100))
            .ToListAsync(cancellationToken);
        Guid? last = null;
        string? lastError = null;
        foreach (var operation in operations)
        {
            last = operation.OriginalSourceEventId;
            try
            {
                await services.GetRequiredService<OperationalReplayService>()
                    .ExecuteAsync(
                        tenantId, operation.Id, WorkerCodes.Replay, cancellationToken);
            }
            catch (DurableOperationRuleException exception)
                when (exception.Code is "OPERATIONS.ATTEMPT.ACTIVE_EXISTS"
                    or "OPERATIONS.ATTEMPT.LEASE_LOST"
                    or "OPERATIONS.TRANSITION.VERSION_CONFLICT"
                    or "OPERATIONS.REPLAY.NOT_EXECUTABLE")
            {
                lastError = exception.Code;
                logger.LogInformation(
                    "Replay operation {OperationId} was not owned by this replica: {Code}",
                    operation.Id, exception.Code);
            }
        }
        db.ChangeTracker.Clear();
        var pending = await db.Operations.AsNoTracking().CountAsync(
            x => x.TenantId == tenantId && x.Status == OperationStatuses.Queued, cancellationToken);
        var running = await db.Operations.AsNoTracking().CountAsync(
            x => x.TenantId == tenantId && x.Status == OperationStatuses.Running, cancellationToken);
        var failed = await db.Operations.AsNoTracking().CountAsync(
            x => x.TenantId == tenantId && x.Status == OperationStatuses.Failed, cancellationToken);
        var oldest = await db.Operations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && (x.Status == OperationStatuses.Queued || x.Status == OperationStatuses.Running))
            .MinAsync(x => (DateTimeOffset?)x.CreatedAtUtc, cancellationToken);
        return new ProcessorIterationResult(last, pending, running, failed, oldest, lastError);
    }
}
