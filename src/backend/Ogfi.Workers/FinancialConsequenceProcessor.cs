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
    ILogger<FinancialConsequenceProcessor> logger)
{
    public async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
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

                await financePosting.ApplyAsync(tenantId, payload, timeProvider.GetUtcNow(), cancellationToken);
                await attemptHook.AfterApplyAsync(tenantId, message.Id, cancellationToken);
                await deliveries.MarkCompletedAsync(tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, cancellationToken);
            }
            catch (FinanceRuleException ex)
            {
                await deliveries.MarkTerminalRejectedAsync(
                    tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, ex.Code, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt Finance consequence {MessageId} was terminally rejected for tenant {TenantId}", message.Id, tenantId);
            }
            catch (JsonException ex)
            {
                await deliveries.MarkTerminalRejectedAsync(
                    tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, "FINANCE.EVENT.INVALID_JSON", cancellationToken);
                logger.LogWarning(ex, "Goods Receipt Finance consequence {MessageId} contained invalid JSON for tenant {TenantId}", message.Id, tenantId);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await deliveries.MarkRetryAsync(
                    tenantId, message.Id, OutboxConsumerCodes.FinanceFinancialConsequence, ex.GetType().Name, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt Finance consequence {MessageId} remains retryable for tenant {TenantId}", message.Id, tenantId);
            }

            await deliveries.TryFinalizeMessageAsync(tenantId, message.Id, cancellationToken);
        }
    }
}
