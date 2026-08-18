using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.BuildingBlocks.Messaging;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Audit;
using Ogfi.Modules.Audit.Persistence;
using Ogfi.Modules.Finance;
using Ogfi.Modules.Finance.Persistence;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow;
using Ogfi.Modules.Workflow.Persistence;

namespace Ogfi.Workers;

public sealed class AuditMaterialActionProcessor(
    ProcurementDbContext procurementDb,
    WorkflowDbContext workflowDb,
    InventoryDbContext inventoryDb,
    FinanceDbContext financeDb,
    AuditDbContext auditDb,
    AuditIngestionService audit,
    OutboxDeliveryStore deliveries,
    ProcessorFailureRecorder failures,
    ILogger<AuditMaterialActionProcessor> logger)
{
    public async Task<ProcessorIterationResult> ProcessTenantAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        Guid? lastSource = null;
        var pendingIds = await deliveries.GetPendingMessageIdsAsync(
            tenantId, OutboxConsumerCodes.AuditMaterialAction,
            ["Procurement.PurchaseOrderApprovalRequested", "Procurement.GoodsReceiptPosted"],
            100, cancellationToken);
        var procurementMessages = await procurementDb.OutboxMessages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && pendingIds.Contains(x.Id))
            .OrderBy(x => x.OccurredAtUtc).Take(100).ToListAsync(cancellationToken);
        foreach (var message in procurementMessages)
        {
            lastSource = message.Id;
            var delivery = await deliveries.EnsureAsync(
                tenantId, message.Id, OutboxConsumerCodes.AuditMaterialAction, cancellationToken);
            if (delivery.Status is OutboxDeliveryStatuses.Completed or OutboxDeliveryStatuses.TerminalRejected)
                continue;
            await deliveries.MarkAttemptAsync(
                tenantId, message.Id, OutboxConsumerCodes.AuditMaterialAction, cancellationToken);
            var context = Context(message, tenantId);
            try
            {
                if (message.Type == "Procurement.PurchaseOrderApprovalRequested")
                    await IngestPurchaseOrderSubmissionAsync(message, cancellationToken);
                else
                    await IngestReceiptConsequencesAsync(message, cancellationToken);
                await deliveries.MarkCompletedAsync(
                    tenantId, message.Id, OutboxConsumerCodes.AuditMaterialAction, cancellationToken);
                await failures.RecoverAsync(context, cancellationToken);
                if (message.Type == "Procurement.GoodsReceiptPosted")
                    await deliveries.TryFinalizeMessageAsync(tenantId, message.Id, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var failure = await failures.RecordAsync(context, ex, cancellationToken);
                if (failure.State == Modules.DurableOperations.ProcessingFailureStates.TerminalRejected)
                    await deliveries.MarkTerminalRejectedAsync(
                        tenantId, message.Id, OutboxConsumerCodes.AuditMaterialAction,
                        failure.SafeErrorCode, cancellationToken);
                else
                    await deliveries.MarkRetryAsync(
                        tenantId, message.Id, OutboxConsumerCodes.AuditMaterialAction,
                        failure.SafeErrorCode, cancellationToken);
                logger.LogWarning(ex, "Audit material delivery {MessageId} remains visible for tenant {TenantId}", message.Id, tenantId);
            }
        }

        var workflowIds = await GetPendingWorkflowMessageIdsAsync(tenantId, cancellationToken);
        var workflowMessages = await workflowDb.OutboxMessages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && workflowIds.Contains(x.Id))
            .OrderBy(x => x.OccurredAtUtc).Take(100).ToListAsync(cancellationToken);
        foreach (var message in workflowMessages)
        {
            lastSource = message.Id;
            var context = Context(message, tenantId, Rs01MaterialStages.WorkflowOwner);
            try
            {
                await IngestApprovalEvidenceAsync(message, cancellationToken);
                await failures.RecoverAsync(context, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await failures.RecordAsync(context, ex, cancellationToken);
                logger.LogWarning(ex, "Workflow Audit evidence {MessageId} remains visible for tenant {TenantId}", message.Id, tenantId);
            }
        }

        var state = await deliveries.GetConsumerStateAsync(
            tenantId, OutboxConsumerCodes.AuditMaterialAction, cancellationToken);
        return state with { CurrentOrLastSourceId = lastSource ?? state.CurrentOrLastSourceId };
    }

    public async Task<ReplayDispatchResult> ReplaySourceAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken)
    {
        var procurement = await procurementDb.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == command.TenantId && x.Id == command.OriginalSourceEventId,
            cancellationToken);
        if (procurement is not null)
        {
            if (procurement.Type == "Procurement.PurchaseOrderApprovalRequested")
                await IngestPurchaseOrderSubmissionAsync(procurement, cancellationToken);
            else if (procurement.Type == "Procurement.GoodsReceiptPosted")
                await IngestReceiptConsequencesAsync(procurement, cancellationToken);
            else
                return Rejected("AUDIT.REPLAY.SOURCE_UNSUPPORTED");
            return Succeeded("AUDIT_EVENT", procurement.Id);
        }
        var workflow = await workflowDb.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == command.TenantId && x.Id == command.OriginalSourceEventId,
            cancellationToken);
        if (workflow is null) return Rejected("AUDIT.REPLAY.SOURCE_NOT_FOUND");
        await IngestApprovalEvidenceAsync(workflow, cancellationToken);
        return Succeeded("AUDIT_EVENT", workflow.Id);
    }

    private async Task IngestPurchaseOrderSubmissionAsync(OutboxMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.Deserialize<PurchaseOrderApprovalRequestedV1>(message.Payload)
            ?? throw new JsonException("Approval request payload is empty.");
        EnsureEnvelope(message, payload.TenantId, payload.EventId);
        await audit.IngestAsync(Material(
            message, payload.TenantId, payload.RequestedByUserId,
            Rs01MaterialStages.PurchaseOrderSubmission, Rs01MaterialStages.ProcurementOwner,
            "PURCHASE_ORDER", payload.PurchaseOrderId, payload.SubjectVersion,
            payload.LegalEntityId, payload.OutletId, payload.BusinessDate,
            "{\"status\":\"SUBMITTED\"}", purchaseOrderId: payload.PurchaseOrderId), token);
    }

    private async Task IngestApprovalEvidenceAsync(OutboxMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.Deserialize<PurchaseOrderApprovalCompletedV1>(message.Payload)
            ?? throw new JsonException("Approval completion payload is empty.");
        EnsureEnvelope(message, payload.TenantId, payload.EventId);
        var decision = await workflowDb.ApprovalDecisions.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == payload.TenantId && x.TaskId == payload.WorkflowTaskId, token)
            ?? throw new InvalidOperationException("Approval decision is not committed yet.");
        await audit.IngestAsync(Material(
            message, payload.TenantId, payload.ActorUserId,
            Rs01MaterialStages.WorkflowApprovalDecision, Rs01MaterialStages.WorkflowOwner,
            "APPROVAL_DECISION", decision.Id, null, null, null, null,
            JsonSerializer.Serialize(new { decision = payload.Decision }),
            purchaseOrderId: payload.PurchaseOrderId, workflowInstanceId: payload.WorkflowInstanceId,
            approvalTaskId: payload.WorkflowTaskId, approvalDecisionId: decision.Id), token);

        var purchaseOrder = await procurementDb.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == payload.TenantId && x.Id == payload.PurchaseOrderId, token);
        if (purchaseOrder?.Status != ProcurementStatuses.Approved)
            throw new InvalidOperationException("Procurement approval outcome is not committed yet.");
        await audit.IngestAsync(Material(
            message, payload.TenantId, payload.ActorUserId,
            Rs01MaterialStages.ProcurementApprovalApplication, Rs01MaterialStages.ProcurementOwner,
            "PURCHASE_ORDER", payload.PurchaseOrderId, purchaseOrder.Version,
            purchaseOrder.LegalEntityId, purchaseOrder.OutletId, purchaseOrder.BusinessDate,
            "{\"status\":\"APPROVED\"}", purchaseOrderId: payload.PurchaseOrderId,
            workflowInstanceId: payload.WorkflowInstanceId, approvalTaskId: payload.WorkflowTaskId,
            approvalDecisionId: decision.Id), token);
    }

    private async Task IngestReceiptConsequencesAsync(OutboxMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.Deserialize<GoodsReceiptPostedV1>(message.Payload)
            ?? throw new JsonException("Goods Receipt payload is empty.");
        EnsureEnvelope(message, payload.TenantId, payload.EventId);
        await audit.IngestAsync(Material(
            message, payload.TenantId, payload.PostedByUserId,
            Rs01MaterialStages.GoodsReceiptPosting, Rs01MaterialStages.ProcurementOwner,
            "GOODS_RECEIPT", payload.GoodsReceiptId, null,
            payload.LegalEntityId, payload.OutletId, payload.BusinessDate,
            JsonSerializer.Serialize(new { status = "POSTED", lineCount = payload.Lines.Count }),
            purchaseOrderId: payload.PurchaseOrderId, goodsReceiptId: payload.GoodsReceiptId), token);

        var movement = await inventoryDb.InventoryMovements.AsNoTracking()
            .Where(x => x.TenantId == payload.TenantId && x.SourceEventId == payload.EventId)
            .OrderBy(x => x.Id).FirstOrDefaultAsync(token)
            ?? throw new InvalidOperationException("Inventory Movement is not committed yet.");
        await audit.IngestAsync(Material(
            message, payload.TenantId, null,
            Rs01MaterialStages.InventoryMovementCreation, Rs01MaterialStages.InventoryOwner,
            "INVENTORY_MOVEMENT", movement.Id, null,
            payload.LegalEntityId, payload.OutletId, payload.BusinessDate,
            "{\"movementType\":\"PURCHASE_RECEIPT\"}", purchaseOrderId: payload.PurchaseOrderId,
            goodsReceiptId: payload.GoodsReceiptId, inventoryMovementId: movement.Id), token);

        var source = await financeDb.SourcePostings.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == payload.TenantId && x.SourceEventId == payload.EventId, token)
            ?? throw new InvalidOperationException("Finance Source Posting is not committed yet.");
        if (source.Status != FinanceStatuses.Posted || source.JournalId is null)
            throw new FinanceRuleException(source.ErrorCode ?? "FINANCE.POSTING.NOT_COMPLETE", "Finance posting is not complete.");
        await audit.IngestAsync(Material(
            message, payload.TenantId, null,
            Rs01MaterialStages.FinanceJournalPosting, Rs01MaterialStages.FinanceOwner,
            "FINANCE_SOURCE_POSTING", source.Id, null,
            payload.LegalEntityId, payload.OutletId, payload.BusinessDate,
            "{\"postingStatus\":\"POSTED\"}", purchaseOrderId: payload.PurchaseOrderId,
            goodsReceiptId: payload.GoodsReceiptId, financeSourcePostingId: source.Id,
            journalId: source.JournalId), token);
    }

    private static AuditMaterialActionRecordedV1 Material(
        OutboxMessage source, Guid tenantId, Guid? actorUserId, string action, string owner,
        string resourceType, Guid resourceId, long? revision, Guid? legalEntityId, Guid? outletId,
        DateOnly? businessDate, string evidence, Guid? purchaseOrderId = null,
        Guid? workflowInstanceId = null, Guid? approvalTaskId = null, Guid? approvalDecisionId = null,
        Guid? goodsReceiptId = null, Guid? inventoryMovementId = null,
        Guid? financeSourcePostingId = null, Guid? journalId = null)
        => new(
            DeterministicId(source.Id, owner, action), tenantId,
            actorUserId is null ? AuditActorTypes.System : AuditActorTypes.Human,
            actorUserId, null, action, owner, resourceType, resourceId, revision,
            legalEntityId, outletId, businessDate, source.OccurredAtUtc,
            AuditOutcomes.Succeeded, null, source.CorrelationId, source.CausationId,
            source.Id, evidence, purchaseOrderId, workflowInstanceId, approvalTaskId,
            approvalDecisionId, goodsReceiptId, inventoryMovementId, financeSourcePostingId, journalId);

    private static Guid DeterministicId(Guid sourceId, string owner, string action)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId:N}|{owner}|{action}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void EnsureEnvelope(OutboxMessage source, Guid tenantId, Guid eventId)
    {
        if (source.TenantId != tenantId || source.Id != eventId)
            throw new InvalidOperationException("Event envelope identity does not match its payload.");
    }

    private static ProcessorMessageContext Context(
        OutboxMessage message, Guid tenantId, string owner = Rs01MaterialStages.ProcurementOwner)
        => new(tenantId, owner, ProcessorCodes.Audit, message.Id, message.CausationId,
            message.CorrelationId, "OUTBOX_MESSAGE", message.Id);

    private static ReplayDispatchResult Succeeded(string type, Guid id)
        => new(true, type, id, SafeDetailJson: "{\"status\":\"SUCCEEDED\"}");

    private static ReplayDispatchResult Rejected(string code)
        => new(false, SafeErrorCode: code,
            SafeDetailJson: JsonSerializer.Serialize(new { reasonCode = code }), Retryable: false);

    private async Task<Guid[]> GetPendingWorkflowMessageIdsAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        await auditDb.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)auditDb.Database.GetDbConnection();
        await using var command = new NpgsqlCommand("""
            SELECT m."Id"
              FROM workflow.outbox_messages m
              LEFT JOIN audit.audit_events decision
                ON decision."TenantId" = m."TenantId" AND decision."SourceEventId" = m."Id"
               AND decision."SourceModule" = 'WORKFLOW'
               AND decision."Action" = 'WORKFLOW.APPROVAL_DECIDED'
              LEFT JOIN audit.audit_events applied
                ON applied."TenantId" = m."TenantId" AND applied."SourceEventId" = m."Id"
               AND applied."SourceModule" = 'PROCUREMENT'
               AND applied."Action" = 'PURCHASE_ORDER.APPROVAL_APPLIED'
             WHERE m."TenantId" = @tenant
               AND m."Type" = 'Workflow.PurchaseOrderApprovalCompleted'
               AND m."SchemaVersion" = 1
               AND (decision."Id" IS NULL OR applied."Id" IS NULL)
             ORDER BY m."OccurredAtUtc", m."Id"
             LIMIT 100;
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }
}
