using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.Modules.DurableOperations.Persistence;

namespace Ogfi.Modules.DurableOperations;

public sealed record CreateReplayOperationRequest(
    Guid TenantId,
    string ReplayRequestKey,
    string OperationType,
    string OwnerModule,
    Guid OriginalSourceEventId,
    string? OriginalCausationId,
    string CorrelationId,
    Guid? LegalEntityId = null,
    Guid? OutletId = null,
    Guid? RequestedByUserId = null,
    Guid? RequestedByMembershipId = null,
    bool Replayable = true);

public sealed record WorkerHeartbeatUpdate(
    Guid TenantId,
    string WorkerCode,
    DateTimeOffset LastIterationStartedAtUtc,
    DateTimeOffset? LastSucceededAtUtc,
    DateTimeOffset? LastFailedAtUtc,
    Guid? CurrentOrLastSourceId,
    int PendingCount,
    int RetryPendingCount,
    int TerminalFailureCount,
    DateTimeOffset? OldestPendingAtUtc,
    string? LastSafeErrorCode);

public sealed record ProcessingFailureUpdate(
    Guid TenantId,
    string OwnerModule,
    string ProcessorCode,
    string FailureClassification,
    Guid OriginalSourceEventId,
    string? OriginalCausationId,
    string CorrelationId,
    string ResourceType,
    Guid ResourceId,
    string SafeErrorCode,
    string SafeDetailJson,
    string State,
    bool Replayable,
    Guid? LegalEntityId = null,
    Guid? OutletId = null,
    Guid? CurrentOperationId = null,
    Guid? FailureId = null);

