namespace Ogfi.Modules.Workflow;

public static class WorkflowDefinitionCodes
{
    public const string PurchaseOrderApproval = "RS01.PO.APPROVAL";
}

public static class WorkflowSubjectTypes
{
    public const string PurchaseOrder = "PURCHASE_ORDER";
}

public static class WorkflowStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
}

public static class WorkflowTaskKeys
{
    public const string PurchaseOrderApproval = "PO_APPROVAL";
}

public sealed class WorkflowDefinitionVersion
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public int Version { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class WorkflowInstance
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DefinitionVersionId { get; set; }
    public required string SubjectType { get; set; }
    public Guid SubjectId { get; set; }
    public int ApprovalRound { get; set; }
    public long SubjectVersion { get; set; }
    public Guid RequesterUserId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid OutletId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public decimal PurchaseOrderTotal { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class WorkflowTask
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InstanceId { get; set; }
    public required string StepKey { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class WorkflowTaskCandidate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class ApprovalDecision
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid InstanceId { get; set; }
    public Guid TaskId { get; set; }
    public required string Decision { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset DecidedAtUtc { get; set; }
}

public sealed record PurchaseOrderApprovalStartCommand(
    Guid TenantId,
    Guid PurchaseOrderId,
    int ApprovalRound,
    long SubjectVersion,
    Guid RequestedByUserId,
    Guid LegalEntityId,
    Guid OutletId,
    DateOnly BusinessDate,
    decimal PurchaseOrderTotal,
    string Currency,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed record WorkflowStartResult(Guid InstanceId, Guid TaskId, Guid DefinitionVersionId, int DefinitionVersion);

public sealed record ApprovalInboxItem(
    Guid TaskId,
    Guid InstanceId,
    Guid PurchaseOrderId,
    int ApprovalRound,
    long SubjectVersion,
    decimal PurchaseOrderTotal,
    string Currency,
    Guid OutletId,
    Guid RequesterUserId,
    DateOnly BusinessDate,
    DateTimeOffset CreatedAtUtc);

public sealed record ApprovalTaskDetail(
    Guid TaskId,
    Guid InstanceId,
    Guid DefinitionVersionId,
    int DefinitionVersion,
    Guid PurchaseOrderId,
    int ApprovalRound,
    long SubjectVersion,
    decimal PurchaseOrderTotal,
    string Currency,
    Guid LegalEntityId,
    Guid OutletId,
    Guid RequesterUserId,
    DateOnly BusinessDate,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record PurchaseOrderApprovalCompletedV1(
    Guid EventId,
    Guid TenantId,
    Guid WorkflowInstanceId,
    Guid WorkflowTaskId,
    Guid PurchaseOrderId,
    int ApprovalRound,
    long SubjectVersion,
    string Decision,
    Guid ActorUserId,
    DateTimeOffset DecidedAtUtc,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed class WorkflowRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
