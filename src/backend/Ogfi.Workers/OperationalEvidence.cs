using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Audit;
using Ogfi.Modules.DurableOperations;
using Ogfi.Modules.DurableOperations.Persistence;
using Ogfi.Modules.Finance;
using Ogfi.Modules.Inventory;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Workflow;

namespace Ogfi.Workers;

public static class WorkerCodes
{
    public const string Approval = "approval.request-outcome";
    public const string Inventory = "inventory.stock-consequence";
    public const string Finance = "finance.financial-consequence";
    public const string Audit = "audit.material-action-ingestion";
    public const string Replay = "operations.replay";
}

public static class ProcessorCodes
{
    public const string ApprovalRequest = "approval.request";
    public const string ApprovalOutcome = "approval.outcome";
    public const string Inventory = "inventory.stock-consequence";
    public const string Finance = "finance.financial-consequence";
    public const string Audit = "audit.material-action-ingestion";
}

public sealed record ProcessorMessageContext(
    Guid TenantId,
    string OwnerModule,
    string ProcessorCode,
    Guid SourceEventId,
    string? CausationId,
    string CorrelationId,
    string ResourceType,
    Guid ResourceId,
    Guid? LegalEntityId = null,
    Guid? OutletId = null);

public sealed record ProcessorIterationResult(
    Guid? CurrentOrLastSourceId,
    int PendingCount,
    int RetryPendingCount,
    int TerminalFailureCount,
    DateTimeOffset? OldestPendingAtUtc,
    string? LastSafeErrorCode = null)
{
    public static readonly ProcessorIterationResult Empty = new(null, 0, 0, 0, null);
}

public sealed record HeartbeatIteration(
    Guid TenantId,
    string WorkerCode,
    DateTimeOffset StartedAtUtc,
    Guid StartObservationId);

