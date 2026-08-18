using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Inventory;
using Ogfi.Modules.Inventory.Persistence;
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
    InventoryDbContext inventoryDb,
    GoodsReceiptPostedConsumer inventoryConsumer,
    IStockConsequenceAttemptHook attemptHook,
    TimeProvider timeProvider,
    ILogger<StockConsequenceProcessor> logger,
    OutboxDeliveryStore? deliveries = null,
    ProcessorFailureRecorder? failureRecorder = null)
{
    public async Task<ProcessorIterationResult> ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (deliveries is null)
        {
            await ProcessLegacySingleConsumerAsync(tenantId, cancellationToken);
            return ProcessorIterationResult.Empty;
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
            var context = new ProcessorMessageContext(
                tenantId, "INVENTORY", ProcessorCodes.Inventory, message.Id,
                message.CausationId, message.CorrelationId, "OUTBOX_MESSAGE", message.Id);
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
                context = context with
                {
                    ResourceType = "GOODS_RECEIPT",
                    ResourceId = payload.GoodsReceiptId,
                    LegalEntityId = payload.LegalEntityId,
                    OutletId = payload.OutletId
                };
                await inventoryConsumer.ApplyAsync(payload, cancellationToken);
                await attemptHook.AfterApplyAsync(tenantId, message.Id, cancellationToken);
                await deliveries.MarkCompletedAsync(tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, cancellationToken);
                if (failureRecorder is not null) await failureRecorder.RecoverAsync(context, cancellationToken);
            }
            catch (InventoryRuleException ex)
            {
                await deliveries.MarkTerminalRejectedAsync(
                    tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, ex.Code, cancellationToken);
                if (failureRecorder is not null) await failureRecorder.RecordAsync(context, ex, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt inventory consequence {MessageId} was terminally rejected for tenant {TenantId}", message.Id, tenantId);
            }
            catch (JsonException ex)
            {
                await deliveries.MarkTerminalRejectedAsync(
                    tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, "INVENTORY.EVENT.INVALID_JSON", cancellationToken);
                if (failureRecorder is not null) await failureRecorder.RecordAsync(context, ex, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt inventory consequence {MessageId} contained invalid JSON for tenant {TenantId}", message.Id, tenantId);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                await deliveries.MarkRetryAsync(
                    tenantId, message.Id, OutboxConsumerCodes.InventoryStockConsequence, ex.GetType().Name, cancellationToken);
                if (failureRecorder is not null) await failureRecorder.RecordAsync(context, ex, cancellationToken);
                logger.LogWarning(ex, "Goods Receipt inventory consequence {MessageId} remains retryable for tenant {TenantId}", message.Id, tenantId);
            }

            await deliveries.TryFinalizeMessageAsync(tenantId, message.Id, cancellationToken);
        }
        var state = await deliveries.GetConsumerStateAsync(
            tenantId, OutboxConsumerCodes.InventoryStockConsequence, cancellationToken);
        return state with { CurrentOrLastSourceId = messages.LastOrDefault()?.Id };
    }

    public async Task<ReplayDispatchResult> ReplaySourceAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken)
    {
        var message = await procurementDb.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == command.TenantId && x.Id == command.OriginalSourceEventId
                 && x.Type == "Procurement.GoodsReceiptPosted" && x.SchemaVersion == 1,
            cancellationToken);
        if (message is null)
            return new ReplayDispatchResult(false, SafeErrorCode: "INVENTORY.REPLAY.SOURCE_NOT_FOUND",
                SafeDetailJson: "{\"reasonCode\":\"SOURCE_NOT_FOUND\"}");
        try
        {
            var payload = DeserializeAndValidate(message.Id, command.TenantId, message.Payload);
            await inventoryConsumer.ApplyAsync(payload, cancellationToken);
            return new ReplayDispatchResult(true, "INVENTORY_SOURCE_EVENT", message.Id,
                SafeDetailJson: "{\"status\":\"SUCCEEDED\"}");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var classified = ProcessorFailureClassifier.Classify(ex);
            return new ReplayDispatchResult(false, SafeErrorCode: classified.SafeErrorCode,
                SafeDetailJson: JsonSerializer.Serialize(new { reasonCode = classified.SafeErrorCode }),
                Retryable: classified.Replayable);
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

        foreach (var initialMessage in pending)
        {
            var messageId = initialMessage.Id;
            var message = initialMessage;

            for (var transientRetry = 0; transientRetry < 3; transientRetry++)
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
                    break;
                }
                catch (Exception ex) when (IsTransientSerializationConflict(ex) && transientRetry < 2)
                {
                    logger.LogWarning(
                        ex,
                        "Goods Receipt consequence {MessageId} hit a transient serialization conflict; bounded retry {RetryNumber} for tenant {TenantId}",
                        messageId,
                        transientRetry + 1,
                        tenantId);

                    inventoryDb.ChangeTracker.Clear();
                    procurementDb.ChangeTracker.Clear();
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (transientRetry + 1)), cancellationToken);
                    message = await procurementDb.OutboxMessages.SingleAsync(
                        x => x.TenantId == tenantId && x.Id == messageId,
                        cancellationToken);
                }
                catch (InventoryRuleException ex)
                {
                    message.LastError = ex.Code;
                    message.ProcessedAtUtc = timeProvider.GetUtcNow();
                    await procurementDb.SaveChangesAsync(cancellationToken);
                    logger.LogWarning(ex, "Goods Receipt consequence {MessageId} was terminally rejected for tenant {TenantId}", message.Id, tenantId);
                    break;
                }
                catch (JsonException ex)
                {
                    message.LastError = "INVENTORY.EVENT.INVALID_JSON";
                    message.ProcessedAtUtc = timeProvider.GetUtcNow();
                    await procurementDb.SaveChangesAsync(cancellationToken);
                    logger.LogWarning(ex, "Goods Receipt consequence {MessageId} contained invalid JSON for tenant {TenantId}", message.Id, tenantId);
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    message.LastError = ex.GetType().Name;
                    await procurementDb.SaveChangesAsync(cancellationToken);
                    logger.LogWarning(ex, "Goods Receipt consequence {MessageId} remains pending for tenant {TenantId}", message.Id, tenantId);
                    break;
                }
            }
        }
    }

    private static bool IsTransientSerializationConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg
                && (pg.SqlState == PostgresErrorCodes.SerializationFailure
                    || pg.SqlState == PostgresErrorCodes.DeadlockDetected))
            {
                return true;
            }
        }

        return false;
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
