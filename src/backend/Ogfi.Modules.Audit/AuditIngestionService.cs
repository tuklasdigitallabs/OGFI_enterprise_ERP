using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Audit.Persistence;

namespace Ogfi.Modules.Audit;

public sealed class AuditIngestionService(AuditDbContext dbContext, TimeProvider timeProvider)
{
    private const int MaximumEvidenceBytes = 16_384;
    private const int MaximumEvidenceStringLength = 2_000;
    private static readonly HashSet<string> ForbiddenEvidenceNames = new(StringComparer.Ordinal)
    {
        "password", "passwd", "secret", "token", "accesstoken", "refreshtoken", "authorization",
        "cookie", "setcookie", "clientsecret", "apikey", "privatekey"
    };

    public async Task<AuditEvent> IngestAsync(
        AuditMaterialActionRecordedV1 message,
        CancellationToken cancellationToken = default)
    {
        ValidateMessage(message);
        var safeEvidence = NormalizeSafeEvidence(message.SafeEvidenceJson);

        if (message.SourceEventId is Guid sourceEventId)
        {
            var existing = await dbContext.AuditEvents.AsNoTracking().SingleOrDefaultAsync(
                x => x.TenantId == message.TenantId
                     && x.SourceModule == message.SourceModule.Trim().ToUpperInvariant()
                     && x.SourceEventId == sourceEventId
                     && x.Action == message.Action.Trim().ToUpperInvariant(),
                cancellationToken);
            if (existing is not null)
            {
                if (existing.ResourceId != message.ResourceId || !JsonEquivalent(existing.SafeEvidenceJson, safeEvidence))
                {
                    throw new AuditRuleException(
                        "AUDIT.INGESTION.IDENTITY_CONFLICT",
                        "The audit ingestion identity is already associated with different safe evidence.");
                }
                return existing;
            }
        }

        var auditEvent = new AuditEvent
        {
            Id = message.EventId == Guid.Empty ? Guid.NewGuid() : message.EventId,
            TenantId = message.TenantId,
            ActorType = message.ActorType.Trim().ToUpperInvariant(),
            ActorUserId = message.ActorUserId,
            ActorMembershipId = message.ActorMembershipId,
            Action = message.Action.Trim().ToUpperInvariant(),
            SourceModule = message.SourceModule.Trim().ToUpperInvariant(),
            ResourceType = message.ResourceType.Trim().ToUpperInvariant(),
            ResourceId = message.ResourceId,
            ResourceRevision = message.ResourceRevision,
            LegalEntityId = message.LegalEntityId,
            OutletId = message.OutletId,
            BusinessDate = message.BusinessDate,
            OccurredAtUtc = message.OccurredAtUtc,
            Outcome = message.Outcome.Trim().ToUpperInvariant(),
            ErrorCode = Optional(message.ErrorCode),
            CorrelationId = message.CorrelationId.Trim(),
            CausationId = Optional(message.CausationId),
            SourceEventId = message.SourceEventId,
            SafeEvidenceJson = safeEvidence,
            PurchaseOrderId = message.PurchaseOrderId,
            WorkflowInstanceId = message.WorkflowInstanceId,
            ApprovalTaskId = message.ApprovalTaskId,
            ApprovalDecisionId = message.ApprovalDecisionId,
            GoodsReceiptId = message.GoodsReceiptId,
            InventoryMovementId = message.InventoryMovementId,
            FinanceSourcePostingId = message.FinanceSourcePostingId,
            JournalId = message.JournalId,
            CreatedAtUtc = timeProvider.GetUtcNow()
        };

        dbContext.AuditEvents.Add(auditEvent);
        await dbContext.SaveChangesAsync(cancellationToken);
        return auditEvent;
    }

    private static void ValidateMessage(AuditMaterialActionRecordedV1 message)
    {
        if (message.TenantId == Guid.Empty || message.ResourceId == Guid.Empty)
        {
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", "Tenant and resource identifiers are required.");
        }
        Required(message.Action, 120, "action");
        Required(message.SourceModule, 80, "source module");
        Required(message.ResourceType, 100, "resource type");
        Required(message.CorrelationId, 64, "correlation identifier");
        OptionalBounded(message.ErrorCode, 120, "error code");
        OptionalBounded(message.CausationId, 128, "causation identifier");
        if (message.ResourceRevision is <= 0)
        {
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", "Resource revision must be positive when supplied.");
        }

        var actorType = Required(message.ActorType, 20, "actor type").ToUpperInvariant();
        if (actorType is not (AuditActorTypes.User or AuditActorTypes.Worker or AuditActorTypes.System))
        {
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", "Actor type is not supported.");
        }
        var outcome = Required(message.Outcome, 20, "outcome").ToUpperInvariant();
        if (outcome is not (AuditOutcomes.Succeeded or AuditOutcomes.Failed or AuditOutcomes.Rejected))
        {
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", "Outcome is not supported.");
        }
        if (outcome != AuditOutcomes.Succeeded && string.IsNullOrWhiteSpace(message.ErrorCode))
        {
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", "Failed and rejected evidence requires a safe error code.");
        }
        if (message.OccurredAtUtc == default)
        {
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", "Occurrence time is required.");
        }
    }

    private static string NormalizeSafeEvidence(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AuditRuleException("AUDIT.EVIDENCE.INVALID", "Safe evidence JSON is required.");
        }
        if (Encoding.UTF8.GetByteCount(value) > MaximumEvidenceBytes)
        {
            throw new AuditRuleException("AUDIT.EVIDENCE.TOO_LARGE", "Safe evidence exceeds the bounded size.");
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 12 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new AuditRuleException("AUDIT.EVIDENCE.INVALID", "Safe evidence must be a JSON object.");
            }
            ValidateElement(document.RootElement);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new AuditRuleException("AUDIT.EVIDENCE.INVALID", $"Safe evidence is not valid bounded JSON: {ex.Message}");
        }
    }

    private static void ValidateElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (ForbiddenEvidenceNames.Contains(NormalizeEvidenceName(property.Name)))
                {
                    throw new AuditRuleException("AUDIT.EVIDENCE.SECRET_REJECTED", "Safe evidence contains a forbidden secret-bearing field.");
                }
                ValidateElement(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateElement(item);
        }
        else if (element.ValueKind == JsonValueKind.String && element.GetString()?.Length > MaximumEvidenceStringLength)
        {
            throw new AuditRuleException("AUDIT.EVIDENCE.TOO_LARGE", "Safe evidence contains an oversized string value.");
        }
    }

    private static string Required(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximumLength)
        {
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", $"Audit {field} is required and bounded to {maximumLength} characters.");
        }
        return value.Trim();
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void OptionalBounded(string? value, int maximumLength, string field)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length > maximumLength)
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", $"Audit {field} is bounded to {maximumLength} characters.");
    }

    private static string NormalizeEvidenceName(string value)
        => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool JsonEquivalent(string left, string right)
        => JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
}
