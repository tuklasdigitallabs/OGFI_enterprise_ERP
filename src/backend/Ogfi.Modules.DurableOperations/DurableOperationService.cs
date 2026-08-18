using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.Modules.DurableOperations.Persistence;

namespace Ogfi.Modules.DurableOperations;

public sealed record CreateReplayOperationRequest(
    Guid TenantId,
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
    Guid? CurrentOperationId = null);

public sealed class DurableOperationService(DurableOperationsDbContext dbContext, TimeProvider timeProvider)
{
    private const int MaximumPageSize = 100;
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
        var ownerModule = NormalizeCode(request.OwnerModule, 60, "owner module");
        var operationType = NormalizeCode(request.OperationType, 100, "operation type");
        var causationId = Optional(request.OriginalCausationId, 128, "original causation identifier");
        var correlationId = Required(request.CorrelationId, 64, "correlation identifier");

        var existing = await FindReplayOperationAsync(
            request.TenantId, ownerModule, operationType, request.OriginalSourceEventId, cancellationToken);
        if (existing is not null)
        {
            EnsureEquivalent(existing, request, causationId, correlationId);
            return existing;
        }

        var operation = new Operation
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            OperationType = operationType,
            OwnerModule = ownerModule,
            Status = OperationStatuses.Queued,
            OriginalSourceEventId = request.OriginalSourceEventId,
            OriginalCausationId = causationId,
            CorrelationId = correlationId,
            LegalEntityId = request.LegalEntityId,
            OutletId = request.OutletId,
            RequestedByUserId = request.RequestedByUserId,
            RequestedByMembershipId = request.RequestedByMembershipId,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            Replayable = request.Replayable,
            Version = 1
        };
        dbContext.Operations.Add(operation);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return operation;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.ChangeTracker.Clear();
            existing = await FindReplayOperationAsync(
                request.TenantId, ownerModule, operationType, request.OriginalSourceEventId, cancellationToken);
            if (existing is null) throw;
            EnsureEquivalent(existing, request, causationId, correlationId);
            return existing;
        }
    }

    public async Task<Operation> TransitionAsync(
        Guid tenantId,
        Guid operationId,
        long expectedVersion,
        string targetStatus,
        string? resultReferenceType = null,
        Guid? resultReferenceId = null,
        string? safeErrorCode = null,
        string? safeDetailJson = null,
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
        if (normalizedTarget == OperationStatuses.Running) operation.StartedAtUtc = now;
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
        Guid tenantId,
        Guid operationId,
        string workerCode,
        string safeDetailJson = "{}",
        CancellationToken cancellationToken = default)
    {
        var operation = await dbContext.Operations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == operationId, cancellationToken)
            ?? throw new DurableOperationRuleException("OPERATIONS.NOT_FOUND", "Operation was not found.");
        var nextAttempt = (await dbContext.OperationAttempts
            .Where(x => x.TenantId == tenantId && x.OperationId == operationId)
            .MaxAsync(x => (int?)x.AttemptNumber, cancellationToken) ?? 0) + 1;
        var attempt = new OperationAttempt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OperationId = operationId,
            AttemptNumber = nextAttempt,
            Status = OperationAttemptStatuses.Running,
            WorkerCode = NormalizeCode(workerCode, 100, "worker code"),
            StartedAtUtc = timeProvider.GetUtcNow(),
            SafeDetailJson = SafeDetailPolicy.Normalize(safeDetailJson),
            OriginalSourceEventId = operation.OriginalSourceEventId,
            OriginalCausationId = operation.OriginalCausationId,
            CorrelationId = operation.CorrelationId
        };
        dbContext.OperationAttempts.Add(attempt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return attempt;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DurableOperationRuleException("OPERATIONS.ATTEMPT.SEQUENCE_CONFLICT", "Attempt sequence was concurrently allocated.");
        }
    }

    public async Task<OperationAttempt> CompleteAttemptAsync(
        Guid tenantId,
        Guid attemptId,
        bool succeeded,
        string? safeErrorCode = null,
        string safeDetailJson = "{}",
        CancellationToken cancellationToken = default)
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
        await dbContext.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    public async Task<OperationCheckpoint> AddCheckpointAsync(
        Guid tenantId,
        Guid operationId,
        int sequence,
        string checkpointKey,
        int progressPercentage,
        string safeDetailJson = "{}",
        CancellationToken cancellationToken = default)
    {
        if (progressPercentage is < 0 or > 100)
            throw new DurableOperationRuleException("OPERATIONS.CHECKPOINT.PROGRESS_INVALID", "Progress must be between 0 and 100.");
        if (!await dbContext.Operations.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == operationId, cancellationToken))
            throw new DurableOperationRuleException("OPERATIONS.NOT_FOUND", "Operation was not found.");
        var expectedSequence = (await dbContext.OperationCheckpoints
            .Where(x => x.TenantId == tenantId && x.OperationId == operationId)
            .MaxAsync(x => (int?)x.Sequence, cancellationToken) ?? 0) + 1;
        if (sequence != expectedSequence)
            throw new DurableOperationRuleException("OPERATIONS.CHECKPOINT.SEQUENCE_INVALID", $"Expected checkpoint sequence {expectedSequence}.");
        var checkpoint = new OperationCheckpoint
        {
            Id = Guid.NewGuid(), TenantId = tenantId, OperationId = operationId, Sequence = sequence,
            CheckpointKey = NormalizeCode(checkpointKey, 100, "checkpoint key"),
            ProgressPercentage = progressPercentage,
            SafeDetailJson = SafeDetailPolicy.Normalize(safeDetailJson),
            OccurredAtUtc = timeProvider.GetUtcNow()
        };
        dbContext.OperationCheckpoints.Add(checkpoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return checkpoint;
    }

    public async Task<WorkerHeartbeat> UpsertHeartbeatAsync(
        WorkerHeartbeatUpdate update,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(update.TenantId);
        if (update.PendingCount < 0 || update.RetryPendingCount < 0 || update.TerminalFailureCount < 0)
            throw new DurableOperationRuleException("OPERATIONS.HEARTBEAT.COUNT_INVALID", "Heartbeat counts cannot be negative.");
        var workerCode = NormalizeCode(update.WorkerCode, 100, "worker code");
        var heartbeat = await dbContext.WorkerHeartbeats.SingleOrDefaultAsync(
            x => x.TenantId == update.TenantId && x.WorkerCode == workerCode, cancellationToken);
        if (heartbeat is null)
        {
            heartbeat = new WorkerHeartbeat { Id = Guid.NewGuid(), TenantId = update.TenantId, WorkerCode = workerCode };
            dbContext.WorkerHeartbeats.Add(heartbeat);
        }
        heartbeat.LastIterationStartedAtUtc = update.LastIterationStartedAtUtc;
        heartbeat.LastSucceededAtUtc = update.LastSucceededAtUtc;
        heartbeat.LastFailedAtUtc = update.LastFailedAtUtc;
        heartbeat.CurrentOrLastSourceId = update.CurrentOrLastSourceId;
        heartbeat.PendingCount = update.PendingCount;
        heartbeat.RetryPendingCount = update.RetryPendingCount;
        heartbeat.TerminalFailureCount = update.TerminalFailureCount;
        heartbeat.OldestPendingAtUtc = update.OldestPendingAtUtc;
        heartbeat.LastSafeErrorCode = Optional(update.LastSafeErrorCode, 120, "last safe error code");
        heartbeat.UpdatedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return heartbeat;
    }

    public async Task<ProcessingFailureProjection> RecordFailureAsync(
        ProcessingFailureUpdate update,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(update.TenantId);
        if (update.OriginalSourceEventId == Guid.Empty || update.ResourceId == Guid.Empty)
            throw new DurableOperationRuleException("OPERATIONS.FAILURE.INVALID", "Source and resource identifiers are required.");
        var ownerModule = NormalizeCode(update.OwnerModule, 60, "owner module");
        var processorCode = NormalizeCode(update.ProcessorCode, 100, "processor code");
        var classification = NormalizeCode(update.FailureClassification, 40, "failure classification");
        var requestedState = NormalizeCode(update.State, 24, "failure state");
        if (!ProcessingFailureStateSet.Contains(requestedState))
            throw new DurableOperationRuleException("OPERATIONS.FAILURE.STATE_INVALID", "Processing failure state is not approved.");
        var terminal = ProcessingFailureClassifications.IsTerminalInvalid(classification);
        var state = terminal ? ProcessingFailureStates.TerminalRejected : requestedState;
        var replayable = !terminal && update.Replayable;
        var now = timeProvider.GetUtcNow();
        var failure = await dbContext.ProcessingFailures.SingleOrDefaultAsync(
            x => x.TenantId == update.TenantId && x.OwnerModule == ownerModule
                 && x.ProcessorCode == processorCode && x.OriginalSourceEventId == update.OriginalSourceEventId,
            cancellationToken);
        if (failure is null)
        {
            failure = new ProcessingFailureProjection
            {
                Id = Guid.NewGuid(), TenantId = update.TenantId, OwnerModule = ownerModule,
                ProcessorCode = processorCode, FailureClassification = classification,
                OriginalSourceEventId = update.OriginalSourceEventId,
                FirstFailedAtUtc = now, AttemptCount = 0,
                CorrelationId = Required(update.CorrelationId, 64, "correlation identifier"),
                ResourceType = NormalizeCode(update.ResourceType, 100, "resource type"), ResourceId = update.ResourceId,
                SafeErrorCode = Required(update.SafeErrorCode, 120, "safe error code"), SafeDetailJson = "{}", State = state
            };
            dbContext.ProcessingFailures.Add(failure);
        }
        failure.FailureClassification = classification;
        failure.OriginalCausationId = Optional(update.OriginalCausationId, 128, "original causation identifier");
        failure.LegalEntityId = update.LegalEntityId;
        failure.OutletId = update.OutletId;
        failure.AttemptCount++;
        failure.LastFailedAtUtc = now;
        failure.SafeErrorCode = Required(update.SafeErrorCode, 120, "safe error code");
        failure.SafeDetailJson = SafeDetailPolicy.Normalize(update.SafeDetailJson);
        failure.Replayable = replayable;
        failure.CurrentOperationId = replayable ? update.CurrentOperationId : null;
        failure.State = state;
        await dbContext.SaveChangesAsync(cancellationToken);
        return failure;
    }

    public async Task<IReadOnlyList<Operation>> QueryOperationsAsync(
        Guid tenantId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        if (limit is < 1 or > MaximumPageSize)
            throw new DurableOperationRuleException("OPERATIONS.QUERY.LIMIT_INVALID", $"Limit must be between 1 and {MaximumPageSize}.");
        return await dbContext.Operations.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(limit).ToListAsync(cancellationToken);
    }

    private Task<Operation?> FindReplayOperationAsync(Guid tenantId, string ownerModule, string operationType, Guid sourceEventId, CancellationToken cancellationToken)
        => dbContext.Operations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.OwnerModule == ownerModule
                 && x.OperationType == operationType && x.OriginalSourceEventId == sourceEventId,
            cancellationToken);

    private static void EnsureEquivalent(Operation existing, CreateReplayOperationRequest request, string? causationId, string correlationId)
    {
        if (existing.OriginalCausationId != causationId || existing.CorrelationId != correlationId
            || existing.LegalEntityId != request.LegalEntityId || existing.OutletId != request.OutletId
            || existing.RequestedByUserId != request.RequestedByUserId
            || existing.RequestedByMembershipId != request.RequestedByMembershipId
            || existing.Replayable != request.Replayable)
            throw new DurableOperationRuleException("OPERATIONS.REPLAY.IDENTITY_CONFLICT", "Replay identity is already associated with different metadata.");
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
