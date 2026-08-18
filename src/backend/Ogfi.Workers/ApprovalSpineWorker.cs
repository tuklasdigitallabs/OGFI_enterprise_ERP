namespace Ogfi.Workers;

public sealed class ApprovalSpineWorker(
    TenantWorkerRunner runner,
    ILogger<ApprovalSpineWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => runner.RunAsync(
            WorkerCodes.Approval,
            static (services, tenantId, token) => services.GetRequiredService<ApprovalSpineProcessor>()
                .ProcessTenantAsync(tenantId, token),
            logger,
            stoppingToken);
}
