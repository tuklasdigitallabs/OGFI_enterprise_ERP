namespace Ogfi.Modules.DurableOperations;

public static class OperationAttemptStatuses
{
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Abandoned = "ABANDONED";
}

public sealed class OperationAttempt
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OperationId { get; set; }
    public int AttemptNumber { get; set; }
    public required string Status { get; set; }
    public required string WorkerCode { get; set; }
    public required string LeaseOwner { get; set; }
    public Guid LeaseToken { get; set; }
    public DateTimeOffset LeaseAcquiredAtUtc { get; set; }
    public DateTimeOffset LeaseExpiresAtUtc { get; set; }
    public DateTimeOffset LastLeaseHeartbeatAtUtc { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? SafeErrorCode { get; set; }
    public required string SafeDetailJson { get; set; }
    public Guid OriginalSourceEventId { get; set; }
    public string? OriginalCausationId { get; set; }
    public required string CorrelationId { get; set; }
    public long Version { get; set; }
}
