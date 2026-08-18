using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.DurableOperations;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow.Persistence;

namespace Ogfi.Workers;

public sealed class ApprovalRequestReplayHandler(
    ApprovalSpineProcessor processor,
    ProcurementDbContext procurementDb) : IReplayOwnerHandler
{
    public string OwnerModule => "WORKFLOW";
    public string OperationType => ProcessorCodes.ApprovalRequest;

    public async Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
    {
        var source = await procurementDb.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == command.TenantId && x.Id == command.OriginalSourceEventId
                 && x.Type == "Procurement.PurchaseOrderApprovalRequested", cancellationToken);
        if (source is null) return Missing("WORKFLOW.REPLAY.SOURCE_NOT_FOUND");
        await processor.ProcessTenantAsync(command.TenantId, cancellationToken);
        procurementDb.ChangeTracker.Clear();
        var completed = await procurementDb.OutboxMessages.AsNoTracking().SingleAsync(
            x => x.TenantId == command.TenantId && x.Id == source.Id, cancellationToken);
        return completed.ProcessedAtUtc is not null
            ? Success("WORKFLOW_INSTANCE", source.Id)
            : Retry(completed.LastError ?? "WORKFLOW.REPLAY.PENDING");
    }

    private static ReplayDispatchResult Missing(string code) => new(false, SafeErrorCode: code,
        SafeDetailJson: "{\"reasonCode\":\"SOURCE_NOT_FOUND\"}");
    private static ReplayDispatchResult Retry(string code) => new(false, SafeErrorCode: code,
        SafeDetailJson: "{\"status\":\"RETRY_PENDING\"}", Retryable: true);
    private static ReplayDispatchResult Success(string type, Guid id) => new(true, type, id,
        SafeDetailJson: "{\"status\":\"SUCCEEDED\"}");
}

public sealed class ApprovalOutcomeReplayHandler(
    ApprovalSpineProcessor processor,
    WorkflowDbContext workflowDb) : IReplayOwnerHandler
{
    public string OwnerModule => "PROCUREMENT";
    public string OperationType => ProcessorCodes.ApprovalOutcome;

    public async Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
    {
        var source = await workflowDb.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == command.TenantId && x.Id == command.OriginalSourceEventId
                 && x.Type == "Workflow.PurchaseOrderApprovalCompleted", cancellationToken);
        if (source is null) return new ReplayDispatchResult(false,
            SafeErrorCode: "PROCUREMENT.REPLAY.SOURCE_NOT_FOUND",
            SafeDetailJson: "{\"reasonCode\":\"SOURCE_NOT_FOUND\"}");
        await processor.ProcessTenantAsync(command.TenantId, cancellationToken);
        workflowDb.ChangeTracker.Clear();
        var completed = await workflowDb.OutboxMessages.AsNoTracking().SingleAsync(
            x => x.TenantId == command.TenantId && x.Id == source.Id, cancellationToken);
        return completed.ProcessedAtUtc is not null
            ? new ReplayDispatchResult(true, "PURCHASE_ORDER", source.Id,
                SafeDetailJson: "{\"status\":\"SUCCEEDED\"}")
            : new ReplayDispatchResult(false, SafeErrorCode: completed.LastError ?? "PROCUREMENT.REPLAY.PENDING",
                SafeDetailJson: "{\"status\":\"RETRY_PENDING\"}", Retryable: true);
    }
}

public sealed class InventoryReplayHandler(StockConsequenceProcessor processor) : IReplayOwnerHandler
{
    public string OwnerModule => "INVENTORY";
    public string OperationType => ProcessorCodes.Inventory;
    public Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
        => processor.ReplaySourceAsync(command, cancellationToken);
}

public sealed class FinanceReplayHandler(FinancialConsequenceProcessor processor) : IReplayOwnerHandler
{
    public string OwnerModule => "FINANCE";
    public string OperationType => ProcessorCodes.Finance;
    public Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
        => processor.ReplaySourceAsync(command, cancellationToken);
}

public abstract class AuditReplayHandlerBase(AuditMaterialActionProcessor processor) : IReplayOwnerHandler
{
    public abstract string OwnerModule { get; }
    public string OperationType => ProcessorCodes.Audit;
    public Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
        => processor.ReplaySourceAsync(command, cancellationToken);
}

public sealed class ProcurementAuditReplayHandler(AuditMaterialActionProcessor processor)
    : AuditReplayHandlerBase(processor)
{
    public override string OwnerModule => "PROCUREMENT";
}

public sealed class WorkflowAuditReplayHandler(AuditMaterialActionProcessor processor)
    : AuditReplayHandlerBase(processor)
{
    public override string OwnerModule => "WORKFLOW";
}
