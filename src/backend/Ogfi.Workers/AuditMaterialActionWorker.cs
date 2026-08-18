using Ogfi.BuildingBlocks.Multitenancy;

namespace Ogfi.Workers;

public sealed class AuditMaterialActionWorker(
    TenantWorkerRunner runner,
    ILogger<AuditMaterialActionWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => runner.RunAsync(
            WorkerCodes.Audit,
            static (services, tenantId, token) => services
                .GetRequiredService<AuditMaterialActionProcessor>()
                .ProcessTenantAsync(tenantId, token),
            logger,
            stoppingToken);
}
