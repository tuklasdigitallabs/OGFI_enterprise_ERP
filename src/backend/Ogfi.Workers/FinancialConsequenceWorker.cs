namespace Ogfi.Workers;

public sealed class FinancialConsequenceWorker(
    TenantWorkerRunner runner,
    ILogger<FinancialConsequenceWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => runner.RunAsync(
            WorkerCodes.Finance,
            static (services, tenantId, token) => services.GetRequiredService<FinancialConsequenceProcessor>()
                .ProcessTenantAsync(tenantId, token),
            logger,
            stoppingToken);
}
