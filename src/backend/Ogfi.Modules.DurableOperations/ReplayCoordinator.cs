using Ogfi.BuildingBlocks.Messaging.Contracts;

namespace Ogfi.Modules.DurableOperations;

public sealed class ReplayCoordinator(
    DurableOperationService operations,
    IEnumerable<IReplayOwnerHandler> handlers)
{
    private const int MaximumReplayAttempts = 3;

    public Task<Operation> RequestReplayForFailureAsync(
        Guid tenantId,
        Guid failureId,
        string replayRequestKey,
        Guid? requestedByUserId,
        Guid? requestedByMembershipId,
        CancellationToken cancellationToken = default)
        => operations.CreateOrReuseReplayForFailureAsync(
            tenantId, failureId, replayRequestKey, requestedByUserId,
            requestedByMembershipId, cancellationToken);

    public Task<Operation> RequestCancellationAsync(
        Guid tenantId, Guid operationId, CancellationToken cancellationToken = default)
        => operations.RequestCancellationAsync(tenantId, operationId, cancellationToken);

    public async Task<Operation> ExecuteQueuedReplayOperationAsync(
        Guid tenantId,
        Guid operationId,
        string workerCode,
        CancellationToken cancellationToken = default)
    {
        var operation = await operations.GetOperationAsync(tenantId, operationId, cancellationToken);
        if (operation.Status == OperationStatuses.CancelRequested)
            return await operations.ObserveRequestedCancellationAsync(
                tenantId, operation.Id, CancellationToken.None);
        var handler = FindHandler(operation.OwnerModule, operation.OperationType);
        if (operation.Status == OperationStatuses.Queued)
        {
            try
            {
                operation = await operations.TransitionAsync(
                    tenantId, operation.Id, operation.Version, OperationStatuses.Running,
                    cancellationToken: cancellationToken);
            }
            catch (DurableOperationRuleException exception)
                when (exception.Code == "OPERATIONS.TRANSITION.VERSION_CONFLICT")
            {
                operation = await operations.GetOperationAsync(tenantId, operation.Id, CancellationToken.None);
                if (operation.Status == OperationStatuses.CancelRequested)
                    return await operations.ObserveRequestedCancellationAsync(
                        tenantId, operation.Id, CancellationToken.None);
                throw;
            }
        }
        else if (operation.Status != OperationStatuses.Running)
        {
            throw new DurableOperationRuleException(
                "OPERATIONS.REPLAY.NOT_EXECUTABLE",
                "Only queued, running, or cancellation-requested operations can execute.");
        }

        OperationAttempt attempt;
        try
        {
            attempt = await operations.StartAttemptAsync(
                tenantId, operation.Id, workerCode,
                """{"stage":"OWNER_DISPATCH","status":"RUNNING"}""",
                cancellationToken: cancellationToken);
        }
        catch (DurableOperationRuleException exception)
            when (exception.Code == "OPERATIONS.ATTEMPT.MAXIMUM_EXCEEDED")
        {
            operation = await operations.GetOperationAsync(tenantId, operation.Id, CancellationToken.None);
            return await operations.TransitionAsync(
                tenantId, operation.Id, operation.Version, OperationStatuses.Failed,
                safeErrorCode: exception.Code, safeDetailJson: "{}",
                cancellationToken: CancellationToken.None);
        }

        try
        {
            await operations.AddNextCheckpointAsync(
                tenantId, operation.Id, "OWNER_DISPATCH", 10,
                $$"""{"stage":"OWNER_DISPATCH","retryCount":{{attempt.AttemptNumber}}}""",
                cancellationToken);
        }
        catch (DurableOperationRuleException exception)
            when (exception.Code == "OPERATIONS.CHECKPOINT.OPERATION_NOT_RUNNING")
        {
            operation = await operations.GetOperationAsync(tenantId, operation.Id, CancellationToken.None);
            if (operation.Status == OperationStatuses.CancelRequested)
                return await operations.ObserveRequestedCancellationAsync(
                    tenantId, operation.Id, CancellationToken.None);
            throw;
        }

        ReplayDispatchResult result;
        try
        {
            result = await handler.ReplayAsync(
                new ReplayDispatchCommand(
                    tenantId, operation.Id, operation.OperationType, operation.OwnerModule,
                    operation.OriginalSourceEventId, operation.OriginalCausationId,
                    operation.CorrelationId, operation.LegalEntityId, operation.OutletId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Host interruption is not a business cancellation. The RUNNING Attempt and
            // its lease remain recoverable and will be abandoned only after lease expiry.
            throw;
        }
        catch (Exception)
        {
            operation = await ObserveCancellationIfRequestedAsync(tenantId, operation.Id);
            if (operation.Status == OperationStatuses.Cancelled) return operation;
            return await CompleteFailedAttemptAsync(
                tenantId, operation, attempt,
                "OPERATIONS.REPLAY.OWNER_HANDLER_FAILED", "{}", retryable: true);
        }

        operation = await ObserveCancellationIfRequestedAsync(tenantId, operation.Id);
        if (operation.Status == OperationStatuses.Cancelled) return operation;
        if (result.Succeeded)
        {
            try
            {
                return await operations.CompleteReplaySuccessAsync(
                    tenantId, operation.Id, attempt.Id, attempt.LeaseToken,
                    result.ResultReferenceType, result.ResultReferenceId,
                    result.SafeDetailJson, cancellationToken);
            }
            catch (DurableOperationRuleException exception)
                when (exception.Code == "OPERATIONS.CANCELLATION.PENDING")
            {
                return await operations.ObserveRequestedCancellationAsync(
                    tenantId, operation.Id, CancellationToken.None);
            }
        }

        return await CompleteFailedAttemptAsync(
            tenantId, operation, attempt,
            result.SafeErrorCode ?? "OPERATIONS.REPLAY.OWNER_REJECTED",
            result.SafeDetailJson, result.Retryable);
    }

    private async Task<Operation> ObserveCancellationIfRequestedAsync(Guid tenantId, Guid operationId)
    {
        var current = await operations.GetOperationAsync(tenantId, operationId, CancellationToken.None);
        return current.Status == OperationStatuses.CancelRequested
            ? await operations.ObserveRequestedCancellationAsync(
                tenantId, operationId, CancellationToken.None)
            : current;
    }

    private async Task<Operation> CompleteFailedAttemptAsync(
        Guid tenantId,
        Operation operation,
        OperationAttempt attempt,
        string safeErrorCode,
        string safeDetailJson,
        bool retryable)
    {
        await operations.CompleteAttemptAsync(
            tenantId, attempt.Id, attempt.LeaseToken, succeeded: false,
            safeErrorCode, safeDetailJson, CancellationToken.None);
        var exhausted = !retryable || attempt.AttemptNumber >= MaximumReplayAttempts;
        await operations.AddNextCheckpointAsync(
            tenantId, operation.Id, exhausted ? "OWNER_FAILED" : "OWNER_RETRY_PENDING",
            exhausted ? 100 : 50, safeDetailJson, CancellationToken.None);
        if (!exhausted)
            return await operations.GetOperationAsync(tenantId, operation.Id, CancellationToken.None);
        operation = await operations.GetOperationAsync(tenantId, operation.Id, CancellationToken.None);
        return await operations.TransitionAsync(
            tenantId, operation.Id, operation.Version, OperationStatuses.Failed,
            safeErrorCode: safeErrorCode, safeDetailJson: safeDetailJson,
            cancellationToken: CancellationToken.None);
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
