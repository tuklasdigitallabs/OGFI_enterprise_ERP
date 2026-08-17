using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Ogfi.Modules.Catalog;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchCSpineTests(BatchCFixture fixture) : IClassFixture<BatchCFixture>
{
    private static readonly DateOnly BusinessDate = new(2026, 8, 17);

    [Fact]
    public async Task Standard_and_item_specific_uom_conversions_are_explicit()
    {
        using var client = fixture.CreateAuthenticatedClient();

        using var standard = await client.PostAsJsonAsync("/api/catalog/uom-conversions/preview", new
        {
            quantity = 1000m,
            fromUomId = UomIds.Gram,
            toUomId = UomIds.Kilogram
        });
        standard.EnsureSuccessStatusCode();
        using (var json = JsonDocument.Parse(await standard.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1m, json.RootElement.GetProperty("convertedQuantity").GetDecimal());
        }

        using var invalid = await client.PostAsJsonAsync("/api/catalog/uom-conversions/preview", new
        {
            quantity = 1m,
            fromUomId = UomIds.Kilogram,
            toUomId = UomIds.Each
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("CATALOG.UOM.CONVERSION_MISSING", await ReadErrorCodeAsync(invalid));
    }

    [Fact]
    public async Task Purchasing_master_data_preserves_snapshots_and_submits_one_approval_request()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var item = await CreateItemAsync(client, $"TOM-{suffix}", $"Tomato {suffix}");
        var supplier = await CreateSupplierAsync(client, $"SUP-{suffix}", $"Fresh Supplier {suffix}");

        using (var missingConversion = await client.PostAsJsonAsync("/api/procurement/supplier-offers", new
        {
            supplierId = supplier.Id,
            catalogItemId = item.Id,
            purchaseUomId = UomIds.Case,
            supplierItemCode = $"CASE-{suffix}",
            unitPrice = 1800m,
            currency = "PHP",
            effectiveFromBusinessDate = BusinessDate,
            effectiveToBusinessDate = (DateOnly?)null
        }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missingConversion.StatusCode);
            Assert.Equal("CATALOG.UOM.CONVERSION_MISSING", await ReadErrorCodeAsync(missingConversion));
        }

        using (var pack = await client.PostAsJsonAsync($"/api/catalog/items/{item.Id}/packaging-conversions", new
        {
            purchaseUomId = UomIds.Case,
            numerator = 10L,
            denominator = 1L,
            effectiveFromBusinessDate = BusinessDate,
            effectiveToBusinessDate = (DateOnly?)null
        }))
        {
            Assert.Equal(HttpStatusCode.Created, pack.StatusCode);
        }

        using (var profile = await client.PostAsJsonAsync("/api/inventory/profiles", new { catalogItemId = item.Id }))
        {
            Assert.Equal(HttpStatusCode.Created, profile.StatusCode);
            using var json = JsonDocument.Parse(await profile.Content.ReadAsStringAsync());
            Assert.Equal(UomIds.Kilogram, json.RootElement.GetProperty("baseUomId").GetGuid());
            Assert.False(json.RootElement.GetProperty("negativeStockAllowed").GetBoolean());
        }

        using (var location = await client.PostAsJsonAsync("/api/inventory/stock-locations", new
        {
            outletId = BatchCFixture.OutletA1,
            code = $"MAIN-{suffix}",
            name = $"Main Store {suffix}"
        }))
        {
            Assert.Equal(HttpStatusCode.Created, location.StatusCode);
        }

        Guid offerId;
        using (var offer = await client.PostAsJsonAsync("/api/procurement/supplier-offers", new
        {
            supplierId = supplier.Id,
            catalogItemId = item.Id,
            purchaseUomId = UomIds.Case,
            supplierItemCode = $"CASE-{suffix}",
            unitPrice = 1800m,
            currency = "PHP",
            effectiveFromBusinessDate = BusinessDate,
            effectiveToBusinessDate = (DateOnly?)null
        }))
        {
            Assert.Equal(HttpStatusCode.Created, offer.StatusCode);
            using var json = JsonDocument.Parse(await offer.Content.ReadAsStringAsync());
            offerId = json.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(10L, json.RootElement.GetProperty("conversionNumerator").GetInt64());
            Assert.Equal("KG", json.RootElement.GetProperty("baseUomCodeSnapshot").GetString());
        }

        Guid poId;
        string poEtag;
        using (var createPo = await client.PostAsJsonAsync("/api/procurement/purchase-orders", new
        {
            supplierId = supplier.Id,
            legalEntityId = BatchCFixture.LegalEntityA,
            outletId = BatchCFixture.OutletA1,
            currency = "PHP",
            lines = new[] { new { supplierOfferId = offerId, quantity = 2m } }
        }))
        {
            Assert.Equal(HttpStatusCode.Created, createPo.StatusCode);
            poEtag = RequireEtag(createPo);
            using var json = JsonDocument.Parse(await createPo.Content.ReadAsStringAsync());
            poId = json.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(1, json.RootElement.GetProperty("version").GetInt64());
            Assert.Equal(3600m, json.RootElement.GetProperty("totalNetAmount").GetDecimal());
            Assert.Equal($"Fresh Supplier {suffix}", json.RootElement.GetProperty("supplierNameSnapshot").GetString());
            var line = json.RootElement.GetProperty("lines")[0];
            Assert.Equal($"Tomato {suffix}", line.GetProperty("catalogItemNameSnapshot").GetString());
            Assert.Equal(10L, line.GetProperty("conversionNumerator").GetInt64());
        }

        using (var noIfMatch = await client.PutAsJsonAsync($"/api/catalog/items/{item.Id}", new { name = $"Tomato Updated {suffix}" }))
        {
            Assert.Equal((HttpStatusCode)428, noIfMatch.StatusCode);
            Assert.Equal("CONCURRENCY.IF_MATCH_REQUIRED", await ReadErrorCodeAsync(noIfMatch));
        }

        using (var updateItem = await SendJsonWithIfMatchAsync(client, HttpMethod.Put, $"/api/catalog/items/{item.Id}", item.ETag, new { name = $"Tomato Updated {suffix}" }))
        {
            updateItem.EnsureSuccessStatusCode();
        }
        using (var updateSupplier = await SendJsonWithIfMatchAsync(client, HttpMethod.Put, $"/api/procurement/suppliers/{supplier.Id}", supplier.ETag, new { name = $"Fresh Supplier Updated {suffix}" }))
        {
            updateSupplier.EnsureSuccessStatusCode();
        }

        using (var historicalPo = await client.GetAsync($"/api/procurement/purchase-orders/{poId}"))
        {
            historicalPo.EnsureSuccessStatusCode();
            poEtag = RequireEtag(historicalPo);
            using var json = JsonDocument.Parse(await historicalPo.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("version").GetInt64());
            Assert.Equal($"Fresh Supplier {suffix}", json.RootElement.GetProperty("supplierNameSnapshot").GetString());
            Assert.Equal($"Tomato {suffix}", json.RootElement.GetProperty("lines")[0].GetProperty("catalogItemNameSnapshot").GetString());
        }

        string updatedPoEtag;
        using (var updatePo = await SendJsonWithIfMatchAsync(client, HttpMethod.Put, $"/api/procurement/purchase-orders/{poId}", poEtag, new
        {
            lines = new[] { new { supplierOfferId = offerId, quantity = 3m } }
        }))
        {
            var body = await updatePo.Content.ReadAsStringAsync();
            Assert.True(updatePo.IsSuccessStatusCode, $"PO update failed with {(int)updatePo.StatusCode}: {body}");
            updatedPoEtag = RequireEtag(updatePo);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(5400m, json.RootElement.GetProperty("totalNetAmount").GetDecimal());
        }

        using (var staleUpdate = await SendJsonWithIfMatchAsync(client, HttpMethod.Put, $"/api/procurement/purchase-orders/{poId}", poEtag, new
        {
            lines = new[] { new { supplierOfferId = offerId, quantity = 4m } }
        }))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleUpdate.StatusCode);
            Assert.Equal("CONCURRENCY.CONFLICT", await ReadErrorCodeAsync(staleUpdate));
        }

        string submittedEtag;
        using (var submit = await SendWithIfMatchAsync(client, HttpMethod.Post, $"/api/procurement/purchase-orders/{poId}/submit", updatedPoEtag, "batch-c-correlation"))
        {
            submit.EnsureSuccessStatusCode();
            submittedEtag = RequireEtag(submit);
            using var json = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            Assert.Equal("SUBMITTED", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(5400m, json.RootElement.GetProperty("totalNetAmount").GetDecimal());
            Assert.Equal("2026-08-17", json.RootElement.GetProperty("businessDate").GetString());
        }

        using (var duplicateSubmit = await SendWithIfMatchAsync(client, HttpMethod.Post, $"/api/procurement/purchase-orders/{poId}/submit", submittedEtag))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicateSubmit.StatusCode);
            Assert.Equal("PROCUREMENT.PO.NOT_APPROVABLE", await ReadErrorCodeAsync(duplicateSubmit));
        }

        Assert.Equal(1, await fixture.CountApprovalRequestsAsync(poId));
        using var approval = JsonDocument.Parse(await fixture.GetApprovalRequestPayloadAsync(poId));
        Assert.Equal(poId, approval.RootElement.GetProperty("PurchaseOrderId").GetGuid());
        Assert.Equal(1, approval.RootElement.GetProperty("ApprovalRound").GetInt32());
        Assert.Equal(BatchCFixture.UserAlice, approval.RootElement.GetProperty("RequestedByUserId").GetGuid());
        Assert.Equal(BatchCFixture.LegalEntityA, approval.RootElement.GetProperty("LegalEntityId").GetGuid());
        Assert.Equal(BatchCFixture.OutletA1, approval.RootElement.GetProperty("OutletId").GetGuid());
        Assert.Equal("2026-08-17", approval.RootElement.GetProperty("BusinessDate").GetString());
        Assert.Equal("batch-c-correlation", approval.RootElement.GetProperty("CorrelationId").GetString());
        var approvalContext = approval.RootElement.GetProperty("ApprovalContext");
        Assert.Equal(5400m, approvalContext.GetProperty("PurchaseOrderTotal").GetDecimal());
        Assert.Equal("PHP", approvalContext.GetProperty("Currency").GetString());
        Assert.Equal(BatchCFixture.OutletA1, approvalContext.GetProperty("OutletId").GetGuid());
        Assert.Equal(BatchCFixture.UserAlice, approvalContext.GetProperty("RequesterUserId").GetGuid());
    }

    [Fact]
    public async Task Batch_c_tenant_tables_are_force_rls_protected_and_cross_tenant_catalog_access_is_hidden()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/api/catalog/items/{BatchCFixture.TenantBItem}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var states = await fixture.GetBatchCRlsStatesAsync();
        Assert.Equal(9, states.Count);
        Assert.All(states, state =>
        {
            Assert.True(state.Enabled, $"RLS is not enabled for {state.Table}");
            Assert.True(state.Forced, $"RLS is not forced for {state.Table}");
            Assert.Equal(1, state.PolicyCount);
        });
        Assert.False(await fixture.RuntimeRoleCanSeeCatalogItemAsync(BatchCFixture.TenantA, BatchCFixture.TenantBItem));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            fixture.AttemptCrossTenantCatalogItemInsertAsRuntimeRoleAsync(BatchCFixture.TenantA, BatchCFixture.TenantB));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static async Task<(Guid Id, string ETag)> CreateItemAsync(HttpClient client, string code, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/catalog/items", new { code, name, baseUomId = UomIds.Kilogram });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var etag = RequireEtag(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("id").GetGuid(), etag);
    }

    private static async Task<(Guid Id, string ETag)> CreateSupplierAsync(HttpClient client, string code, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/procurement/suppliers", new { code, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var etag = RequireEtag(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("id").GetGuid(), etag);
    }

    private static Task<HttpResponseMessage> SendJsonWithIfMatchAsync(HttpClient client, HttpMethod method, string uri, string etag, object body)
    {
        var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendWithIfMatchAsync(HttpClient client, HttpMethod method, string uri, string etag, string? correlationId = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (correlationId is not null) request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        return client.SendAsync(request);
    }

    private static string RequireEtag(HttpResponseMessage response)
        => response.Headers.ETag?.Tag ?? throw new InvalidOperationException("Expected an ETag response header.");

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }
}
