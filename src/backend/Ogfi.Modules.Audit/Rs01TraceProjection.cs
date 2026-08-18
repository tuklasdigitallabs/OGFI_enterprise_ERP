using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Audit.Persistence;

namespace Ogfi.Modules.Audit;

public static class Rs01TraceStates
{
    public const string Complete = "COMPLETE";
    public const string Incomplete = "INCOMPLETE";
    public const string Invalid = "INVALID";
}

public static class Rs01MaterialStages
{
    public const string PurchaseOrderSubmission = "PURCHASE_ORDER.SUBMITTED";
    public const string WorkflowApprovalDecision = "WORKFLOW.APPROVAL_DECIDED";
    public const string ProcurementApprovalApplication = "PURCHASE_ORDER.APPROVAL_APPLIED";
    public const string GoodsReceiptPosting = "GOODS_RECEIPT.POSTED";
    public const string InventoryMovementCreation = "INVENTORY.MOVEMENT.CREATED";
    public const string FinanceJournalPosting = "FINANCE.JOURNAL.POSTED";

    public const string ProcurementOwner = "PROCUREMENT";
    public const string WorkflowOwner = "WORKFLOW";
    public const string InventoryOwner = "INVENTORY";
    public const string FinanceOwner = "FINANCE";
}

public sealed class Rs01TraceProjection
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid? WorkflowInstanceId { get; set; }
    public Guid? ApprovalTaskId { get; set; }
    public Guid? ApprovalDecisionId { get; set; }
    public Guid? GoodsReceiptId { get; set; }
    public required string InventoryMovementIdsJson { get; set; }
    public int InventoryMovementCount { get; set; }
    public Guid? FinanceSourcePostingId { get; set; }
    public Guid? JournalId { get; set; }
    public required string CorrelationId { get; set; }
    public required string State { get; set; }
    public required string MissingLinksJson { get; set; }
    public string? InvalidReason { get; set; }
    public int EvidenceEventCount { get; set; }
    public DateTimeOffset FirstEventAtUtc { get; set; }
    public DateTimeOffset LastEventAtUtc { get; set; }
    public DateTimeOffset RebuiltAtUtc { get; set; }
}

public sealed class Rs01TraceProjectionService(AuditDbContext dbContext, TimeProvider timeProvider)
{
    private const int MaximumEventsPerPurchaseOrder = 5_000;
    private static readonly StageRequirement[] RequiredStages =
    [
        new("PURCHASE_ORDER_SUBMISSION", Rs01MaterialStages.ProcurementOwner, Rs01MaterialStages.PurchaseOrderSubmission,
            x => x.PurchaseOrderId is not null),
        new("WORKFLOW_APPROVAL_DECISION", Rs01MaterialStages.WorkflowOwner, Rs01MaterialStages.WorkflowApprovalDecision,
            x => x.WorkflowInstanceId is not null && x.ApprovalTaskId is not null && x.ApprovalDecisionId is not null),
        new("PROCUREMENT_APPROVAL_APPLICATION", Rs01MaterialStages.ProcurementOwner, Rs01MaterialStages.ProcurementApprovalApplication,
            x => x.ApprovalDecisionId is not null),
        new("GOODS_RECEIPT_POSTING", Rs01MaterialStages.ProcurementOwner, Rs01MaterialStages.GoodsReceiptPosting,
            x => x.GoodsReceiptId is not null),
        new("INVENTORY_MOVEMENT_CREATION", Rs01MaterialStages.InventoryOwner, Rs01MaterialStages.InventoryMovementCreation,
            x => x.GoodsReceiptId is not null && x.InventoryMovementId is not null),
        new("FINANCE_JOURNAL_POSTING", Rs01MaterialStages.FinanceOwner, Rs01MaterialStages.FinanceJournalPosting,
            x => x.GoodsReceiptId is not null && x.FinanceSourcePostingId is not null && x.JournalId is not null)
    ];

    public async Task<IReadOnlyList<Rs01TraceProjection>> RebuildAsync(
        Guid tenantId,
        Guid purchaseOrderId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || purchaseOrderId == Guid.Empty)
            throw new AuditRuleException("AUDIT.TRACE.INVALID", "Tenant and Purchase Order identifiers are required.");