public sealed class WorkerHeartbeatReporter(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
{
    public async Task<HeartbeatIteration> RecordStartAsync(
        Guid tenantId, string workerCode, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var observationId = Guid.NewGuid();
        await WriteAsync(tenantId, workerCode, observationId, started, succeeded: null,
            ProcessorIterationResult.Empty, cancellationToken);
        return new HeartbeatIteration(tenantId, workerCode, started, observationId);
    }

    public Task RecordSuccessAsync(
        HeartbeatIteration iteration,
        ProcessorIterationResult result,
        CancellationToken cancellationToken)
        => WriteAsync(iteration.TenantId, iteration.WorkerCode, Guid.NewGuid(), iteration.StartedAtUtc,
            succeeded: true, result, cancellationToken);

    public Task RecordFailureAsync(
        HeartbeatIteration iteration,
        ProcessorIterationResult result,
        string safeErrorCode,
        CancellationToken cancellationToken)
        => WriteAsync(iteration.TenantId, iteration.WorkerCode, Guid.NewGuid(), iteration.StartedAtUtc,
            succeeded: false, result with { LastSafeErrorCode = safeErrorCode }, cancellationToken);

    private async Task WriteAsync(
        Guid tenantId,
        string workerCode,
        Guid observationId,
        DateTimeOffset started,
        bool? succeeded,
        ProcessorIterationResult result,
        CancellationToken cancellationToken)
    {
        // A heartbeat never shares the authoritative processor scope or DbContext.
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>().SetCandidateTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<DurableOperationsDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<DurableOperationService>();
        var normalized = workerCode.Trim().ToUpperInvariant();
        var previous = await db.WorkerHeartbeats.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.WorkerCode == normalized, cancellationToken);
        var sequence = checked((previous?.ObservationSequence ?? 0) + 1);
        var now = timeProvider.GetUtcNow();
        var update = new WorkerHeartbeatUpdate(
            tenantId, workerCode, observationId, sequence, started,
            succeeded == true ? now : previous?.LastSucceededAtUtc,
            succeeded == false ? now : previous?.LastFailedAtUtc,
            result.CurrentOrLastSourceId ?? previous?.CurrentOrLastSourceId,
            result.PendingCount, result.RetryPendingCount, result.TerminalFailureCount,
            result.OldestPendingAtUtc, result.LastSafeErrorCode);
        try
        {
            await service.UpsertHeartbeatAsync(update, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Retry the exact observation identity; UpsertHeartbeatAsync is idempotent for it.
            db.ChangeTracker.Clear();
            await service.UpsertHeartbeatAsync(update, cancellationToken);
        }
    }
}

public sealed record FailureClassification(
    string Classification,
    string State,
    bool Replayable,
    string SafeErrorCode);

public static class ProcessorFailureClassifier
{
    public static FailureClassification Classify(Exception exception)
    {
        var code = ExceptionCode(exception);
        if (code.Contains("TENANT_MISMATCH", StringComparison.Ordinal))
            return Terminal(ProcessingFailureClassifications.ForgedTenant, code);
        if (exception is JsonException
            || code.Contains("INVALID_JSON", StringComparison.Ordinal)
            || code.Contains("EVENT.INVALID", StringComparison.Ordinal)
            || code.Contains("INGESTION.INVALID", StringComparison.Ordinal)
            || code.StartsWith("AUDIT.EVIDENCE.", StringComparison.Ordinal)
            || code.Contains("IDENTITY_CONFLICT", StringComparison.Ordinal))
            return Terminal(ProcessingFailureClassifications.MalformedContract, code);
        if (code.Contains("AUTH", StringComparison.Ordinal)
            || code.Contains("PERMISSION", StringComparison.Ordinal))
            return Terminal(ProcessingFailureClassifications.Authorization, code);
        if (code.Contains("SECURITY", StringComparison.Ordinal))
            return Terminal(ProcessingFailureClassifications.SecurityTerminal, code);
        if (exception is ProcurementRuleException or WorkflowRuleException
            or InventoryRuleException or FinanceRuleException or AuditRuleException)
            return new FailureClassification(
                ProcessingFailureClassifications.Business,
                ProcessingFailureStates.BusinessFailed,
                true,
                code);
        return new FailureClassification(
            ProcessingFailureClassifications.Transient,
            ProcessingFailureStates.RetryPending,
            true,
            code);
    }

    private static FailureClassification Terminal(string classification, string code)
        => new(classification, ProcessingFailureStates.TerminalRejected, false, code);

    private static string ExceptionCode(Exception exception) => exception switch
    {
        ProcurementRuleException value => value.Code,
        WorkflowRuleException value => value.Code,
        InventoryRuleException value => value.Code,
        FinanceRuleException value => value.Code,
        AuditRuleException value => value.Code,
        DurableOperationRuleException value => value.Code,
        JsonException => "CONTRACT.INVALID_JSON",
        _ => exception.GetType().Name.ToUpperInvariant()
    };
}

public sealed class ProcessorFailureRecorder(IServiceScopeFactory scopeFactory)
{
    public async Task<ProcessingFailureProjection> RecordAsync(
        ProcessorMessageContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var classified = ProcessorFailureClassifier.Classify(exception);
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>()
            .SetCandidateTenant(context.TenantId);
        var service = scope.ServiceProvider.GetRequiredService<DurableOperationService>();
        var failure = await service.RecordFailureAsync(new ProcessingFailureUpdate(
            context.TenantId, context.OwnerModule, context.ProcessorCode,
            classified.Classification, context.SourceEventId, context.CausationId,
            context.CorrelationId, context.ResourceType, context.ResourceId,
            classified.SafeErrorCode,
            JsonSerializer.Serialize(new { reasonCode = classified.SafeErrorCode }),
            classified.State, classified.Replayable,
            context.LegalEntityId, context.OutletId), cancellationToken);
        try
        {
            await scope.ServiceProvider.GetRequiredService<OperationalAuditEvidenceService>()
                .RecordFailureAsync(failure, cancellationToken);
        }
        catch (Exception)
        {
            // Audit evidence is an independent consequence; the failure projection remains authoritative.
        }
        return failure;
    }

    public async Task<ProcessingFailureProjection?> RecoverAsync(
        ProcessorMessageContext context,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>()
            .SetCandidateTenant(context.TenantId);
        var recovered = await scope.ServiceProvider.GetRequiredService<DurableOperationService>()
            .RecoverFailureAfterNormalRetryAsync(
                context.TenantId, context.OwnerModule, context.ProcessorCode,
                context.SourceEventId, "{\"status\":\"RECOVERED\"}", cancellationToken);
        if (recovered is not null)
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<OperationalAuditEvidenceService>()
                    .RecordRecoveryAsync(recovered, cancellationToken);
            }
            catch (Exception)
            {
                // Recovery state cannot be rolled back by independent Audit evidence.
            }
        }
        return recovered;
    }
}
