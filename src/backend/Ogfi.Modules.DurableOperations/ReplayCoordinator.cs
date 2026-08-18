using Ogfi.BuildingBlocks.Messaging.Contracts;

namespace Ogfi.Modules.DurableOperations;

public sealed record ReplayRequest(
    Guid TenantId,
    string OperationType,
    string OwnerModule,
    string FailureClassification,
    bool Replayable,
    Guid OriginalSourceEventId,
    string? OriginalCausationId,
    string CorrelationId,
    Guid? LegalEntityId = null,
    Guid? OutletId = null,
    Guid? RequestedByUserId = null,
    Guid? RequestedByMembershipId = null);

public sealed class ReplayCoordinator(
    DurableOperationService operations,
    IEnumerable<IReplayOwnerHandler> handlers)
{
    public async Task<Operation> ReplayAsync(
        ReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        var classification = Normalize(request.FailureClassification);
        if (!request.Replayable || ProcessingFailureClassifications.IsTerminalInvalid(classification))
            throw new DurableOperationRuleException("OPERATIONS.REPLAY.NOT_ALLOWED", "Terminal-invalid or non-replayable failures cannot be replayed.");

        var ownerModule = Normalize(request.OwnerModule);
        var operationType = Normalize(request.OperationType);
        var handler = handlers.SingleOrDefault(x =>
            string.Equals(Normalize(x.OwnerModule), ownerModule, StringComparison.Ordinal)
            && string.Equals(Normalize(x.OperationType), operationType, StringComparison.Ordinal))
            ?? throw new DurableOperationRuleException("OPERATIONS.REPLAY.HANDLER_NOT_FOUND", "No neutral owner replay handler is registered.");

        var operation = await operations.CreateOrReuseReplayOperationAsync(
            new CreateReplayOperationRequest(
                request.TenantId, operationType, ownerModule, request.OriginalSourceEventId,
                request.OriginalCausationId, request.CorrelationId, request.LegalEntityId, request.OutletId,
                request.RequestedByUserId, request.RequestedByMembershipId, Replayable: true),
            cancellationToken);
        if (operation.Status != OperationStatuses.Queued) return operation;

        operation = await operations.TransitionAsync(
            request.TenantId, operation.Id, operation.Version, OperationStatuses.Running,
            cancellationToken: cancellationToken);
        ReplayDispatchResult result;
        try
        {
            result = await handler.ReplayAsync(
                new ReplayDispatchCommand(
                    request.TenantId, operation.Id, operationType, ownerModule,
                    request.OriginalSourceEventId, request.OriginalCausationId, request.CorrelationId,
                    request.LegalEntityId, request.OutletId),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await operations.TransitionAsync(
                request.TenantId, operation.Id, operation.Version, OperationStatuses.Failed,
                safeErrorCode: "OPERATIONS.REPLAY.OWNER_HANDLER_FAILED", safeDetailJson: "{}",
                cancellationToken: cancellationToken);
        }

        return await operations.TransitionAsync(
            request.TenantId,
            operation.Id,
            operation.Version,
            result.Succeeded ? OperationStatuses.Succeeded : OperationStatuses.Failed,
            result.ResultReferenceType,
            result.ResultReferenceId,
            result.Succeeded ? null : result.SafeErrorCode ?? "OPERATIONS.REPLAY.OWNER_REJECTED",
            result.SafeDetailJson,
            cancellationToken);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DurableOperationRuleException("OPERATIONS.REPLAY.INVALID", "Replay classification, owner and operation type are required.");
        return value.Trim().ToUpperInvariant();
    }
}
