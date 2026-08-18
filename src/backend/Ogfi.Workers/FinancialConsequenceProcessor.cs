using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Finance;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Workers;

public interface IFinancialConsequenceAttemptHook
{
    Task BeforeApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);
    Task AfterApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);
}

public sealed class NoopFinancialConsequenceAttemptHook : IFinancialConsequenceAttemptHook
{
    public Task BeforeApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AfterApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FinancialConsequenceProcessor(
    ProcurementDbContext procurementDb,
    FinancePostingService financePosting,
    OutboxDeliveryStore deliveries,
    IFinancialConsequenceAttemptHook attemptHook,
    TimeProvider timeProvider,
    ILogger<FinancialConsequenceProcessor> logger,
    ProcessorFailureRecorder? failureRecorder = null)
{
    public async Task<ProcessorIterationResult> ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var messages = await procurementDb.OutboxMessages.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && x.Type == "Procurement.GoodsReceiptPosted"
                        && x.SchemaVersion == 1)
            .OrderBy(x => x.OccurredAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            var context = new ProcessorMessageContext(
                tenantId, "FINANCE", ProcessorCodes.Finance, message.Id,
                message.CausationId, message.CorrelationId, "OUTBOX_MESSAGE", message.Id);
            var delivery = await deliveries.EnsureAsync(
                tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, cancellationToken);
            if (delivery.Status is OutboxDeliveryStatuses.Completed or OutboxDeliveryStatuses.TerminalRejected)
            {
                await deliveries.TryFinalizeMessageAsync(tenantId, message.Id, cancellationToken);
                continue;
            }

            await deliveries.MarkAttemptAsync(tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, cancellationToken);
            try
            {
                await attemptHook.BeforeApplyAsync(tenantId, message.Id, cancellationToken);
                var payload = JsonSerializer.Deserialize<GoodsReceiptPostedV1>(message.Payload)
                    ?? throw new FinanceRuleException("FINANCE.EVENT.INVALID", "GoodsReceiptPosted payload is empty.");
                if (payload.TenantId != tenantId || payload.EventId != message.Id)
                {
                    throw new FinanceRuleException("FINANCE.EVENT.TENANT_MISMATCH", "GoodsReceiptPosted envelope identity does not match its payload.");
                }
                context = context with
                {
                    ResourceType = "GOODS_RECEIPT",
                    ResourceId = payload.GoodsReceiptId,
                    LegalEntityId = payload.LegalEntityId,
                    OutletId = payload.OutletId
                };

                var posting = await financePosting.ApplyAsync(tenantId, payload, timeProvider.GetUtcNow(), cancellationToken);
                if (posting.Status != FinanceStatuses.Posted)
                    throw new FinanceRuleException(
                        posting.ErrorCode ?? "FINANCE.POSTING.FAILED",
                        "Finance source posting remains failed and requires governed recovery.");
                await attemptHook.AfterApplyAsync(tenantId, message.Id, cancellationToken);
                await deliveries.MarkCompletedAsync(tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, cancellationToken);
                if (failureRecorder is not null) await failureRecorder.RecoverAsync(context, cancellationToken);
            }
            catch (FinanceRuleException ex)
            {
                var classified = ProcessorFailureClassifier.Classify(ex);
                if (classified.Classification == Modules.DurableOperations.ProcessingFailureClassifications.Business)
                    await deliveries.MarkCompletedAsync(tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, cancellationToken);
                else if (!classified.Replayable)
                    await deliveries.MarkTerminalRejectedAsync(tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, ex.Code, cancellationToken);
                else
                    await deliveries.MarkRetryAsync(tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, ex.Code, cancellationToken);
                if (failureRecorder is not null) await failureRecorder.RecordAsync(context, ex, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt Finance consequence {MessageId} failed visibly for tenant {TenantId}", message.Id, tenantId);
            }
            catch (JsonException ex)
            {
                await deliveries.MarkTerminalRejectedAsync(
                    tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, "FINANCE.EVENT.INVALID_JSON", cancellationToken);
                if (failureRecorder is not null) await failureRecorder.RecordAsync(context, ex, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt Finance consequence {MessageId} contained invalid JSON for tenant {TenantId}", message.Id, tenantId);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await deliveries.MarkRetryAsync(
                    tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, ex.GetType().Name, cancellationToken);
                if (failureRecorder is not null) await failureRecorder.RecordAsync(context, ex, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt Finance consequence {MessageId} remains retryable for tenant {TenantId}", message.Id, tenantId);
            }

            await deliveries.TryFinalizeMessageAsync(tenantId, message.Id, cancellationToken);
        }
        var state = await deliveries.GetConsumerStateAsync(
            tenantId, OutboxConsumerCodes.FinanceFinancialConsequence, cancellationToken);
        return state with { CurrentOrLastSourceId = messages.LastOrDefault()?.Id };
    }

    public async Task<ReplayDispatchResult> ReplaySourceAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken)
    {
        var source = await procurementDb.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == command.TenantId && x.Id == command.OriginalSourceEventId
                 && x.Type == "Procurement.GoodsReceiptPosted" && x.SchemaVersion == 1,
            cancellationToken);
        if (source is null)
            return new ReplayDispatchResult(false, SafeErrorCode: "FINANCE.REPLAY.SOURCE_NOT_FOUND",
                SafeDetailJson: "{\"reasonCode\":\"SOURCE_NOT_FOUND\"}");
        try
        {
            var payload = JsonSerializer.Deserialize<GoodsReceiptPostedV1>(source.Payload)
                ?? throw new FinanceRuleException("FINANCE.EVENT.INVALID", "GoodsReceiptPosted payload is empty.");
            var posting = await financePosting.ApplyAsync(command.TenantId, payload, timeProvider.GetUtcNow(), cancellationToken);
            if (posting.Status == FinanceStatuses.Failed)
                posting = await financePosting.ReplayAsync(command.TenantId, posting.Id, Guid.Empty,
                    timeProvider.GetUtcNow(), cancellationToken);
            if (posting.Status != FinanceStatuses.Posted || posting.JournalId is null)
                return new ReplayDispatchResult(false, SafeErrorCode: posting.ErrorCode ?? "FINANCE.POSTING.FAILED",
                    SafeDetailJson: JsonSerializer.Serialize(new { reasonCode = posting.ErrorCode ?? "POSTING_FAILED" }),
                    Retryable: true);
            return new ReplayDispatchResult(true, "JOURNAL", posting.JournalId,
                SafeDetailJson: "{\"status\":\"POSTED\"}");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var classified = ProcessorFailureClassifier.Classify(ex);
            return new ReplayDispatchResult(false, SafeErrorCode: classified.SafeErrorCode,
                SafeDetailJson: JsonSerializer.Serialize(new { reasonCode = classified.SafeErrorCode }),
                Retryable: classified.Replayable);
        }
    }
}
