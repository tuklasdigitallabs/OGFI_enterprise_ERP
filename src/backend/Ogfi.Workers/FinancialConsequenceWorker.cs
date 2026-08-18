using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Persistence;

namespace Ogfi.Workers;

public sealed class FinancialConsequenceWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<FinancialConsequenceWorker> logger) : BackgroundService
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
                    await scope.ServiceProvider.GetRequiredService<FinancialConsequenceProcessor>()
                        .ProcessTenantAsync(tenantId, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Financial consequence worker iteration failed; retrying with bounded delay");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task<Guid[]> GetTenantIdsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FoundationDbContext>()
            .Tenants.AsNoTracking().Select(x => x.Id).ToArrayAsync(cancellationToken);
    }
}
