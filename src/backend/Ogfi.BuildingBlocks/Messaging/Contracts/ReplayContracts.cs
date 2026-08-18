namespace Ogfi.BuildingBlocks.Messaging.Contracts;

public sealed record ReplayDispatchCommand(
    Guid TenantId,
    Guid OperationId,
    string OperationType,
    string OwnerModule,
    Guid OriginalSourceEventId,
    string? OriginalCausationId,
    string CorrelationId,
    Guid? LegalEntityId,
    Guid? OutletId);

public sealed record ReplayDispatchResult(
    bool Succeeded,
    string? ResultReferenceType = null,
    Guid? ResultReferenceId = null,
    string? SafeErrorCode = null,
    string SafeDetailJson = "{}");

public interface IReplayOwnerHandler
{
    string OwnerModule { get; }
    string OperationType { get; }

    Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command,
        CancellationToken cancellationToken = default);
}
