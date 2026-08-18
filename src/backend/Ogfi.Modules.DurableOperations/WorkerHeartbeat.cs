namespace Ogfi.Modules.DurableOperations;

public sealed class WorkerHeartbeat
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string WorkerCode { get; set; }
    public DateTimeOffset LastIterationStartedAtUtc { get; set; }
    public DateTimeOffset? LastSucceededAtUtc { get; set; }
    public DateTimeOffset? LastFailedAtUtc { get; set; }
    public Guid? CurrentOrLastSourceId { get; set; }
    public int PendingCount { get; set; }
    public int RetryPendingCount { get; set; }
    public int TerminalFailureCount { get; set; }
    public DateTimeOffset? OldestPendingAtUtc { get; set; }
    public string? LastSafeErrorCode { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