        var events = await dbContext.AuditEvents.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PurchaseOrderId == purchaseOrderId)
            .OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id)
            .Take(MaximumEventsPerPurchaseOrder + 1)
            .ToListAsync(cancellationToken);
        if (events.Count > MaximumEventsPerPurchaseOrder)
            throw new AuditRuleException("AUDIT.TRACE.EVIDENCE_LIMIT", "The trace exceeds the bounded evidence rebuild limit.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Rs01TraceProjections
            .Where(x => x.TenantId == tenantId && x.PurchaseOrderId == purchaseOrderId)
            .ExecuteDeleteAsync(cancellationToken);
        if (events.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        var receiptIds = events.Where(x => x.GoodsReceiptId != null).Select(x => x.GoodsReceiptId!.Value).Distinct().ToArray();
        Guid?[] traceReceipts = receiptIds.Length == 0 ? [null] : receiptIds.Select(x => (Guid?)x).ToArray();
        var rebuiltAtUtc = timeProvider.GetUtcNow();
        var projections = new List<Rs01TraceProjection>(traceReceipts.Length);

        foreach (var receiptId in traceReceipts)
        {
            var evidence = events.Where(x => x.GoodsReceiptId == null || x.GoodsReceiptId == receiptId).ToArray();
            var contradictions = new List<string>();
            var workflowInstanceId = SingleLink(evidence, x => x.WorkflowInstanceId, "workflow instance", contradictions);
            var approvalTaskId = SingleLink(evidence, x => x.ApprovalTaskId, "approval task", contradictions);
            var approvalDecisionId = SingleLink(evidence, x => x.ApprovalDecisionId, "approval decision", contradictions);
            var financeSourcePostingId = SingleLink(evidence, x => x.FinanceSourcePostingId, "Finance Source Posting", contradictions);
            var journalId = SingleLink(evidence, x => x.JournalId, "Journal", contradictions);
            var movementIds = evidence.Where(x => x.InventoryMovementId != null)
                .Select(x => x.InventoryMovementId!.Value).Distinct().Order().ToArray();
            var missing = new List<string>();
            ValidateMaterialStages(evidence, missing, contradictions);

            var state = contradictions.Count > 0
                ? Rs01TraceStates.Invalid
                : missing.Count > 0 ? Rs01TraceStates.Incomplete : Rs01TraceStates.Complete;
            var correlationId = evidence.OrderByDescending(x => x.OccurredAtUtc)
                .Select(x => x.CorrelationId).First(x => !string.IsNullOrWhiteSpace(x));
            var projection = new Rs01TraceProjection
            {
                Id = DeterministicTraceId(tenantId, purchaseOrderId, receiptId),
                TenantId = tenantId,
                PurchaseOrderId = purchaseOrderId,
                WorkflowInstanceId = workflowInstanceId,
                ApprovalTaskId = approvalTaskId,
                ApprovalDecisionId = approvalDecisionId,
                GoodsReceiptId = receiptId,
                InventoryMovementIdsJson = JsonSerializer.Serialize(movementIds),
                InventoryMovementCount = movementIds.Length,
                FinanceSourcePostingId = financeSourcePostingId,
                JournalId = journalId,
                CorrelationId = correlationId,
                State = state,
                MissingLinksJson = JsonSerializer.Serialize(missing),
                InvalidReason = contradictions.Count == 0 ? null : string.Join("; ", contradictions),
                EvidenceEventCount = evidence.Length,
                FirstEventAtUtc = evidence.Min(x => x.OccurredAtUtc),
                LastEventAtUtc = evidence.Max(x => x.OccurredAtUtc),
                RebuiltAtUtc = rebuiltAtUtc
            };
            projections.Add(projection);
        }

        dbContext.Rs01TraceProjections.AddRange(projections);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return projections;
    }

    private static void ValidateMaterialStages(
        IReadOnlyCollection<AuditEvent> evidence,
        ICollection<string> missing,
        ICollection<string> contradictions)
    {
        foreach (var requirement in RequiredStages)
        {
            var actionEvidence = evidence
                .Where(x => string.Equals(x.Action, requirement.Action, StringComparison.Ordinal))
                .ToArray();
            if (actionEvidence.Any(x => !string.Equals(x.SourceModule, requirement.Owner, StringComparison.Ordinal)))
            {
                contradictions.Add($"{requirement.Name} evidence was recorded by a non-owning module.");
            }

            var ownerEvidence = actionEvidence
                .Where(x => string.Equals(x.SourceModule, requirement.Owner, StringComparison.Ordinal))
                .ToArray();
            if (ownerEvidence.Any(x => !string.Equals(x.Outcome, AuditOutcomes.Succeeded, StringComparison.Ordinal)))
            {
                contradictions.Add($"{requirement.Name} contains a non-successful outcome.");
            }

            var successfulEvidence = ownerEvidence
                .Where(x => string.Equals(x.Outcome, AuditOutcomes.Succeeded, StringComparison.Ordinal))
                .ToArray();
            if (successfulEvidence.Length == 0)
            {
                missing.Add(requirement.Name);
            }
            else if (successfulEvidence.Any(x => !requirement.HasRequiredLinks(x)))
            {
                contradictions.Add($"{requirement.Name} is missing its owner-specific links.");
            }
        }
    }

    private static Guid? SingleLink(
        IEnumerable<AuditEvent> events,
        Func<AuditEvent, Guid?> selector,
        string label,
        ICollection<string> contradictions)
    {
        var values = events.Select(selector).Where(x => x != null).Select(x => x!.Value).Distinct().ToArray();
        if (values.Length > 1) contradictions.Add($"Contradictory {label} links were recorded.");
        return values.Length == 1 ? values[0] : null;
    }

    private static Guid DeterministicTraceId(Guid tenantId, Guid purchaseOrderId, Guid? goodsReceiptId)
    {
        var identity = $"RS01|{tenantId:N}|{purchaseOrderId:N}|{goodsReceiptId?.ToString("N") ?? "PENDING"}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private sealed record StageRequirement(
        string Name,
        string Owner,
        string Action,
        Func<AuditEvent, bool> HasRequiredLinks);
}
