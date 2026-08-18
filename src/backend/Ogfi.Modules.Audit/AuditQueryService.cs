using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Audit.Persistence;

namespace Ogfi.Modules.Audit;

public sealed record AuditEventQuery(
    string? SourceModule = null,
    string? Action = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    string? CorrelationId = null,
    Guid? PurchaseOrderId = null,
    Guid? GoodsReceiptId = null,
    Guid? JournalId = null,
    DateTimeOffset? OccurredFromUtc = null,
    DateTimeOffset? OccurredToUtc = null,
    int Offset = 0,
    int Limit = 50);

public sealed record Rs01TraceQuery(
    Guid? PurchaseOrderId = null,
    Guid? GoodsReceiptId = null,
    Guid? JournalId = null,
    string? CorrelationId = null,
    string? State = null,
    int Offset = 0,
    int Limit = 50);

public sealed class AuditQueryService(AuditDbContext dbContext)
{
    public async Task<IReadOnlyList<AuditEvent>> QueryEventsAsync(
        Guid tenantId,
        AuditEventQuery request,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(request.Offset, request.Limit);
        var query = dbContext.AuditEvents.AsNoTracking().Where(x => x.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(request.SourceModule))
            query = query.Where(x => x.SourceModule == request.SourceModule.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(request.Action))
            query = query.Where(x => x.Action == request.Action.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(request.ResourceType))
            query = query.Where(x => x.ResourceType == request.ResourceType.Trim().ToUpperInvariant());
        if (request.ResourceId is Guid resourceId) query = query.Where(x => x.ResourceId == resourceId);
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            query = query.Where(x => x.CorrelationId == request.CorrelationId.Trim());
        if (request.PurchaseOrderId is Guid purchaseOrderId) query = query.Where(x => x.PurchaseOrderId == purchaseOrderId);
        if (request.GoodsReceiptId is Guid goodsReceiptId) query = query.Where(x => x.GoodsReceiptId == goodsReceiptId);
        if (request.JournalId is Guid journalId) query = query.Where(x => x.JournalId == journalId);
        if (request.OccurredFromUtc is DateTimeOffset occurredFromUtc) query = query.Where(x => x.OccurredAtUtc >= occurredFromUtc);
        if (request.OccurredToUtc is DateTimeOffset occurredToUtc) query = query.Where(x => x.OccurredAtUtc <= occurredToUtc);
        if (request.OccurredFromUtc > request.OccurredToUtc)
            throw new AuditRuleException("AUDIT.QUERY.INVALID", "The audit occurrence range is invalid.");

        return await query.OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id)
            .Skip(request.Offset).Take(request.Limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Rs01TraceProjection>> QueryTracesAsync(
        Guid tenantId,
        Rs01TraceQuery request,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(request.Offset, request.Limit);
        var query = dbContext.Rs01TraceProjections.AsNoTracking().Where(x => x.TenantId == tenantId);

        if (request.PurchaseOrderId is Guid purchaseOrderId) query = query.Where(x => x.PurchaseOrderId == purchaseOrderId);
        if (request.GoodsReceiptId is Guid goodsReceiptId) query = query.Where(x => x.GoodsReceiptId == goodsReceiptId);
        if (request.JournalId is Guid journalId) query = query.Where(x => x.JournalId == journalId);
        if (!string.IsNullOrWhiteSpace(request.State)) query = query.Where(x => x.State == request.State.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            var correlationId = request.CorrelationId.Trim();
            var purchaseOrderIds = dbContext.AuditEvents.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CorrelationId == correlationId && x.PurchaseOrderId != null)
                .Select(x => x.PurchaseOrderId!.Value)
                .Distinct();
            query = query.Where(x => x.CorrelationId == correlationId || purchaseOrderIds.Contains(x.PurchaseOrderId));
        }

        return await query.OrderByDescending(x => x.LastEventAtUtc).ThenByDescending(x => x.Id)
            .Skip(request.Offset).Take(request.Limit).ToListAsync(cancellationToken);
    }

    public Task<Rs01TraceProjection?> GetTraceAsync(
        Guid tenantId,
        Guid traceId,
        CancellationToken cancellationToken = default)
        => dbContext.Rs01TraceProjections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == traceId, cancellationToken);

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100)
            throw new AuditRuleException("AUDIT.QUERY.BOUNDS", "Offset must be non-negative and limit must be between 1 and 100.");
    }
}
