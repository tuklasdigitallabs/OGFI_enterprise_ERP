namespace Ogfi.Workers;

public sealed class StockConsequenceWorker(TenantWorkerRunner runner, ILogger<StockConsequenceWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => runner.RunAsync(
            WorkerCodes.Inventory,
            static (services, tenantId, token) => services.GetRequiredService<StockConsequenceProcessor>()
                .ProcessTenantAsync(tenantId, token),
            logger,
            stoppingToken);
}
