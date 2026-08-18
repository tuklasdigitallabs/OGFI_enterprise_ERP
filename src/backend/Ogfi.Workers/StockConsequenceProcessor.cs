using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Inventory;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Workers;

public interface IStockConsequenceAttemptHook
{
    Task BeforeApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);
    Task AfterApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);
}

public sealed class NoopStockConsequenceAttemptHook : IStockConsequenceAttemptHook
{
    public Task BeforeApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AfterApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class StockConsequenceProcessor(
    ProcurementDbContext procurementDb,
    GoodsReceiptPostedConsumer inventoryConsumer,
    OutboxDeliveryStore deliveries,
    IStockConsequenceAttemptHook attemptHook,
    ILogger<StockConsequenceProcessor> logger)
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
                tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, cancellationToken);
            if (delivery.Status is OutboxDeliveryStatuses.Completed or OutboxDeliveryStatuses.TerminalRejected)
            {
                await deliveries.TryFinalizeMessageAsync(tenantId, message.Id, cancellationToken);
                continue;
            }

            await deliveries.MarkAttemptAsync(tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, cancellationToken);
            try
            {
                await attemptHook.BeforeApplyAsync(tenantId, message.Id, cancellationToken);
                var payload = JsonSerializer.Deserialize<GoodsReceiptPostedV1>(message.Payload)
                    ?? throw new InventoryRuleException("INVENTORY.EVENT.INVALID", "GoodsReceiptPosted payload is empty.");
                if (payload.TenantId != tenantId || payload.EventId != message.Id)
                {
                    throw new InventoryRuleException("INVENTORY.EVENT.TENANT_MISMATCH", "GoodsReceiptPosted envelope identity does not match its payload.");
                }

                await inventoryConsumer.ApplyAsync(payload, cancellationToken);
                await attemptHook.AfterApplyAsync(tenantId, message.Id, cancellationToken);
                await deliveries.MarkCompletedAsync(tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, cancellationToken);
            }
            catch (InventoryRuleException ex)
            {
                await deliveries.MarkTerminalRejectedAsync(
                    tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, ex.Code, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt inventory consequence {MessageId} was terminally rejected for tenant {TenantId}", message.Id, tenantId);
            }
            catch (JsonException ex)
            {
                await deliveries.MarkTerminalRejectedAsync(
                    tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, "INVENTORY.EVENT.INVALID_JSON", cancellationToken);
                logger.LogWarning(ex, "Goods Receipt inventory consequence {MessageId} contained invalid JSON for tenant {TenantId}", message.Id, tenantId);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await deliveries.MarkRetryAsync(
                    tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, ex.GetType().Name, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt inventory consequence {MessageId} remains retryable for tenant {TenantId}", message.Id, tenantId);
            }

            await deliveries.TryFinalizeMessageAsync(tenantId, message.Id, cancellationToken);
        }
    }
}
