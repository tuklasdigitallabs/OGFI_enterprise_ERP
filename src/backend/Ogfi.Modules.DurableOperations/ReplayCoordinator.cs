using Ogfi.BuildingBlocks.Messaging.Contracts;

namespace Ogfi.Modules.DurableOperations;

public sealed class ReplayCoordinator(
    DurableOperationService operations,
    IEnumerable<IReplayOwnerHandler> handlers)
{
    private const int MaximumReplayAttempts = 3;

    public async Task<Operation> RequestReplayForFailureAsync(
        Guid tenantId,
        Guid failureId,
        string replayRequestKey,
        Guid? requestedByUserId,
        Guid? requestedByMembershipId,
        CancellationToken cancellationToken = default)
    {
        var failure = await operations.GetFailureAsync(tenantId, failureId, cancellationToken);
        if (!failure.Replayable || ProcessingFailureStates.IsTerminal(failure.State)
                                || ProcessingFailureClassifications.IsTerminalInvalid(failure.FailureClassification))
            throw new DurableOperationRuleException(
                "OPERATIONS.REPLAY.NOT_ALLOWED", "Persisted failure is terminal or non-replayable.");

        var operation = await operations.CreateOrReuseReplayOperationAsync(
            new CreateReplayOperationRequest(
                tenantId,
                replayRequestKey,
                failure.ProcessorCode,
                failure.OwnerModule,
                failure.OriginalSourceEventId,
                failure.OriginalCausationId,
                failure.CorrelationId,
                failure.LegalEntityId,
                failure.OutletId,
                requestedByUserId,
                requestedByMembershipId,
                Replayable: true),
            cancellationToken);
        await operations.LinkFailureToOperationAsync(tenantId, failureId, operation.Id, cancellationToken);
        return operation;
    }

    public async Task<Operation> ExecuteQueuedReplayOperationAsync(
        Guid tenantId,
        Guid operationId,
        string workerCode,
        CancellationToken cancellationToken = default)
    {
        var operation = await operations.GetOperationAsync(tenantId, operationId, cancellationToken);
        var handler = FindHandler(operation.OwnerModule, operation.OperationType);
        if (operation.Status == OperationStatuses.Queued)
        {
            operation = await operations.TransitionAsync(
                tenantId, operation.Id, operation.Version, OperationStatuses.Running,
                cancellationToken: cancellationToken);
        }
        else if (operation.Status != OperationStatuses.Running)
        {
            throw new DurableOperationRuleException(
                "OPERATIONS.REPLAY.NOT_EXECUTABLE", "Only queued or retry-eligible running operations can execute.");
        }

        var attempt = await operations.StartAttemptAsync(
            tenantId, operation.Id, workerCode,
            """{"stage":"OWNER_DISPATCH","status":"RUNNING"}""", cancellationToken);
        await operations.AddNextCheckpointAsync(
            tenantId, operation.Id, "OWNER_DISPATCH", 10,
            $$"""{"stage":"OWNER_DISPATCH","retryCount":{{attempt.AttemptNumber}}}""", cancellationToken);

        ReplayDispatchResult result;
        try
        {
            result = await handler.ReplayAsync(
                new ReplayDispatchCommand(
                    tenantId, operation.Id, operation.OperationType, operation.OwnerModule,
                    operation.OriginalSourceEventId, operation.OriginalCausationId, operation.CorrelationId,
                    operation.LegalEntityId, operation.OutletId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CompleteCancellationAsync(tenantId, operation, attempt);
            throw;
        }
        catch (Exception)
        {
            const string safeErrorCode = "OPERATIONS.REPLAY.OWNER_HANDLER_FAILED";
            await operations.CompleteAttemptAsync(
                tenantId, attempt.Id, succeeded: false, safeErrorCode, "{}", CancellationToken.None);
            var exhausted = attempt.AttemptNumber >= MaximumReplayAttempts;
            await operations.AddNextCheckpointAsync(
                tenantId, operation.Id, exhausted ? "OWNER_FAILED" : "OWNER_RETRY_PENDING",
                exhausted ? 100 : 50, """{"reasonCode":"OWNER_HANDLER_FAILED"}""", CancellationToken.None);
            if (!exhausted) return await operations.GetOperationAsync(tenantId, operation.Id, CancellationToken.None);
            return await operations.TransitionAsync(
                tenantId, operation.Id, operation.Version, OperationStatuses.Failed,
                safeErrorCode: safeErrorCode, safeDetailJson: "{}", cancellationToken: CancellationToken.None);
        }

        if (result.Succeeded)
        {
            await operations.CompleteAttemptAsync(
                tenantId, attempt.Id, succeeded: true, safeDetailJson: result.SafeDetailJson,
                cancellationToken: cancellationToken);
            await operations.AddNextCheckpointAsync(
                tenantId, operation.Id, "OWNER_SUCCEEDED", 100, result.SafeDetailJson, cancellationToken);
            return await operations.TransitionAsync(
                tenantId, operation.Id, operation.Version, OperationStatuses.Succeeded,
                result.ResultReferenceType, result.ResultReferenceId,
                safeDetailJson: result.SafeDetailJson, cancellationToken: cancellationToken);
        }

        var resultErrorCode = result.SafeErrorCode ?? "OPERATIONS.REPLAY.OWNER_REJECTED";
        await operations.CompleteAttemptAsync(
            tenantId, attempt.Id, succeeded: false, resultErrorCode, result.SafeDetailJson, cancellationToken);
        var resultExhausted = !result.Retryable || attempt.AttemptNumber >= MaximumReplayAttempts;
        await operations.AddNextCheckpointAsync(
            tenantId, operation.Id, resultExhausted ? "OWNER_FAILED" : "OWNER_RETRY_PENDING",
            resultExhausted ? 100 : 50, result.SafeDetailJson, cancellationToken);
        if (!resultExhausted) return await operations.GetOperationAsync(tenantId, operation.Id, cancellationToken);
        return await operations.TransitionAsync(
            tenantId, operation.Id, operation.Version, OperationStatuses.Failed,
            safeErrorCode: resultErrorCode, safeDetailJson: result.SafeDetailJson,
            cancellationToken: cancellationToken);
    }

    private async Task CompleteCancellationAsync(Guid tenantId, Operation operation, OperationAttempt attempt)
    {
        const string safeErrorCode = "OPERATIONS.REPLAY.CANCELLED";
        await operations.CompleteAttemptAsync(
            tenantId, attempt.Id, succeeded: false, safeErrorCode, "{}", CancellationToken.None);
        await operations.AddNextCheckpointAsync(
            tenantId, operation.Id, "OWNER_CANCELLED", 100,
            """{"reasonCode":"EXECUTION_CANCELLED"}""", CancellationToken.None);
        operation = await operations.TransitionAsync(
            tenantId, operation.Id, operation.Version, OperationStatuses.CancelRequested,
            safeErrorCode: safeErrorCode, cancellationToken: CancellationToken.None);
        await operations.TransitionAsync(
            tenantId, operation.Id, operation.Version, OperationStatuses.Cancelled,
            safeErrorCode: safeErrorCode, cancellationToken: CancellationToken.None);
    }

    private IReplayOwnerHandler FindHandler(string ownerModule, string operationType)
        => handlers.SingleOrDefault(x =>
               string.Equals(Normalize(x.OwnerModule), ownerModule, StringComparison.Ordinal)
               && string.Equals(Normalize(x.OperationType), operationType, StringComparison.Ordinal))
           ?? throw new DurableOperationRuleException(
               "OPERATIONS.REPLAY.HANDLER_NOT_FOUND", "No neutral owner replay handler is registered.");

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DurableOperationRuleException(
                "OPERATIONS.REPLAY.INVALID", "Replay owner and operation type are required.");
        return value.Trim().ToUpperInvariant();
    }
}
