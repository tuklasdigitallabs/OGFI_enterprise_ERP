using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Modules.Procurement;

public sealed class PurchaseOrderApprovalOutcomeService(ProcurementDbContext dbContext)
{
    public async Task<PurchaseOrder> ApplyAsync(
        Guid tenantId,
        PurchaseOrderApprovalOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.ApprovalRound != 1 || !string.Equals(outcome.Decision, ProcurementStatuses.Approved, StringComparison.Ordinal))
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.APPROVAL_OUTCOME_INVALID", "The approval outcome is not valid for the RS-01 Purchase Order approval round.");
        }

        var purchaseOrder = await dbContext.PurchaseOrders
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == outcome.PurchaseOrderId, cancellationToken)
            ?? throw new ProcurementRuleException("PROCUREMENT.PO.NOT_FOUND", "Purchase Order does not exist.");

        if (purchaseOrder.Status == ProcurementStatuses.Approved && purchaseOrder.Version == outcome.SubjectVersion + 1)
        {
            return purchaseOrder;
        }

        if (purchaseOrder.Status != ProcurementStatuses.Submitted || purchaseOrder.Version != outcome.SubjectVersion)
        {
            throw new ProcurementRuleException(
                "PROCUREMENT.PO.APPROVAL_STALE",
                "The approval outcome references a Purchase Order revision that is no longer current and was rejected.");
        }

        purchaseOrder.Status = ProcurementStatuses.Approved;
        purchaseOrder.Version++;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ProcurementRuleException(
                "PROCUREMENT.PO.APPROVAL_STALE",
                "The Purchase Order changed while the approval outcome was being applied.");
        }

        return purchaseOrder;
    }
}
