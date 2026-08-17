using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Persistence;

namespace Ogfi.Workers;

public sealed class ApprovalSpineWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ApprovalSpineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var tenantId in await GetTenantIdsAsync(stoppingToken))
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var executionContext = scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>();
                    executionContext.SetCandidateTenant(tenantId);
                    var processor = scope.ServiceProvider.GetRequiredService<ApprovalSpineProcessor>();
                    await processor.ProcessTenantAsync(tenantId, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Approval spine worker iteration failed; retrying with bounded delay");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task<Guid[]> GetTenantIdsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FoundationDbContext>();
        return await db.Tenants.AsNoTracking().Select(x => x.Id).ToArrayAsync(cancellationToken);
    }
}
