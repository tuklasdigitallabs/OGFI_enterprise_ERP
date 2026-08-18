using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Audit;
using Ogfi.Modules.DurableOperations;

namespace Ogfi.Workers;

public static class OperationalAuditActions
{
    public const string ProcessingFailureRecorded = "PROCESSING.FAILURE.RECORDED";
    public const string ReplayRequested = "REPLAY.REQUESTED";
    public const string ReplayAttempted = "REPLAY.ATTEMPTED";
    public const string ReplaySucceeded = "REPLAY.SUCCEEDED";
    public const string ReplayFailed = "REPLAY.FAILED";
    public const string ReplayCancelled = "REPLAY.CANCELLED";
    public const string TerminalRejected = "PROCESSING.TERMINAL_REJECTED";
    public const string RecoverySucceeded = "PROCESSING.RECOVERED";
}

public sealed class OperationalAuditEvidenceService(AuditIngestionService audit, TimeProvider timeProvider)
{
    public Task RecordFailureAsync(ProcessingFailureProjection failure, CancellationToken cancellationToken)
        => IngestAsync(
            failure.TenantId,
            failure.Id,
            failure.OriginalSourceEventId,
            failure.State == ProcessingFailureStates.TerminalRejected
                ? OperationalAuditActions.TerminalRejected
                : OperationalAuditActions.ProcessingFailureRecorded,
            "PROCESSING_FAILURE",
            failure.SafeErrorCode,
            failure.CorrelationId,
            failure.OriginalCausationId,
            failure.LegalEntityId,
            failure.OutletId,
            failure.State == ProcessingFailureStates.TerminalRejected ? AuditOutcomes.Rejected : AuditOutcomes.Failed,
            JsonSerializer.Serialize(new { reasonCode = failure.SafeErrorCode, status = failure.State }),
            cancellationToken);

    public Task RecordRecoveryAsync(ProcessingFailureProjection failure, CancellationToken cancellationToken)
        => IngestAsync(
            failure.TenantId, failure.Id, failure.Id, OperationalAuditActions.RecoverySucceeded,
            "PROCESSING_FAILURE", null, failure.CorrelationId, failure.OriginalCausationId,
            failure.LegalEntityId, failure.OutletId, AuditOutcomes.Succeeded,
            "{\"status\":\"RECOVERED\"}", cancellationToken);

    public Task RecordOperationAsync(
        Operation operation,
        string action,
        string outcome,
        string? errorCode,
        string safeEvidenceJson,
        CancellationToken cancellationToken)
        => IngestAsync(
            operation.TenantId, operation.Id, operation.Id, action, "OPERATION", errorCode,
            operation.CorrelationId, operation.OriginalCausationId,
            operation.LegalEntityId, operation.OutletId, outcome, safeEvidenceJson,
            cancellationToken);

    private Task IngestAsync(
        Guid tenantId,
        Guid resourceId,
        Guid sourceId,
        string action,
        string resourceType,
        string? errorCode,
        string correlationId,
        string? causationId,
        Guid? legalEntityId,
        Guid? outletId,
        string outcome,
        string safeEvidenceJson,
        CancellationToken cancellationToken)
        => audit.IngestAsync(new AuditMaterialActionRecordedV1(
            DeterministicId(sourceId, action), tenantId, AuditActorTypes.System,
            null, null, action, "DURABLE_OPERATIONS", resourceType, resourceId, null,
            legalEntityId, outletId, null, timeProvider.GetUtcNow(), outcome, errorCode,
            correlationId, causationId, sourceId, safeEvidenceJson), cancellationToken);

    private static Guid DeterministicId(Guid sourceId, string action)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId:N}|{action}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed class OperationalReplayService(
    ReplayCoordinator coordinator,
    OperationalAuditEvidenceService evidence)
{
    public async Task<Operation> RequestReplayAsync(
        Guid tenantId,
        Guid failureId,
        string replayRequestKey,
        Guid? requestedByUserId,
        Guid? requestedByMembershipId,
        CancellationToken cancellationToken = default)
    {
        var operation = await coordinator.RequestReplayForFailureAsync(
            tenantId, failureId, replayRequestKey, requestedByUserId,
            requestedByMembershipId, cancellationToken);
        await evidence.RecordOperationAsync(
            operation, OperationalAuditActions.ReplayRequested, AuditOutcomes.Succeeded, null,
            "{\"status\":\"QUEUED\"}", cancellationToken);
        return operation;
    }

    public async Task<Operation> ExecuteAsync(
        Guid tenantId,
        Guid operationId,
        string workerCode,
        CancellationToken cancellationToken)
    {
        var operation = await coordinator.ExecuteQueuedReplayOperationAsync(
            tenantId, operationId, workerCode, cancellationToken);
        var action = operation.Status switch
        {
            OperationStatuses.Succeeded => OperationalAuditActions.ReplaySucceeded,
            OperationStatuses.Cancelled => OperationalAuditActions.ReplayCancelled,
            OperationStatuses.Failed => OperationalAuditActions.ReplayFailed,
            _ => OperationalAuditActions.ReplayAttempted
        };
        var outcome = operation.Status switch
        {
            OperationStatuses.Succeeded => AuditOutcomes.Succeeded,
            OperationStatuses.Cancelled => AuditOutcomes.Rejected,
            OperationStatuses.Failed => AuditOutcomes.Failed,
            _ => AuditOutcomes.Succeeded
        };
        var error = outcome == AuditOutcomes.Succeeded
            ? null
            : operation.SafeErrorCode ?? $"OPERATIONS.REPLAY.{operation.Status}";
        await evidence.RecordOperationAsync(
            operation, action, outcome, error,
            JsonSerializer.Serialize(new { status = operation.Status }), cancellationToken);
        if (operation.Status == OperationStatuses.Succeeded)
            await evidence.RecordOperationAsync(
                operation, OperationalAuditActions.RecoverySucceeded, AuditOutcomes.Succeeded,
                null, "{\"status\":\"RECOVERED\"}", cancellationToken);
        return operation;
    }
}
