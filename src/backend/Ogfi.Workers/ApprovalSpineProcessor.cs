using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow;
using Ogfi.Modules.Workflow.Persistence;

namespace Ogfi.Workers;

public sealed class ApprovalSpineProcessor(
    ProcurementDbContext procurementDb,
    WorkflowDbContext workflowDb,
    FoundationApproverResolver approverResolver,
    WorkflowApprovalService workflowApproval,
    PurchaseOrderApprovalOutcomeService procurementApproval,
    TimeProvider timeProvider,
    ILogger<ApprovalSpineProcessor> logger)
{
    public async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await ProcessApprovalRequestsAsync(tenantId, cancellationToken);
        await ProcessApprovalOutcomesAsync(tenantId, cancellationToken);
    }

    private async Task ProcessApprovalRequestsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var pending = await procurementDb.OutboxMessages
            .Where(x => x.TenantId == tenantId
                        && x.ProcessedAtUtc == null
                        && x.Type == "Procurement.PurchaseOrderApprovalRequested"
                        && x.SchemaVersion == 1)
            .OrderBy(x => x.OccurredAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            message.AttemptCount++;
            try
            {
                var payload = JsonSerializer.Deserialize<PurchaseOrderApprovalRequestedV1>(message.Payload)
                    ?? throw new InvalidOperationException("Purchase Order approval request payload is empty.");
                if (payload.TenantId != tenantId || payload.EventId != message.Id)
                {
                    throw new InvalidOperationException("Purchase Order approval request envelope identity does not match its payload.");
                }

                var candidateUserIds = await approverResolver.ResolveUserIdsAsync(
                    tenantId,
                    ProcurementPermissionCodes.PurchaseOrderApprove,
                    payload.OutletId,
                    cancellationToken);

                await workflowApproval.StartPurchaseOrderApprovalAsync(
                    new PurchaseOrderApprovalStartCommand(
                        payload.TenantId,
                        payload.PurchaseOrderId,
                        payload.ApprovalRound,
                        payload.SubjectVersion,
                        payload.RequestedByUserId,
                        payload.LegalEntityId,
                        payload.OutletId,
                        payload.BusinessDate,
                        payload.ApprovalContext.PurchaseOrderTotal,
                        payload.ApprovalContext.Currency,
                        payload.CorrelationId,
                        payload.OccurredAtUtc),
                    candidateUserIds,
                    cancellationToken);

                message.LastError = null;
                message.ProcessedAtUtc = timeProvider.GetUtcNow();
                await procurementDb.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is WorkflowRuleException or JsonException or InvalidOperationException)
            {
                message.LastError = ex is WorkflowRuleException workflowError ? workflowError.Code : ex.GetType().Name;
                await procurementDb.SaveChangesAsync(cancellationToken);
                logger.LogWarning(ex, "Approval-start message {MessageId} for tenant {TenantId} remains pending", message.Id, tenantId);
            }
        }
    }

    private async Task ProcessApprovalOutcomesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var pending = await workflowDb.OutboxMessages
            .Where(x => x.TenantId == tenantId
                        && x.ProcessedAtUtc == null
                        && x.Type == "Workflow.PurchaseOrderApprovalCompleted"
                        && x.SchemaVersion == 1)
            .OrderBy(x => x.OccurredAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            message.AttemptCount++;
            try
            {
                var payload = JsonSerializer.Deserialize<PurchaseOrderApprovalCompletedV1>(message.Payload)
                    ?? throw new InvalidOperationException("Purchase Order approval completion payload is empty.");
                if (payload.TenantId != tenantId || payload.EventId != message.Id)
                {
                    throw new InvalidOperationException("Purchase Order approval completion envelope identity does not match its payload.");
                }

                await procurementApproval.ApplyAsync(
                    tenantId,
                    new PurchaseOrderApprovalOutcome(
                        payload.WorkflowInstanceId,
                        payload.WorkflowTaskId,
                        payload.PurchaseOrderId,
                        payload.ApprovalRound,
                        payload.SubjectVersion,
                        payload.Decision,
                        payload.ActorUserId,
                        payload.DecidedAtUtc,
                        payload.CorrelationId),
                    cancellationToken);

                message.LastError = null;
                message.ProcessedAtUtc = timeProvider.GetUtcNow();
                await workflowDb.SaveChangesAsync(cancellationToken);
            }
            catch (ProcurementRuleException ex) when (ex.Code is "PROCUREMENT.PO.APPROVAL_STALE" or "PROCUREMENT.PO.APPROVAL_OUTCOME_INVALID" or "PROCUREMENT.PO.NOT_FOUND")
            {
                message.LastError = ex.Code;
                message.ProcessedAtUtc = timeProvider.GetUtcNow();
                await workflowDb.SaveChangesAsync(cancellationToken);
                logger.LogWarning(ex, "Approval outcome {MessageId} was terminally rejected for tenant {TenantId}", message.Id, tenantId);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ProcurementRuleException)
            {
                message.LastError = ex is ProcurementRuleException procurementError ? procurementError.Code : ex.GetType().Name;
                await workflowDb.SaveChangesAsync(cancellationToken);
                logger.LogWarning(ex, "Approval outcome {MessageId} for tenant {TenantId} remains pending", message.Id, tenantId);
            }
        }
    }
}
