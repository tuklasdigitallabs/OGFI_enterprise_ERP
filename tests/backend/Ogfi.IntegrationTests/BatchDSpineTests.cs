using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ogfi.Modules.Catalog;
using Ogfi.Modules.Workflow.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchDSpineTests(BatchDFixture fixture) : IClassFixture<BatchDFixture>
{
    [Fact]
    public async Task Approval_spine_is_versioned_idempotent_authorized_and_procurement_owned()
    {
        using var alice = fixture.CreateAuthenticatedClient();

        Guid definitionV1Id;
        using (var defineV1 = await alice.PostAsJsonAsync("/api/workflow/definitions/purchase-order/versions", new
        {
            version = 1,
            name = "RS-01 Purchase Order Approval v1"
        }))
        {
            Assert.Equal(HttpStatusCode.Created, defineV1.StatusCode);
            using var json = JsonDocument.Parse(await defineV1.Content.ReadAsStringAsync());
            definitionV1Id = json.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        }

        var po1 = await CreateSubmittedPurchaseOrderAsync(alice, "ONE");
        await fixture.ProcessApprovalSpineAsync(BatchDFixture.TenantA);
        Assert.Equal(1, await fixture.CountWorkflowInstancesAsync(po1));

        await fixture.RedeliverApprovalStartAsync(po1);
        await fixture.ProcessApprovalSpineAsync(BatchDFixture.TenantA);
        Assert.Equal(1, await fixture.CountWorkflowInstancesAsync(po1));

        Guid task1;
        Guid instance1;
        long subjectVersion1;
        using (var inbox = await alice.GetAsync("/api/workflow/approval-inbox?limit=50"))
        {
            inbox.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await inbox.Content.ReadAsStringAsync());
            var row = json.RootElement.EnumerateArray().Single(x => x.GetProperty("purchaseOrderId").GetGuid() == po1);
            task1 = row.GetProperty("taskId").GetGuid();
            instance1 = row.GetProperty("instanceId").GetGuid();
            subjectVersion1 = row.GetProperty("subjectVersion").GetInt64();
        }

        using (var detail = await alice.GetAsync($"/api/workflow/tasks/{task1}"))
        {
            detail.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Equal(definitionV1Id, json.RootElement.GetProperty("definitionVersionId").GetGuid());
            Assert.Equal(1, json.RootElement.GetProperty("definitionVersion").GetInt32());
        }

        using (var defineV2 = await alice.PostAsJsonAsync("/api/workflow/definitions/purchase-order/versions", new
        {
            version = 2,
            name = "RS-01 Purchase Order Approval v2"
        }))
        {
            Assert.Equal(HttpStatusCode.Created, defineV2.StatusCode);
        }

        using (var detailAfterNewVersion = await alice.GetAsync($"/api/workflow/tasks/{task1}"))
        {
            detailAfterNewVersion.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await detailAfterNewVersion.Content.ReadAsStringAsync());
            Assert.Equal(definitionV1Id, json.RootElement.GetProperty("definitionVersionId").GetGuid());
            Assert.Equal(1, json.RootElement.GetProperty("definitionVersion").GetInt32());
        }

        using (var bob = fixture.CreateAuthenticatedClient(BatchDFixture.BobSubject))
        {
            using var bobInbox = await bob.GetAsync("/api/workflow/approval-inbox");
            Assert.Equal(HttpStatusCode.Forbidden, bobInbox.StatusCode);
            Assert.Equal("AUTH.PERMISSION_DENIED", await ReadErrorCodeAsync(bobInbox));

            using var bobApprove = await bob.PostAsync($"/api/workflow/tasks/{task1}/approve", null);
            Assert.Equal(HttpStatusCode.Forbidden, bobApprove.StatusCode);
            Assert.Equal("AUTH.PERMISSION_DENIED", await ReadErrorCodeAsync(bobApprove));
        }

        var foreignTask = await fixture.CreateForeignWorkflowTaskAsync();
        using (var foreign = await alice.GetAsync($"/api/workflow/tasks/{foreignTask}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        Guid decisionId;
        using (var approve = await alice.PostAsync($"/api/workflow/tasks/{task1}/approve", null))
        {
            approve.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await approve.Content.ReadAsStringAsync());
            decisionId = json.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("APPROVED", json.RootElement.GetProperty("decision").GetString());
            Assert.Equal(BatchDFixture.UserAlice, json.RootElement.GetProperty("actorUserId").GetGuid());
        }
        using (var repeatApprove = await alice.PostAsync($"/api/workflow/tasks/{task1}/approve", null))
        {
            repeatApprove.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await repeatApprove.Content.ReadAsStringAsync());
            Assert.Equal(decisionId, json.RootElement.GetProperty("id").GetGuid());
        }

        Assert.Equal(1, await fixture.CountApprovalDecisionsAsync(task1));
        Assert.Equal(1, await fixture.CountApprovalOutcomeMessagesAsync(instance1));
        var beforeOutcome = await fixture.GetPurchaseOrderStateAsync(po1);
        Assert.Equal("SUBMITTED", beforeOutcome.Status);
        Assert.Equal(subjectVersion1, beforeOutcome.Version);

        await fixture.ProcessApprovalSpineAsync(BatchDFixture.TenantA);
        var approved = await fixture.GetPurchaseOrderStateAsync(po1);
        Assert.Equal("APPROVED", approved.Status);
        Assert.Equal(subjectVersion1 + 1, approved.Version);

        var po2 = await CreateSubmittedPurchaseOrderAsync(alice, "STALE");
        await fixture.ProcessApprovalSpineAsync(BatchDFixture.TenantA);

        Guid task2;
        Guid instance2;
        long subjectVersion2;
        using (var inbox = await alice.GetAsync("/api/workflow/approval-inbox?limit=50"))
        {
            inbox.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await inbox.Content.ReadAsStringAsync());
            var row = json.RootElement.EnumerateArray().Single(x => x.GetProperty("purchaseOrderId").GetGuid() == po2);
            task2 = row.GetProperty("taskId").GetGuid();
            instance2 = row.GetProperty("instanceId").GetGuid();
            subjectVersion2 = row.GetProperty("subjectVersion").GetInt64();
        }

        using (var detail = await alice.GetAsync($"/api/workflow/tasks/{task2}"))
        {
            detail.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Equal(2, json.RootElement.GetProperty("definitionVersion").GetInt32());
        }

        using (var approve = await alice.PostAsync($"/api/workflow/tasks/{task2}/approve", null))
        {
            approve.EnsureSuccessStatusCode();
        }
        await fixture.BumpPurchaseOrderVersionAsync(po2);
        await fixture.ProcessApprovalSpineAsync(BatchDFixture.TenantA);

        var stale = await fixture.GetPurchaseOrderStateAsync(po2);
        Assert.Equal("SUBMITTED", stale.Status);
        Assert.Equal(subjectVersion2 + 1, stale.Version);
        Assert.Equal("PROCUREMENT.PO.APPROVAL_STALE", await fixture.GetApprovalOutcomeLastErrorAsync(instance2));
    }

    [Fact]
    public async Task Workflow_tenant_tables_are_force_rls_protected()
    {
        var states = await fixture.GetWorkflowRlsStatesAsync();
        Assert.Equal(6, states.Count);
        Assert.All(states, state =>
        {
            Assert.True(state.Enabled, $"RLS is not enabled for workflow.{state.Table}");
            Assert.True(state.Forced, $"RLS is not forced for workflow.{state.Table}");
            Assert.Equal(1, state.PolicyCount);
        });

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            fixture.AttemptCrossTenantWorkflowDefinitionInsertAsRuntimeRoleAsync(BatchDFixture.TenantA, BatchDFixture.TenantB));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task Workflow_model_snapshot_matches_runtime_model()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    private static async Task<Guid> CreateSubmittedPurchaseOrderAsync(HttpClient client, string marker)
    {
        var suffix = $"{marker}-{Guid.NewGuid():N}"[..12].ToUpperInvariant();

        Guid itemId;
        using (var item = await client.PostAsJsonAsync("/api/catalog/items", new
        {
            code = $"I-{suffix}",
            name = $"Approval Item {suffix}",
            baseUomId = UomIds.Kilogram
        }))
        {
            Assert.Equal(HttpStatusCode.Created, item.StatusCode);
            using var json = JsonDocument.Parse(await item.Content.ReadAsStringAsync());
            itemId = json.RootElement.GetProperty("id").GetGuid();
        }

        Guid supplierId;
        using (var supplier = await client.PostAsJsonAsync("/api/procurement/suppliers", new
        {
            code = $"S-{suffix}",
            name = $"Approval Supplier {suffix}"
        }))
        {
            Assert.Equal(HttpStatusCode.Created, supplier.StatusCode);
            using var json = JsonDocument.Parse(await supplier.Content.ReadAsStringAsync());
            supplierId = json.RootElement.GetProperty("id").GetGuid();
        }

        Guid offerId;
        using (var offer = await client.PostAsJsonAsync("/api/procurement/supplier-offers", new
        {
            supplierId,
            catalogItemId = itemId,
            purchaseUomId = UomIds.Kilogram,
            supplierItemCode = $"SKU-{suffix}",
            unitPrice = 125m,
            currency = "PHP",
            effectiveFromBusinessDate = "2026-08-18",
            effectiveToBusinessDate = (string?)null
        }))
        {
            Assert.Equal(HttpStatusCode.Created, offer.StatusCode);
            using var json = JsonDocument.Parse(await offer.Content.ReadAsStringAsync());
            offerId = json.RootElement.GetProperty("id").GetGuid();
        }

        Guid purchaseOrderId;
        string etag;
        using (var po = await client.PostAsJsonAsync("/api/procurement/purchase-orders", new
        {
            supplierId,
            legalEntityId = BatchDFixture.LegalEntityA,
            outletId = BatchDFixture.OutletA1,
            currency = "PHP",
            lines = new[] { new { supplierOfferId = offerId, quantity = 2m } }
        }))
        {
            Assert.Equal(HttpStatusCode.Created, po.StatusCode);
            etag = po.Headers.ETag?.Tag ?? throw new InvalidOperationException("Purchase Order ETag missing.");
            using var json = JsonDocument.Parse(await po.Content.ReadAsStringAsync());
            purchaseOrderId = json.RootElement.GetProperty("id").GetGuid();
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/procurement/purchase-orders/{purchaseOrderId}/submit"))
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", $"batch-d-{marker.ToLowerInvariant()}");
            using var submit = await client.SendAsync(request);
            submit.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            Assert.Equal("SUBMITTED", json.RootElement.GetProperty("status").GetString());
        }

        return purchaseOrderId;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }
}
