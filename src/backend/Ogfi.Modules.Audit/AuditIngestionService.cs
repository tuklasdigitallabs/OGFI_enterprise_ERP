using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Audit.Persistence;

namespace Ogfi.Modules.Audit;

public sealed class AuditIngestionService(AuditDbContext dbContext, TimeProvider timeProvider)
{
    private const int MaximumEvidenceBytes = 16_384;
    private const int MaximumEvidenceStringLength = 2_000;
    private static readonly HashSet<string> AllowedEvidenceNames = new(StringComparer.Ordinal)
    {
        "status", "linecount", "decision", "quantity", "movementtype", "postingstatus", "reasoncode",
        "result", "items", "stage"
    };

    public async Task<AuditEvent> IngestAsync(
        AuditMaterialActionRecordedV1 message,
        CancellationToken cancellationToken = default)
    {
        ValidateMessage(message);
        var safeEvidence = NormalizeSafeEvidence(message.SafeEvidenceJson);

        if (message.SourceEventId is not null)
        {
            var existing = await FindExistingAsync(message, cancellationToken);
            if (existing is not null)
            {
                EnsureEquivalent(existing, message.ResourceId, safeEvidence);
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
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return auditEvent;
        }
        catch (DbUpdateException exception) when (
            message.SourceEventId is not null
            && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.ChangeTracker.Clear();
            var existing = await FindExistingAsync(message, cancellationToken);
            if (existing is null) throw;
            EnsureEquivalent(existing, message.ResourceId, safeEvidence);
            return existing;
        }
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
        if (actorType is not (AuditActorTypes.Human or AuditActorTypes.System or AuditActorTypes.Integration or AuditActorTypes.SupportElevation))
        {
            throw new AuditRuleException("AUDIT.INGESTION.INVALID", "Actor type is not supported.");
        }
        if (actorType == AuditActorTypes.SupportElevation)
        {
            throw new AuditRuleException(
                "AUDIT.INGESTION.SUPPORT_ELEVATION_RESERVED",
                "Support-elevation evidence is reserved until a controlled support action is implemented.");
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
            ValidateAllowedEvidence(document.RootElement);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new AuditRuleException("AUDIT.EVIDENCE.INVALID", $"Safe evidence is not valid bounded JSON: {ex.Message}");
        }
    }

    private static void ValidateAllowedEvidence(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!AllowedEvidenceNames.Contains(NormalizeEvidenceName(property.Name)))
                {
                    throw new AuditRuleException(
                        "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED",
                        $"Safe evidence field '{property.Name}' is not in the minimum-proof allow-list.");
                }
                ValidateAllowedEvidence(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateAllowedEvidence(item);
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

    private Task<AuditEvent?> FindExistingAsync(
        AuditMaterialActionRecordedV1 message,
        CancellationToken cancellationToken)
    {
        var sourceModule = message.SourceModule.Trim().ToUpperInvariant();
        var action = message.Action.Trim().ToUpperInvariant();
        return dbContext.AuditEvents.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == message.TenantId
                 && x.SourceModule == sourceModule
                 && x.SourceEventId == message.SourceEventId
                 && x.Action == action,
            cancellationToken);
    }

    private static void EnsureEquivalent(AuditEvent existing, Guid resourceId, string safeEvidence)
    {
        if (existing.ResourceId != resourceId || !JsonEquivalent(existing.SafeEvidenceJson, safeEvidence))
        {
            throw new AuditRuleException(
                "AUDIT.INGESTION.IDENTITY_CONFLICT",
                "The audit ingestion identity is already associated with different safe evidence.");
        }
    }

    private static bool JsonEquivalent(string left, string right)
        => JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
}
