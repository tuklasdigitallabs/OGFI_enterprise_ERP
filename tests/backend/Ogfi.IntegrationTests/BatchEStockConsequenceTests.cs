using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchEStockConsequenceTests(BatchEFixture fixture) : IClassFixture<BatchEFixture>
{
    [Fact]
    public async Task Partial_receipt_posts_once_and_materializes_one_normalized_movement_and_position()
    {
        var po = await fixture.CreatePurchaseOrderAsync(ProcurementStatuses.Approved);
        using var client = fixture.CreateAuthenticatedClient();
        var draft = await CreateReceiptAsync(client, po, 2m, BatchEFixture.ActiveLocation);

        Assert.Equal(HttpStatusCode.Created, draft.Response.StatusCode);
        Assert.Equal("DRAFT", draft.Body.GetProperty("status").GetString());
        var line = draft.Body.GetProperty("lines")[0];
        Assert.Equal(2m, line.GetProperty("receivedQuantity").GetDecimal());
        Assert.Equal(10m, line.GetProperty("normalizedBaseQuantity").GetDecimal());
        var originalItemName = line.GetProperty("catalogItemNameSnapshot").GetString();

        await fixture.ChangeCatalogItemNameAsync(po.CatalogItemId, "Changed after Goods Receipt draft");
        var historical = await client.GetFromJsonAsync<JsonElement>($"/api/procurement/goods-receipts/{draft.Id}");
        Assert.Equal(originalItemName, historical.GetProperty("lines")[0].GetProperty("catalogItemNameSnapshot").GetString());

        var posted = await PostReceiptAsync(client, draft.Id, draft.ETag, "post-once");
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Equal("POSTED", (await posted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(2m, await fixture.GetReceivedQuantityAsync(po.PurchaseOrderLineId));
        Assert.Equal(1, await fixture.CountReceiptOutboxAsync(draft.Id));
        Assert.Equal(0, await fixture.CountMovementsAsync(draft.Id));

        var replay = await PostReceiptAsync(client, draft.Id, draft.ETag, "post-once");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("X-OGFI-Idempotent-Replay").Single());
        Assert.Equal(1, await fixture.CountReceiptOutboxAsync(draft.Id));

        var eventId = await fixture.GetReceiptEventIdAsync(draft.Id);
        await fixture.ProcessStockConsequenceAsync(BatchEFixture.TenantA);
        Assert.Equal(1, await fixture.CountMovementsAsync(draft.Id));
        Assert.Equal(1, await fixture.CountSourceEffectsAsync(eventId));
        Assert.Equal(10m, await fixture.GetPositionQuantityAsync(po.CatalogItemId));

        var movement = await fixture.GetMovementAsync(draft.Id);
        Assert.Equal(eventId, movement.SourceEventId);
        Assert.Equal(po.PurchaseOrderId, movement.PurchaseOrderId);
        Assert.Equal(po.PurchaseOrderLineId, movement.PurchaseOrderLineId);
        Assert.Equal(po.CatalogItemId, movement.CatalogItemId);
        Assert.Equal(BatchEFixture.ActiveLocation, movement.StockLocationId);
        Assert.Equal(10m, movement.QuantityBaseUom);
        Assert.Equal("PURCHASE_RECEIPT", movement.MovementType);
        Assert.False(string.IsNullOrWhiteSpace(movement.CorrelationId));

        await fixture.RedeliverReceiptEventAsync(draft.Id);
        await fixture.ProcessStockConsequenceAsync(BatchEFixture.TenantA);
        Assert.Equal(1, await fixture.CountMovementsAsync(draft.Id));
        Assert.Equal(1, await fixture.CountSourceEffectsAsync(eventId));
        Assert.Equal(10m, await fixture.GetPositionQuantityAsync(po.CatalogItemId));

        var appendOnly = await Assert.ThrowsAsync<PostgresException>(() => fixture.AttemptMovementUpdateAsync(movement.Id));
        Assert.Equal("55000", appendOnly.SqlState);

        await fixture.CorruptPositionAsync(po.CatalogItemId, 999m);
        var movementCountBeforeRebuild = await fixture.CountMovementsAsync(draft.Id);
        var rebuild = await client.PostAsJsonAsync("/api/inventory/stock-positions/rebuild", new { outletId = BatchEFixture.OutletA, catalogItemId = po.CatalogItemId });
        Assert.Equal(HttpStatusCode.OK, rebuild.StatusCode);
        Assert.Equal(10m, await fixture.GetPositionQuantityAsync(po.CatalogItemId));
        Assert.Equal(movementCountBeforeRebuild, await fixture.CountMovementsAsync(draft.Id));
    }

    [Fact]
    public async Task Receipt_validation_rejects_nonapproved_overreceipt_bad_location_bad_conversion_and_unauthorized_access()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var draftPo = await fixture.CreatePurchaseOrderAsync(ProcurementStatuses.Draft);
        var nonApproved = await CreateReceiptAsync(client, draftPo, 1m, BatchEFixture.ActiveLocation);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nonApproved.Response.StatusCode);
        Assert.Equal("PROCUREMENT.GR.PO_NOT_APPROVED", await ProblemCodeAsync(nonApproved.Response));

        var approved = await fixture.CreatePurchaseOrderAsync(ProcurementStatuses.Approved);
        var over = await CreateReceiptAsync(client, approved, 11m, BatchEFixture.ActiveLocation);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.Response.StatusCode);
        Assert.Equal("PROCUREMENT.GR.OVER_RECEIPT", await ProblemCodeAsync(over.Response));

        var inactive = await CreateReceiptAsync(client, approved, 1m, BatchEFixture.InactiveLocation);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactive.Response.StatusCode);
        Assert.Equal("PROCUREMENT.GR.STOCK_LOCATION_INVALID", await ProblemCodeAsync(inactive.Response));

        var wrongOutlet = await CreateReceiptAsync(client, approved, 1m, BatchEFixture.OtherOutletLocation);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongOutlet.Response.StatusCode);
        Assert.Equal("PROCUREMENT.GR.STOCK_LOCATION_INVALID", await ProblemCodeAsync(wrongOutlet.Response));

        var foreignLocation = await CreateReceiptAsync(client, approved, 1m, BatchEFixture.ForeignLocation);
        Assert.Equal(HttpStatusCode.NotFound, foreignLocation.Response.StatusCode);

        var valid = await CreateReceiptAsync(client, approved, 1m, BatchEFixture.ActiveLocation);
        Assert.Equal(HttpStatusCode.Created, valid.Response.StatusCode);
        await fixture.CorruptReceiptConversionAsync(valid.Id, approved.ConversionNumerator + 1);
        var invalidUom = await PostReceiptAsync(client, valid.Id, valid.ETag, "bad-uom");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidUom.StatusCode);
        Assert.Equal("PROCUREMENT.GR.UOM_INVALID", await ProblemCodeAsync(invalidUom));

        using var unauthorized = fixture.CreateAuthenticatedClient(BatchEFixture.BobSubject);
        var denied = await CreateReceiptAsync(unauthorized, approved, 1m, BatchEFixture.ActiveLocation);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Response.StatusCode);
        Assert.Equal("AUTH.PERMISSION_DENIED", await ProblemCodeAsync(denied.Response));

        var foreignReceipt = await client.GetAsync($"/api/procurement/goods-receipts/{BatchEFixture.ForeignReceipt}");
        Assert.Equal(HttpStatusCode.NotFound, foreignReceipt.StatusCode);
    }

    [Fact]
    public async Task Concurrent_partial_receipts_cannot_bypass_zero_overreceipt_tolerance()
    {
        var po = await fixture.CreatePurchaseOrderAsync(ProcurementStatuses.Approved, 10m);
        using var clientA = fixture.CreateAuthenticatedClient();
        using var clientB = fixture.CreateAuthenticatedClient();
        var first = await CreateReceiptAsync(clientA, po, 6m, BatchEFixture.ActiveLocation);
        var second = await CreateReceiptAsync(clientB, po, 6m, BatchEFixture.ActiveLocation);
        Assert.Equal(HttpStatusCode.Created, first.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.Response.StatusCode);

        var results = await Task.WhenAll(
            PostReceiptAsync(clientA, first.Id, first.ETag, $"concurrent-{first.Id:N}"),
            PostReceiptAsync(clientB, second.Id, second.ETag, $"concurrent-{second.Id:N}"));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, x => x.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity);
        Assert.Equal(6m, await fixture.GetReceivedQuantityAsync(po.PurchaseOrderLineId));

        var successfulReceipt = results[0].StatusCode == HttpStatusCode.OK ? first.Id : second.Id;
        var rejectedReceipt = successfulReceipt == first.Id ? second.Id : first.Id;
        Assert.Equal(1, await fixture.CountReceiptOutboxAsync(successfulReceipt));
        Assert.Equal(0, await fixture.CountReceiptOutboxAsync(rejectedReceipt));
        await fixture.ProcessStockConsequenceAsync(BatchEFixture.TenantA);
        Assert.Equal(1, await fixture.CountMovementsAsync(successfulReceipt));
        Assert.Equal(30m, await fixture.GetPositionQuantityAsync(po.CatalogItemId));
    }

    [Fact]
    public async Task Worker_retries_transient_failure_and_recovers_after_inventory_commit_before_acknowledgement()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var beforePo = await fixture.CreatePurchaseOrderAsync(ProcurementStatuses.Approved);
        var beforeReceipt = await CreateReceiptAsync(client, beforePo, 1m, BatchEFixture.ActiveLocation);
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(client, beforeReceipt.Id, beforeReceipt.ETag, "transient-before")).StatusCode);
        var beforeEvent = await fixture.GetReceiptEventIdAsync(beforeReceipt.Id);

        fixture.AttemptHook.FailBeforeOnce();
        await fixture.ProcessStockConsequenceAsync(BatchEFixture.TenantA);
        var pendingBefore = await fixture.GetOutboxStateAsync(beforeEvent);
        Assert.Null(pendingBefore.ProcessedAtUtc);
        Assert.Equal(nameof(TimeoutException), pendingBefore.LastError);
        Assert.Equal(0, await fixture.CountMovementsAsync(beforeReceipt.Id));

        await fixture.ProcessStockConsequenceAsync(BatchEFixture.TenantA);
        Assert.NotNull((await fixture.GetOutboxStateAsync(beforeEvent)).ProcessedAtUtc);
        Assert.Equal(1, await fixture.CountMovementsAsync(beforeReceipt.Id));

        var afterPo = await fixture.CreatePurchaseOrderAsync(ProcurementStatuses.Approved);
        var afterReceipt = await CreateReceiptAsync(client, afterPo, 1m, BatchEFixture.ActiveLocation);
        Assert.Equal(HttpStatusCode.OK, (await PostReceiptAsync(client, afterReceipt.Id, afterReceipt.ETag, "crash-after")).StatusCode);
        var afterEvent = await fixture.GetReceiptEventIdAsync(afterReceipt.Id);

        fixture.AttemptHook.FailAfterOnce();
        await fixture.ProcessStockConsequenceAsync(BatchEFixture.TenantA);
        var pendingAfter = await fixture.GetOutboxStateAsync(afterEvent);
        Assert.Null(pendingAfter.ProcessedAtUtc);
        Assert.Equal(nameof(TimeoutException), pendingAfter.LastError);
        Assert.Equal(1, await fixture.CountMovementsAsync(afterReceipt.Id));
        Assert.Equal(1, await fixture.CountSourceEffectsAsync(afterEvent));

        await fixture.ProcessStockConsequenceAsync(BatchEFixture.TenantA);
        Assert.NotNull((await fixture.GetOutboxStateAsync(afterEvent)).ProcessedAtUtc);
        Assert.Equal(1, await fixture.CountMovementsAsync(afterReceipt.Id));
        Assert.Equal(1, await fixture.CountSourceEffectsAsync(afterEvent));
    }

    [Fact]
    public async Task Tenant_forgery_forced_rls_and_committed_model_snapshots_are_enforced()
    {
        var forgedEvent = await fixture.CreateForgedTenantEventAsync();
        await fixture.ProcessStockConsequenceAsync(BatchEFixture.TenantA);
        var forgedState = await fixture.GetOutboxStateAsync(forgedEvent);
        Assert.NotNull(forgedState.ProcessedAtUtc);
        Assert.Equal("INVENTORY.EVENT.TENANT_MISMATCH", forgedState.LastError);
        Assert.Equal(0, await fixture.CountSourceEffectsAsync(forgedEvent));

        var states = await fixture.GetBatchERlsStatesAsync();
        Assert.Equal(6, states.Count);
        Assert.All(states, state =>
        {
            Assert.True(state.Enabled, $"{state.Schema}.{state.Table} must have RLS enabled.");
            Assert.True(state.Forced, $"{state.Schema}.{state.Table} must force RLS.");
            Assert.Equal(1, state.PolicyCount);
        });

        var blocked = await Assert.ThrowsAsync<PostgresException>(() => fixture.AttemptCrossTenantStockPositionInsertAsRuntimeRoleAsync());
        Assert.Equal("42501", blocked.SqlState);

        await using var scope = fixture.Services.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.HasPendingModelChanges());
        Assert.False(scope.ServiceProvider.GetRequiredService<ProcurementDbContext>().Database.HasPendingModelChanges());
    }

    private static async Task<ReceiptDraft> CreateReceiptAsync(HttpClient client, SeededPurchaseOrder po, decimal quantity, Guid stockLocationId)
    {
        var response = await client.PostAsJsonAsync("/api/procurement/goods-receipts", new
        {
            purchaseOrderId = po.PurchaseOrderId,
            stockLocationId,
            lines = new[] { new { purchaseOrderLineId = po.PurchaseOrderLineId, quantity } }
        });
        if (!response.IsSuccessStatusCode) return new ReceiptDraft(Guid.Empty, string.Empty, default, response);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new ReceiptDraft(body.GetProperty("id").GetGuid(), response.Headers.ETag?.Tag ?? throw new InvalidOperationException("Goods Receipt ETag missing."), body, response);
    }

    private static Task<HttpResponseMessage> PostReceiptAsync(HttpClient client, Guid receiptId, string etag, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/procurement/goods-receipts/{receiptId}/post");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed record ReceiptDraft(Guid Id, string ETag, JsonElement Body, HttpResponseMessage Response);
}
