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

        await dbContext.Rs01TraceProjections
            .Where(x => x.TenantId == tenantId && x.PurchaseOrderId == purchaseOrderId)
            .ExecuteDeleteAsync(cancellationToken);
        if (events.Count == 0) return [];

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
            if (workflowInstanceId is null) missing.Add("WORKFLOW_INSTANCE");
            if (approvalTaskId is null) missing.Add("APPROVAL_TASK");
            if (approvalDecisionId is null) missing.Add("APPROVAL_DECISION");
            if (receiptId is null) missing.Add("GOODS_RECEIPT");
            if (movementIds.Length == 0) missing.Add("INVENTORY_MOVEMENT");
            if (financeSourcePostingId is null) missing.Add("FINANCE_SOURCE_POSTING");
            if (journalId is null) missing.Add("JOURNAL");

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
        return projections;
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
}
