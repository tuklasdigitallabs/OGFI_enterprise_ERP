using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.DurableOperations.Persistence;

namespace Ogfi.Modules.DurableOperations;

public static class OperationsPermissionCodes
{
    public const string ProcessingRead = "operations.processing.read";
    public const string ProcessingReplay = "operations.processing.replay";
}

public static class OperationalHealthStatuses
{
    public const string Healthy = "HEALTHY";
    public const string Degraded = "DEGRADED";
    public const string Unhealthy = "UNHEALTHY";
    public const string Stale = "STALE";
    public const string Unknown = "UNKNOWN";
}

public sealed class OperationalHealthOptions
{
    public TimeSpan StaleHeartbeatAge { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan DegradedPendingAge { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan UnhealthyPendingAge { get; set; } = TimeSpan.FromMinutes(15);
    public int DegradedRetryPendingCount { get; set; } = 1;
    public int UnhealthyRetryPendingCount { get; set; } = 10;
    public int UnhealthyTerminalFailureCount { get; set; } = 1;
    public TimeSpan DegradedDeliveryLag { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan UnhealthyDeliveryLag { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record WorkerHealthProjection(
    Guid TenantId,
    string WorkerCode,
    string Status,
    DateTimeOffset? LastObservedAtUtc,
    TimeSpan? HeartbeatAge,
    TimeSpan? OldestPendingAge,
    TimeSpan? DeliveryLag,
    int PendingCount,
    int RetryPendingCount,
    int TerminalFailureCount,
    string? LastSafeErrorCode);

public sealed record ProcessingFailureQuery(
    string? State = null,
    string? OwnerModule = null,
    string? ProcessorCode = null,
    int Limit = 50);

public sealed class OperationalHealthService(
    DurableOperationsDbContext dbContext,
    TimeProvider timeProvider,
    OperationalHealthOptions options)
{
    private const int MaximumPageSize = 100;

    public async Task<IReadOnlyList<WorkerHealthProjection>> QueryWorkerHealthAsync(
        Guid tenantId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var rows = await dbContext.WorkerHeartbeats.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.WorkerCode)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        return rows.Select(x => Project(x, now)).ToArray();
    }

    public async Task<WorkerHealthProjection> GetWorkerHealthAsync(
        Guid tenantId,
        string workerCode,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(workerCode);
        var row = await dbContext.WorkerHeartbeats.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.WorkerCode == normalized,
            cancellationToken);
        return row is null
            ? new WorkerHealthProjection(tenantId, normalized, OperationalHealthStatuses.Unknown,
                null, null, null, null, 0, 0, 0, null)
            : Project(row, timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<ProcessingFailureProjection>> QueryFailuresAsync(
        Guid tenantId,
        ProcessingFailureQuery request,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(request.Limit);
        var query = dbContext.ProcessingFailures.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(request.State))
            query = query.Where(x => x.State == request.State.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(request.OwnerModule))
            query = query.Where(x => x.OwnerModule == request.OwnerModule.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(request.ProcessorCode))
            query = query.Where(x => x.ProcessorCode == request.ProcessorCode.Trim().ToUpperInvariant());
        return await query.OrderByDescending(x => x.LastFailedAtUtc).ThenBy(x => x.Id)
            .Take(request.Limit).ToListAsync(cancellationToken);
    }

    private WorkerHealthProjection Project(WorkerHeartbeat row, DateTimeOffset now)
    {
        var heartbeatAge = NonNegative(now - row.UpdatedAtUtc);
        TimeSpan? oldestPendingAge = row.OldestPendingAtUtc is { } pending
            ? NonNegative(now - pending)
            : null;
        // Oldest pending age is the durable delivery-lag signal currently available to Phase 3.
        var deliveryLag = oldestPendingAge;
        var status = heartbeatAge > options.StaleHeartbeatAge
            ? OperationalHealthStatuses.Stale
            : row.TerminalFailureCount >= options.UnhealthyTerminalFailureCount
              || row.RetryPendingCount >= options.UnhealthyRetryPendingCount
              || oldestPendingAge >= options.UnhealthyPendingAge
              || deliveryLag >= options.UnhealthyDeliveryLag
                ? OperationalHealthStatuses.Unhealthy
                : row.RetryPendingCount >= options.DegradedRetryPendingCount
                  || oldestPendingAge >= options.DegradedPendingAge
                  || deliveryLag >= options.DegradedDeliveryLag
                    ? OperationalHealthStatuses.Degraded
                    : OperationalHealthStatuses.Healthy;
        return new WorkerHealthProjection(
            row.TenantId, row.WorkerCode, status, row.UpdatedAtUtc, heartbeatAge,
            oldestPendingAge, deliveryLag, row.PendingCount, row.RetryPendingCount,
            row.TerminalFailureCount, row.LastSafeErrorCode);
    }

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string Normalize(string workerCode)
    {
        if (string.IsNullOrWhiteSpace(workerCode) || workerCode.Trim().Length > 100)
            throw new DurableOperationRuleException("OPERATIONS.HEALTH.WORKER_INVALID", "Worker code is required and bounded.");
        return workerCode.Trim().ToUpperInvariant();
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > MaximumPageSize)
            throw new DurableOperationRuleException(
                "OPERATIONS.QUERY.LIMIT_INVALID", $"Limit must be between 1 and {MaximumPageSize}.");
    }
}
