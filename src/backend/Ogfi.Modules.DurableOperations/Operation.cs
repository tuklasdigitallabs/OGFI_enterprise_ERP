namespace Ogfi.Modules.DurableOperations;

public static class OperationStatuses
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string CancelRequested = "CANCEL_REQUESTED";
    public const string Cancelled = "CANCELLED";
}

public sealed class Operation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string ReplayRequestKey { get; set; }
    public required string OperationType { get; set; }
    public required string OwnerModule { get; set; }
    public required string Status { get; set; }
    public Guid OriginalSourceEventId { get; set; }
    public string? OriginalCausationId { get; set; }
    public required string CorrelationId { get; set; }
    public Guid? LegalEntityId { get; set; }
    public Guid? OutletId { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public Guid? RequestedByMembershipId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? CancelRequestedAtUtc { get; set; }
    public string? ResultReferenceType { get; set; }
    public Guid? ResultReferenceId { get; set; }
    public string? SafeErrorCode { get; set; }
    public string? SafeDetailJson { get; set; }
    public bool Replayable { get; set; }
    public long Version { get; set; }
}

public sealed class DurableOperationRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
