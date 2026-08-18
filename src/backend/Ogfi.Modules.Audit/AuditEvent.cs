namespace Ogfi.Modules.Audit;

public static class AuditPermissionCodes
{
    public const string Read = "audit.read";
    public const string TraceRead = "audit.trace.read";
}

public static class AuditActorTypes
{
    public const string User = "USER";
    public const string Worker = "WORKER";
    public const string System = "SYSTEM";
}

public static class AuditOutcomes
{
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Rejected = "REJECTED";
}

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string ActorType { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? ActorMembershipId { get; set; }
    public required string Action { get; set; }
    public required string SourceModule { get; set; }
    public required string ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public long? ResourceRevision { get; set; }
    public Guid? LegalEntityId { get; set; }
    public Guid? OutletId { get; set; }
    public DateOnly? BusinessDate { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public required string CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public Guid? SourceEventId { get; set; }
    public required string SafeEvidenceJson { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public Guid? WorkflowInstanceId { get; set; }
    public Guid? ApprovalTaskId { get; set; }
    public Guid? ApprovalDecisionId { get; set; }
    public Guid? GoodsReceiptId { get; set; }
    public Guid? InventoryMovementId { get; set; }
    public Guid? FinanceSourcePostingId { get; set; }
    public Guid? JournalId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AuditRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
