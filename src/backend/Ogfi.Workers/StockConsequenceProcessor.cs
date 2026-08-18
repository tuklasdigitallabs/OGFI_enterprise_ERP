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
    IStockConsequenceAttemptHook attemptHook,
    TimeProvider timeProvider,
    ILogger<StockConsequenceProcessor> logger,
    OutboxDeliveryStore? deliveries = null)
{
    public async Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (deliveries is null)
        {
            await ProcessLegacySingleConsumerAsync(tenantId, cancellationToken);
            return;
        }

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
                var payload = DeserializeAndValidate(message.Id, tenantId, message.Payload);
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

    private async Task ProcessLegacySingleConsumerAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var pending = await procurementDb.OutboxMessages
            .Where(x => x.TenantId == tenantId
                        && x.ProcessedAtUtc == null
                        && x.Type == "Procurement.GoodsReceiptPosted"
                        && x.SchemaVersion == 1)
            .OrderBy(x => x.OccurredAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            message.AttemptCount++;
            try
            {
                await attemptHook.BeforeApplyAsync(tenantId, message.Id, cancellationToken);
                var payload = DeserializeAndValidate(message.Id, tenantId, message.Payload);
                await inventoryConsumer.ApplyAsync(payload, cancellationToken);
                await attemptHook.AfterApplyAsync(tenantId, message.Id, cancellationToken);
                message.LastError = null;
                message.ProcessedAtUtc = timeProvider.GetUtcNow();
                await procurementDb.SaveChangesAsync(cancellationToken);
            }
            catch (InventoryRuleException ex)
            {
                message.LastError = ex.Code;
                message.ProcessedAtUtc = timeProvider.GetUtcNow();
                await procurementDb.SaveChangesAsync(cancellationToken);
                logger.LogWarning(ex, "Goods Receipt consequence {MessageId} was terminally rejected for tenant {TenantId}", message.Id, tenantId);
            }
            catch (JsonException ex)
            {
                message.LastError = "INVENTORY.EVENT.INVALID_JSON";
                message.ProcessedAtUtc = timeProvider.GetUtcNow();
                await procurementDb.SaveChangesAsync(cancellationToken);
                logger.LogWarning(ex, "Goods Receipt consequence {MessageId} contained invalid JSON for tenant {TenantId}", message.Id, tenantId);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                message.LastError = ex.GetType().Name;
                await procurementDb.SaveChangesAsync(cancellationToken);
                logger.LogWarning(ex, "Goods Receipt consequence {MessageId} remains pending for tenant {TenantId}", message.Id, tenantId);
            }
        }
    }

    private static GoodsReceiptPostedV1 DeserializeAndValidate(Guid messageId, Guid tenantId, string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<GoodsReceiptPostedV1>(payloadJson)
            ?? throw new InventoryRuleException("INVENTORY.EVENT.INVALID", "GoodsReceiptPosted payload is empty.");
        if (payload.TenantId != tenantId || payload.EventId != messageId)
        {
            throw new InventoryRuleException("INVENTORY.EVENT.TENANT_MISMATCH", "GoodsReceiptPosted envelope identity does not match its payload.");
        }
        return payload;
    }
}