public sealed class DurableOperationService(DurableOperationsDbContext dbContext, TimeProvider timeProvider)
{
    private const int MaximumPageSize = 100;
    private const int MaximumConcurrencyRetries = 5;
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedTransitions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [OperationStatuses.Queued] = [OperationStatuses.Running, OperationStatuses.CancelRequested],
            [OperationStatuses.Running] = [OperationStatuses.Succeeded, OperationStatuses.Failed, OperationStatuses.CancelRequested],
            [OperationStatuses.CancelRequested] = [OperationStatuses.Cancelled]
        };

    public async Task<Operation> CreateOrReuseReplayOperationAsync(
        CreateReplayOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateReplayRequest(request);
        var replayRequestKey = Required(request.ReplayRequestKey, 128, "replay request key");
        var ownerModule = NormalizeCode(request.OwnerModule, 60, "owner module");
        var operationType = NormalizeCode(request.OperationType, 100, "operation type");
        var causationId = Optional(request.OriginalCausationId, 128, "original causation identifier");
        var correlationId = Required(request.CorrelationId, 64, "correlation identifier");
        var existing = await FindReplayOperationAsync(request.TenantId, replayRequestKey, cancellationToken);
        if (existing is not null)
        {
            EnsureEquivalent(existing, request, ownerModule, operationType, causationId, correlationId);
            return existing;
        }

        var operation = new Operation
        {
            Id = Guid.NewGuid(), TenantId = request.TenantId, ReplayRequestKey = replayRequestKey,
            OperationType = operationType, OwnerModule = ownerModule, Status = OperationStatuses.Queued,
            OriginalSourceEventId = request.OriginalSourceEventId, OriginalCausationId = causationId,
            CorrelationId = correlationId, LegalEntityId = request.LegalEntityId, OutletId = request.OutletId,
            RequestedByUserId = request.RequestedByUserId, RequestedByMembershipId = request.RequestedByMembershipId,
            CreatedAtUtc = timeProvider.GetUtcNow(), Replayable = request.Replayable, Version = 1
        };
        dbContext.Operations.Add(operation);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return operation;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            existing = await FindReplayOperationAsync(request.TenantId, replayRequestKey, cancellationToken);
            if (existing is null) throw;
            EnsureEquivalent(existing, request, ownerModule, operationType, causationId, correlationId);
            return existing;
        }
    }

    public async Task<Operation> GetOperationAsync(Guid tenantId, Guid operationId, CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        return await dbContext.Operations.AsNoTracking().SingleOrDefaultAsync(
                   x => x.TenantId == tenantId && x.Id == operationId, cancellationToken)
               ?? throw new DurableOperationRuleException("OPERATIONS.NOT_FOUND", "Operation was not found.");
    }

    public async Task<ProcessingFailureProjection> GetFailureAsync(
        Guid tenantId, Guid failureId, CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        return await dbContext.ProcessingFailures.AsNoTracking().SingleOrDefaultAsync(
                   x => x.TenantId == tenantId && x.Id == failureId, cancellationToken)
               ?? throw new DurableOperationRuleException("OPERATIONS.FAILURE.NOT_FOUND", "Processing failure was not found.");
    }

    public async Task<Operation> TransitionAsync(
        Guid tenantId, Guid operationId, long expectedVersion, string targetStatus,
        string? resultReferenceType = null, Guid? resultReferenceId = null,
        string? safeErrorCode = null, string? safeDetailJson = null,
        CancellationToken cancellationToken = default)
    {
        var operation = await dbContext.Operations.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == operationId, cancellationToken)
            ?? throw new DurableOperationRuleException("OPERATIONS.NOT_FOUND", "Operation was not found.");
        if (operation.Version != expectedVersion)
            throw new DurableOperationRuleException("OPERATIONS.TRANSITION.VERSION_CONFLICT", "Operation version is stale.");
        var normalizedTarget = NormalizeCode(targetStatus, 24, "target status");
        if (!AllowedTransitions.TryGetValue(operation.Status, out var targets) || !targets.Contains(normalizedTarget))
            throw new DurableOperationRuleException("OPERATIONS.TRANSITION.INVALID", $"Transition {operation.Status} -> {normalizedTarget} is not allowed.");

        var now = timeProvider.GetUtcNow();
        operation.Status = normalizedTarget;
        operation.Version++;
        if (normalizedTarget == OperationStatuses.Running) operation.StartedAtUtc ??= now;
        if (normalizedTarget == OperationStatuses.CancelRequested) operation.CancelRequestedAtUtc = now;
        if (normalizedTarget is OperationStatuses.Succeeded or OperationStatuses.Failed or OperationStatuses.Cancelled)
            operation.CompletedAtUtc = now;
        operation.ResultReferenceType = Optional(resultReferenceType, 100, "result reference type");
        operation.ResultReferenceId = resultReferenceId;
        operation.SafeErrorCode = Optional(safeErrorCode, 120, "safe error code");
        operation.SafeDetailJson = safeDetailJson is null ? null : SafeDetailPolicy.Normalize(safeDetailJson);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return operation;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DurableOperationRuleException("OPERATIONS.TRANSITION.VERSION_CONFLICT", "Operation was concurrently changed.");
        }
    }

    public async Task<OperationAttempt> StartAttemptAsync(
        Guid tenantId, Guid operationId, string workerCode, string safeDetailJson = "{}",
        CancellationToken cancellationToken = default)
    {
        var normalizedWorker = NormalizeCode(workerCode, 100, "worker code");
        var normalizedDetail = SafeDetailPolicy.Normalize(safeDetailJson);
        for (var retry = 0; retry < MaximumConcurrencyRetries; retry++)
        {
            dbContext.ChangeTracker.Clear();
            var operation = await dbContext.Operations.AsNoTracking().SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.Id == operationId, cancellationToken)
                ?? throw new DurableOperationRuleException("OPERATIONS.NOT_FOUND", "Operation was not found.");
            if (operation.Status != OperationStatuses.Running)
                throw new DurableOperationRuleException("OPERATIONS.ATTEMPT.OPERATION_NOT_RUNNING", "Attempts require a running operation.");
            if (await dbContext.OperationAttempts.AsNoTracking().AnyAsync(
                    x => x.TenantId == tenantId && x.OperationId == operationId
                         && x.Status == OperationAttemptStatuses.Running, cancellationToken))
                throw new DurableOperationRuleException("OPERATIONS.ATTEMPT.ACTIVE_EXISTS", "A running attempt already exists.");
            var nextAttempt = (await dbContext.OperationAttempts.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OperationId == operationId)
                .MaxAsync(x => (int?)x.AttemptNumber, cancellationToken) ?? 0) + 1;
            var attempt = new OperationAttempt
            {
                Id = Guid.NewGuid(), TenantId = tenantId, OperationId = operationId, AttemptNumber = nextAttempt,
                Status = OperationAttemptStatuses.Running, WorkerCode = normalizedWorker,
                StartedAtUtc = timeProvider.GetUtcNow(), SafeDetailJson = normalizedDetail,
                OriginalSourceEventId = operation.OriginalSourceEventId,
                OriginalCausationId = operation.OriginalCausationId, CorrelationId = operation.CorrelationId, Version = 1
            };
            dbContext.OperationAttempts.Add(attempt);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return attempt;
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                dbContext.ChangeTracker.Clear();
                if (await dbContext.OperationAttempts.AsNoTracking().AnyAsync(
                        x => x.TenantId == tenantId && x.OperationId == operationId
                             && x.Status == OperationAttemptStatuses.Running, cancellationToken))
                    throw new DurableOperationRuleException("OPERATIONS.ATTEMPT.ACTIVE_EXISTS", "A running attempt already exists.");
            }
        }
        throw new DurableOperationRuleException("OPERATIONS.ATTEMPT.SEQUENCE_CONFLICT", "Attempt sequence could not be allocated after bounded retries.");
    }

    public async Task<OperationAttempt> CompleteAttemptAsync(
        Guid tenantId, Guid attemptId, bool succeeded, string? safeErrorCode = null,
        string safeDetailJson = "{}", CancellationToken cancellationToken = default)
    {
        var attempt = await dbContext.OperationAttempts.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == attemptId, cancellationToken)
            ?? throw new DurableOperationRuleException("OPERATIONS.ATTEMPT.NOT_FOUND", "Operation attempt was not found.");
        if (attempt.Status != OperationAttemptStatuses.Running)
            throw new DurableOperationRuleException("OPERATIONS.ATTEMPT.TERMINAL", "Completed attempts cannot transition again.");
        attempt.Status = succeeded ? OperationAttemptStatuses.Succeeded : OperationAttemptStatuses.Failed;
        attempt.CompletedAtUtc = timeProvider.GetUtcNow();
        attempt.SafeErrorCode = Optional(safeErrorCode, 120, "safe error code");
        attempt.SafeDetailJson = SafeDetailPolicy.Normalize(safeDetailJson);
        attempt.Version++;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return attempt;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DurableOperationRuleException("OPERATIONS.ATTEMPT.VERSION_CONFLICT", "Attempt was concurrently completed.");
        }
    }

    public async Task<OperationCheckpoint> AddCheckpointAsync(
        Guid tenantId, Guid operationId, int sequence, string checkpointKey, int progressPercentage,
        string safeDetailJson = "{}", CancellationToken cancellationToken = default)
    {
        if (progressPercentage is < 0 or > 100)
            throw new DurableOperationRuleException("OPERATIONS.CHECKPOINT.PROGRESS_INVALID", "Progress must be between 0 and 100.");
        var operation = await dbContext.Operations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == operationId, cancellationToken)
            ?? throw new DurableOperationRuleException("OPERATIONS.NOT_FOUND", "Operation was not found.");
        if (operation.Status != OperationStatuses.Running)
            throw new DurableOperationRuleException("OPERATIONS.CHECKPOINT.OPERATION_NOT_RUNNING", "Checkpoints require a running operation.");
        var previous = await dbContext.OperationCheckpoints.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OperationId == operationId)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(cancellationToken);
        var expectedSequence = (previous?.Sequence ?? 0) + 1;
        if (sequence != expectedSequence)
            throw new DurableOperationRuleException("OPERATIONS.CHECKPOINT.SEQUENCE_INVALID", $"Expected checkpoint sequence {expectedSequence}.");
        if (previous is not null && progressPercentage < previous.ProgressPercentage)
            throw new DurableOperationRuleException("OPERATIONS.CHECKPOINT.PROGRESS_REGRESSION", "Checkpoint progress cannot decrease.");
        var checkpoint = new OperationCheckpoint
        {
            Id = Guid.NewGuid(), TenantId = tenantId, OperationId = operationId, Sequence = sequence,
            CheckpointKey = NormalizeCode(checkpointKey, 100, "checkpoint key"), ProgressPercentage = progressPercentage,
            SafeDetailJson = SafeDetailPolicy.Normalize(safeDetailJson), OccurredAtUtc = timeProvider.GetUtcNow()
        };
        dbContext.OperationCheckpoints.Add(checkpoint);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return checkpoint;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new DurableOperationRuleException("OPERATIONS.CHECKPOINT.SEQUENCE_CONFLICT", "Checkpoint sequence was concurrently allocated.");
        }
    }

    public async Task<OperationCheckpoint> AddNextCheckpointAsync(
        Guid tenantId, Guid operationId, string checkpointKey, int progressPercentage,
        string safeDetailJson = "{}", CancellationToken cancellationToken = default)
    {
        var previous = await dbContext.OperationCheckpoints.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OperationId == operationId)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(cancellationToken);
        return await AddCheckpointAsync(tenantId, operationId, (previous?.Sequence ?? 0) + 1, checkpointKey,
            Math.Max(progressPercentage, previous?.ProgressPercentage ?? 0), safeDetailJson, cancellationToken);
    }

    public async Task<WorkerHeartbeat> UpsertHeartbeatAsync(
        WorkerHeartbeatUpdate update, CancellationToken cancellationToken = default)
    {
        ValidateTenant(update.TenantId);
        if (update.PendingCount < 0 || update.RetryPendingCount < 0 || update.TerminalFailureCount < 0)
            throw new DurableOperationRuleException("OPERATIONS.HEARTBEAT.COUNT_INVALID", "Heartbeat counts cannot be negative.");
        var workerCode = NormalizeCode(update.WorkerCode, 100, "worker code");
        var safeErrorCode = Optional(update.LastSafeErrorCode, 120, "last safe error code");
        var updatedAt = timeProvider.GetUtcNow();
        var rows = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO operations.worker_heartbeats
              ("Id","TenantId","WorkerCode","LastIterationStartedAtUtc","LastSucceededAtUtc","LastFailedAtUtc",
               "CurrentOrLastSourceId","PendingCount","RetryPendingCount","TerminalFailureCount","OldestPendingAtUtc",
               "LastSafeErrorCode","UpdatedAtUtc")
            VALUES
              ({Guid.NewGuid()},{update.TenantId},{workerCode},{update.LastIterationStartedAtUtc},{update.LastSucceededAtUtc},
               {update.LastFailedAtUtc},{update.CurrentOrLastSourceId},{update.PendingCount},{update.RetryPendingCount},
               {update.TerminalFailureCount},{update.OldestPendingAtUtc},{safeErrorCode},{updatedAt})
            ON CONFLICT ("TenantId","WorkerCode") DO UPDATE SET
              "LastIterationStartedAtUtc"=EXCLUDED."LastIterationStartedAtUtc",
              "LastSucceededAtUtc"=EXCLUDED."LastSucceededAtUtc", "LastFailedAtUtc"=EXCLUDED."LastFailedAtUtc",
              "CurrentOrLastSourceId"=EXCLUDED."CurrentOrLastSourceId", "PendingCount"=EXCLUDED."PendingCount",
              "RetryPendingCount"=EXCLUDED."RetryPendingCount", "TerminalFailureCount"=EXCLUDED."TerminalFailureCount",
              "OldestPendingAtUtc"=EXCLUDED."OldestPendingAtUtc", "LastSafeErrorCode"=EXCLUDED."LastSafeErrorCode",
              "UpdatedAtUtc"=EXCLUDED."UpdatedAtUtc"
            WHERE EXCLUDED."LastIterationStartedAtUtc" > operations.worker_heartbeats."LastIterationStartedAtUtc";
            """, cancellationToken);
        if (rows == 0)
            throw new DurableOperationRuleException("OPERATIONS.HEARTBEAT.STALE", "An older or duplicate heartbeat observation cannot replace newer state.");
        dbContext.ChangeTracker.Clear();
        return await dbContext.WorkerHeartbeats.AsNoTracking().SingleAsync(
            x => x.TenantId == update.TenantId && x.WorkerCode == workerCode, cancellationToken);
    }

    public async Task<ProcessingFailureProjection> RecordFailureAsync(
        ProcessingFailureUpdate update, CancellationToken cancellationToken = default)
    {
        var normalized = await NormalizeFailureUpdateAsync(update, cancellationToken);
        for (var retry = 0; retry < MaximumConcurrencyRetries; retry++)
        {
            dbContext.ChangeTracker.Clear();
            var failure = update.FailureId is { } failureId
                ? await dbContext.ProcessingFailures.SingleOrDefaultAsync(
                    x => x.TenantId == update.TenantId && x.Id == failureId, cancellationToken)
                : await dbContext.ProcessingFailures.SingleOrDefaultAsync(
                    x => x.TenantId == update.TenantId && x.ProcessorCode == normalized.ProcessorCode
                         && x.OriginalSourceEventId == update.OriginalSourceEventId, cancellationToken);
            if (update.FailureId is not null && failure is null)
                throw new DurableOperationRuleException("OPERATIONS.FAILURE.NOT_FOUND", "Processing failure was not found.");
            if (failure is null)
            {
                failure = new ProcessingFailureProjection
                {
                    Id = Guid.NewGuid(), TenantId = update.TenantId, OwnerModule = normalized.OwnerModule,
                    ProcessorCode = normalized.ProcessorCode, FailureClassification = normalized.Classification,
                    OriginalSourceEventId = update.OriginalSourceEventId, OriginalCausationId = normalized.CausationId,
                    CorrelationId = normalized.CorrelationId, ResourceType = normalized.ResourceType,
                    ResourceId = update.ResourceId, LegalEntityId = update.LegalEntityId, OutletId = update.OutletId,
                    FirstFailedAtUtc = normalized.Now, LastFailedAtUtc = normalized.Now, AttemptCount = 1,
                    SafeErrorCode = normalized.SafeErrorCode, SafeDetailJson = normalized.SafeDetailJson,
                    Replayable = normalized.Replayable, CurrentOperationId = normalized.CurrentOperationId,
                    State = normalized.State, Version = 1
                };
                dbContext.ProcessingFailures.Add(failure);
            }
            else
            {
                EnsureFailureLineage(failure, update, normalized);
                if (!IsFailureTransitionAllowed(failure.State, normalized.State, normalized.TerminalClassification))
                    throw new DurableOperationRuleException("OPERATIONS.FAILURE.STATE_TRANSITION_INVALID", $"Failure state {failure.State} cannot transition to {normalized.State}.");
                failure.FailureClassification = normalized.Classification;
                failure.AttemptCount++;
                failure.LastFailedAtUtc = normalized.Now;
                failure.SafeErrorCode = normalized.SafeErrorCode;
                failure.SafeDetailJson = normalized.SafeDetailJson;
                failure.Replayable = normalized.Replayable;
                failure.CurrentOperationId = normalized.CurrentOperationId;
                failure.State = normalized.State;
                failure.Version++;
            }
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return failure;
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception)) { }
            catch (DbUpdateConcurrencyException) { }
        }
        throw new DurableOperationRuleException("OPERATIONS.FAILURE.CONCURRENCY_CONFLICT", "Failure projection could not be updated after bounded retries.");
    }

    public async Task LinkFailureToOperationAsync(
        Guid tenantId, Guid failureId, Guid operationId, CancellationToken cancellationToken = default)
    {
        var operation = await GetOperationAsync(tenantId, operationId, cancellationToken);
        for (var retry = 0; retry < MaximumConcurrencyRetries; retry++)
        {
            dbContext.ChangeTracker.Clear();
            var failure = await dbContext.ProcessingFailures.SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.Id == failureId, cancellationToken)
                ?? throw new DurableOperationRuleException("OPERATIONS.FAILURE.NOT_FOUND", "Processing failure was not found.");
            EnsureOperationMatchesFailure(operation, failure);
            if (!failure.Replayable || ProcessingFailureStates.IsTerminal(failure.State)
                                    || ProcessingFailureClassifications.IsTerminalInvalid(failure.FailureClassification))
                throw new DurableOperationRuleException("OPERATIONS.REPLAY.NOT_ALLOWED", "Persisted failure is terminal or non-replayable.");
            if (failure.CurrentOperationId == operationId) return;
            failure.CurrentOperationId = operationId;
            failure.Version++;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) { }
        }
        throw new DurableOperationRuleException("OPERATIONS.FAILURE.CONCURRENCY_CONFLICT", "Failure linkage could not be updated after bounded retries.");
    }

    public async Task<IReadOnlyList<Operation>> QueryOperationsAsync(
        Guid tenantId, int limit, CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        if (limit is < 1 or > MaximumPageSize)
            throw new DurableOperationRuleException("OPERATIONS.QUERY.LIMIT_INVALID", $"Limit must be between 1 and {MaximumPageSize}.");
        return await dbContext.Operations.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(limit).ToListAsync(cancellationToken);
    }

    public Task<int> CountAttemptsAsync(Guid tenantId, Guid operationId, CancellationToken cancellationToken = default)
        => dbContext.OperationAttempts.AsNoTracking().CountAsync(
            x => x.TenantId == tenantId && x.OperationId == operationId, cancellationToken);

    private Task<Operation?> FindReplayOperationAsync(Guid tenantId, string replayRequestKey, CancellationToken cancellationToken)
        => dbContext.Operations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.ReplayRequestKey == replayRequestKey, cancellationToken);

    private async Task<NormalizedFailureUpdate> NormalizeFailureUpdateAsync(
        ProcessingFailureUpdate update, CancellationToken cancellationToken)
    {
        ValidateTenant(update.TenantId);
        if (update.OriginalSourceEventId == Guid.Empty || update.ResourceId == Guid.Empty)
            throw new DurableOperationRuleException("OPERATIONS.FAILURE.INVALID", "Source and resource identifiers are required.");
        var ownerModule = NormalizeCode(update.OwnerModule, 60, "owner module");
        var processorCode = NormalizeCode(update.ProcessorCode, 100, "processor code");
        var classification = NormalizeCode(update.FailureClassification, 40, "failure classification");
        if (!ProcessingFailureClassifications.IsApproved(classification))
            throw new DurableOperationRuleException("OPERATIONS.FAILURE.CLASSIFICATION_INVALID", "Failure classification is not approved.");
        var requestedState = NormalizeCode(update.State, 24, "failure state");
        if (!ProcessingFailureStateSet.Contains(requestedState))
            throw new DurableOperationRuleException("OPERATIONS.FAILURE.STATE_INVALID", "Processing failure state is not approved.");
        var terminalClassification = ProcessingFailureClassifications.IsTerminalInvalid(classification);
        var state = terminalClassification ? ProcessingFailureStates.TerminalRejected : requestedState;
        var replayable = !terminalClassification && !ProcessingFailureStates.IsTerminal(state) && update.Replayable;
        var currentOperationId = replayable ? update.CurrentOperationId : null;
        var causationId = Optional(update.OriginalCausationId, 128, "original causation identifier");
        var correlationId = Required(update.CorrelationId, 64, "correlation identifier");
        if (currentOperationId is { } operationId)
        {
            var operation = await GetOperationAsync(update.TenantId, operationId, cancellationToken);
            EnsureOperationMatchesFailure(operation, ownerModule, processorCode, update.OriginalSourceEventId, causationId, correlationId);
        }
        return new NormalizedFailureUpdate(
            ownerModule, processorCode, classification, state, replayable, terminalClassification,
            causationId, correlationId, NormalizeCode(update.ResourceType, 100, "resource type"),
            Required(update.SafeErrorCode, 120, "safe error code"), SafeDetailPolicy.Normalize(update.SafeDetailJson),
            currentOperationId, timeProvider.GetUtcNow());
    }

    private static bool IsFailureTransitionAllowed(string current, string target, bool terminalClassification)
    {
        if (current == ProcessingFailureStates.TerminalRejected)
            return target == ProcessingFailureStates.TerminalRejected && terminalClassification;
        if (current == ProcessingFailureStates.Completed) return target == ProcessingFailureStates.Completed;
        if (terminalClassification) return target == ProcessingFailureStates.TerminalRejected;
        if (target == ProcessingFailureStates.Pending) return current == ProcessingFailureStates.Pending;
        if (current == ProcessingFailureStates.Recovered)
            return target is ProcessingFailureStates.Recovered or ProcessingFailureStates.Completed;
        return current switch
        {
            ProcessingFailureStates.Pending => true,
            ProcessingFailureStates.RetryPending => target is ProcessingFailureStates.RetryPending
                or ProcessingFailureStates.BusinessFailed or ProcessingFailureStates.Stalled
                or ProcessingFailureStates.Recovered or ProcessingFailureStates.Completed,
            ProcessingFailureStates.BusinessFailed => target is ProcessingFailureStates.BusinessFailed
                or ProcessingFailureStates.Recovered or ProcessingFailureStates.Completed,
            ProcessingFailureStates.Stalled => target is ProcessingFailureStates.Stalled
                or ProcessingFailureStates.RetryPending or ProcessingFailureStates.Recovered or ProcessingFailureStates.Completed,
            _ => false
        };
    }

    private static void EnsureEquivalent(Operation existing, CreateReplayOperationRequest request,
        string ownerModule, string operationType, string? causationId, string correlationId)
    {
        if (existing.OwnerModule != ownerModule || existing.OperationType != operationType
            || existing.OriginalSourceEventId != request.OriginalSourceEventId
            || existing.OriginalCausationId != causationId || existing.CorrelationId != correlationId
            || existing.LegalEntityId != request.LegalEntityId || existing.OutletId != request.OutletId
            || existing.RequestedByUserId != request.RequestedByUserId
            || existing.RequestedByMembershipId != request.RequestedByMembershipId
            || existing.Replayable != request.Replayable)
            throw new DurableOperationRuleException("OPERATIONS.REPLAY.IDENTITY_CONFLICT", "Replay request key is already associated with different metadata.");
    }

    private static void EnsureFailureLineage(ProcessingFailureProjection failure,
        ProcessingFailureUpdate update, NormalizedFailureUpdate normalized)
    {
        if (failure.OwnerModule != normalized.OwnerModule || failure.ProcessorCode != normalized.ProcessorCode
            || failure.OriginalSourceEventId != update.OriginalSourceEventId
            || failure.OriginalCausationId != normalized.CausationId || failure.CorrelationId != normalized.CorrelationId
            || failure.ResourceType != normalized.ResourceType || failure.ResourceId != update.ResourceId
            || failure.LegalEntityId != update.LegalEntityId || failure.OutletId != update.OutletId)
            throw new DurableOperationRuleException("OPERATIONS.FAILURE.IDENTITY_CONFLICT", "Failure lineage is immutable.");
    }

    private static void EnsureOperationMatchesFailure(Operation operation, ProcessingFailureProjection failure)
        => EnsureOperationMatchesFailure(operation, failure.OwnerModule, failure.ProcessorCode,
            failure.OriginalSourceEventId, failure.OriginalCausationId, failure.CorrelationId);

    private static void EnsureOperationMatchesFailure(Operation operation, string ownerModule, string processorCode,
        Guid sourceEventId, string? causationId, string correlationId)
    {
        if (operation.OwnerModule != ownerModule || operation.OperationType != processorCode
            || operation.OriginalSourceEventId != sourceEventId
            || operation.OriginalCausationId != causationId || operation.CorrelationId != correlationId)
            throw new DurableOperationRuleException("OPERATIONS.FAILURE.OPERATION_MISMATCH", "Operation does not match persisted failure lineage.");
    }

    private static void ValidateReplayRequest(CreateReplayOperationRequest request)
    {
        ValidateTenant(request.TenantId);
        if (request.OriginalSourceEventId == Guid.Empty)
            throw new DurableOperationRuleException("OPERATIONS.REPLAY.INVALID", "Original source event identity is required.");
    }

    private static void ValidateTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new DurableOperationRuleException("OPERATIONS.TENANT.INVALID", "Tenant identifier is required.");
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string Required(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximumLength)
            throw new DurableOperationRuleException("OPERATIONS.VALUE.INVALID", $"{field} is required and bounded to {maximumLength} characters.");
        return value.Trim();
    }

    private static string NormalizeCode(string? value, int maximumLength, string field)
        => Required(value, maximumLength, field).ToUpperInvariant();

    private static string? Optional(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim().Length > maximumLength)
            throw new DurableOperationRuleException("OPERATIONS.VALUE.INVALID", $"{field} is bounded to {maximumLength} characters.");
        return value.Trim();
    }

    private static readonly HashSet<string> ProcessingFailureStateSet =
    [
        ProcessingFailureStates.Pending, ProcessingFailureStates.RetryPending, ProcessingFailureStates.BusinessFailed,
        ProcessingFailureStates.TerminalRejected, ProcessingFailureStates.Stalled, ProcessingFailureStates.Recovered,
        ProcessingFailureStates.Completed
    ];

    private sealed record NormalizedFailureUpdate(
        string OwnerModule, string ProcessorCode, string Classification, string State, bool Replayable,
        bool TerminalClassification, string? CausationId, string CorrelationId, string ResourceType,
        string SafeErrorCode, string SafeDetailJson, Guid? CurrentOperationId, DateTimeOffset Now);
}

internal static class SafeDetailPolicy
{
    private const int MaximumBytes = 8_192;
    private const int MaximumStringLength = 1_000;
    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "status", "stage", "reasoncode", "retrycount", "progress", "result", "items", "workerstate"
    };

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > MaximumBytes)
            throw new DurableOperationRuleException("OPERATIONS.SAFE_DETAIL.TOO_LARGE", "Safe detail is required and bounded to 8192 UTF-8 bytes.");
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new DurableOperationRuleException("OPERATIONS.SAFE_DETAIL.INVALID", "Safe detail must be a JSON object.");
            Validate(document.RootElement);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            throw new DurableOperationRuleException("OPERATIONS.SAFE_DETAIL.INVALID", "Safe detail must be valid bounded JSON.");
        }
    }

    private static void Validate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = string.Concat(property.Name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
                if (!AllowedFields.Contains(normalized))
                    throw new DurableOperationRuleException("OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED", $"Safe detail field '{property.Name}' is not allowed.");
                Validate(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Validate(item);
        }
        else if (element.ValueKind == JsonValueKind.String && element.GetString()?.Length > MaximumStringLength)
        {
            throw new DurableOperationRuleException("OPERATIONS.SAFE_DETAIL.TOO_LARGE", "Safe detail string exceeds 1000 characters.");
        }
    }
}
