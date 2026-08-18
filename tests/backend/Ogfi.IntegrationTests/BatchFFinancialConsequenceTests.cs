using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ogfi.Modules.Finance;
using Ogfi.Modules.Finance.Persistence;
using Ogfi.Workers;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchFFinancialConsequenceTests(BatchFFixture fixture) : IClassFixture<BatchFFixture>
{
    [Fact]
    public async Task Valid_goods_receipt_creates_one_balanced_traceable_journal_with_independent_fanout()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var context = await fixture.CreateBusinessContextAsync();
        var setup = await ConfigureFinanceAsync(client, context);
        var sourceEvent = await fixture.CreateGoodsReceiptPostedEventAsync(context, lineNetAmount: 1800m);

        await fixture.ProcessInventoryAsync(BatchFFixture.TenantA);
        Assert.Equal(1, await fixture.CountInventoryMovementsAsync(sourceEvent.EventId));
        Assert.Equal(OutboxDeliveryStatuses.Completed,
            (await fixture.GetDeliveryAsync(sourceEvent.EventId, OutboxConsumerCodes.InventoryStockConsequence)).Status);

        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        Assert.Equal(1, await fixture.CountSourcePostingsAsync(sourceEvent.EventId));
        Assert.Equal(1, await fixture.CountJournalsAsync(sourceEvent.EventId));
        Assert.Equal(OutboxDeliveryStatuses.Completed,
            (await fixture.GetDeliveryAsync(sourceEvent.EventId, OutboxConsumerCodes.FinanceFinancialConsequence)).Status);

        var source = await fixture.GetSourcePostingAsync(sourceEvent.EventId);
        Assert.Equal(FinanceStatuses.Posted, source.Status);
        Assert.Null(source.ErrorCode);
        Assert.NotNull(source.JournalId);
        Assert.Equal(sourceEvent.GoodsReceiptId, source.GoodsReceiptId);
        Assert.Equal(context.OutletId, source.OutletId);

        var journal = await fixture.GetJournalAsync(sourceEvent.EventId);
        Assert.Equal(source.Id, journal.SourcePostingId);
        Assert.Equal(sourceEvent.GoodsReceiptId, journal.GoodsReceiptId);
        Assert.Equal(1800m, journal.TotalDebit);
        Assert.Equal(1800m, journal.TotalCredit);
        Assert.Equal(sourceEvent.Payload.CorrelationId, journal.CorrelationId);
        Assert.Equal(setup.PostingRuleId, journal.PostingRuleVersionId);

        var lines = await fixture.GetJournalLinesAsync(journal.Id);
        Assert.Equal(2, lines.Count);
        var debit = Assert.Single(lines, x => x.Debit == 1800m && x.Credit == 0m);
        var credit = Assert.Single(lines, x => x.Credit == 1800m && x.Debit == 0m);
        Assert.Equal(setup.InventoryAccountId, debit.AccountId);
        Assert.Equal("1100", debit.AccountCode);
        Assert.Equal(setup.GrniAccountId, credit.AccountId);
        Assert.Equal("2100", credit.AccountCode);
        Assert.All(lines, x =>
        {
            Assert.Equal(sourceEvent.GoodsReceiptLineId, x.GoodsReceiptLineId);
            Assert.Equal(sourceEvent.PurchaseOrderId, x.PurchaseOrderId);
            Assert.Equal(sourceEvent.PurchaseOrderLineId, x.PurchaseOrderLineId);
            Assert.Equal(context.StockLocationId, x.StockLocationId);
            Assert.Equal(context.CatalogItemId, x.CatalogItemId);
            Assert.Equal(1800m, x.SourceLineAmount);
        });

        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        Assert.Equal(1, await fixture.CountSourcePostingsAsync(sourceEvent.EventId));
        Assert.Equal(1, await fixture.CountJournalsAsync(sourceEvent.EventId));

        using (var statusList = await client.GetAsync($"/api/finance/source-postings?goodsReceiptId={sourceEvent.GoodsReceiptId}"))
        {
            statusList.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await statusList.Content.ReadAsStringAsync());
            Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal("POSTED", json.RootElement[0].GetProperty("status").GetString());
        }
        using (var journalDetail = await client.GetAsync($"/api/finance/journals/{journal.Id}"))
        {
            journalDetail.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await journalDetail.Content.ReadAsStringAsync());
            Assert.Equal(1800m, json.RootElement.GetProperty("totalDebit").GetDecimal());
            Assert.Equal(1800m, json.RootElement.GetProperty("totalCredit").GetDecimal());
            Assert.Equal(2, json.RootElement.GetProperty("lines").GetArrayLength());
        }

        using var bob = fixture.CreateAuthenticatedClient(BatchFFixture.BobSubject);
        using var denied = await bob.GetAsync("/api/finance/journals");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("AUTH.PERMISSION_DENIED", await ProblemCodeAsync(denied));
    }

    [Fact]
    public async Task Missing_posting_rule_fails_explicitly_then_replays_same_source_after_remediation()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var context = await fixture.CreateBusinessContextAsync();
        var setup = await ConfigureFinanceAsync(client, context, includeRule: false);
        var sourceEvent = await fixture.CreateGoodsReceiptPostedEventAsync(context, lineNetAmount: 725m);

        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        var failed = await fixture.GetSourcePostingAsync(sourceEvent.EventId);
        Assert.Equal(FinanceStatuses.Failed, failed.Status);
        Assert.Equal("FINANCE.POSTING_RULE.MISSING", failed.ErrorCode);
        Assert.Null(failed.JournalId);
        Assert.Equal(0, await fixture.CountJournalsAsync(sourceEvent.EventId));
        Assert.Equal(OutboxDeliveryStatuses.Completed,
            (await fixture.GetDeliveryAsync(sourceEvent.EventId, OutboxConsumerCodes.FinanceFinancialConsequence)).Status);

        var ruleId = await CreateRuleAsync(client, setup.BookId, setup.InventoryAccountId, setup.GrniAccountId);
        using var replay = await client.PostAsync($"/api/finance/source-postings/{failed.Id}/replay", null);
        replay.EnsureSuccessStatusCode();
        using (var json = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()))
        {
            Assert.Equal(failed.Id, json.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("POSTED", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("replayCount").GetInt32());
            Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("journalId").GetGuid());
        }

        Assert.Equal(1, await fixture.CountSourcePostingsAsync(sourceEvent.EventId));
        Assert.Equal(1, await fixture.CountJournalsAsync(sourceEvent.EventId));
        Assert.Equal(ruleId, (await fixture.GetJournalAsync(sourceEvent.EventId)).PostingRuleVersionId);
    }

    [Fact]
    public async Task Closed_period_and_currency_mismatch_fail_without_journal_or_fallback()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var closedContext = await fixture.CreateBusinessContextAsync();
        var closedSetup = await ConfigureFinanceAsync(client, closedContext);
        await fixture.SetPeriodStatusAsync(closedSetup.PeriodId, FinanceStatuses.Closed);
        var closedEvent = await fixture.CreateGoodsReceiptPostedEventAsync(closedContext, lineNetAmount: 500m);
        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        var closedSource = await fixture.GetSourcePostingAsync(closedEvent.EventId);
        Assert.Equal(FinanceStatuses.Failed, closedSource.Status);
        Assert.Equal("FINANCE.PERIOD.NOT_OPEN", closedSource.ErrorCode);
        Assert.Equal(0, await fixture.CountJournalsAsync(closedEvent.EventId));

        var currencyContext = await fixture.CreateBusinessContextAsync();
        await ConfigureFinanceAsync(client, currencyContext, functionalCurrency: "USD");
        var currencyEvent = await fixture.CreateGoodsReceiptPostedEventAsync(currencyContext, currency: "PHP", lineNetAmount: 800m);
        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        var currencySource = await fixture.GetSourcePostingAsync(currencyEvent.EventId);
        Assert.Equal(FinanceStatuses.Failed, currencySource.Status);
        Assert.Equal("FINANCE.CURRENCY.UNSUPPORTED", currencySource.ErrorCode);
        Assert.Equal(0, await fixture.CountJournalsAsync(currencyEvent.EventId));
    }

    [Fact]
    public async Task Finance_transient_retry_and_crash_after_commit_recovery_are_idempotent()
    {
        using var client = fixture.CreateAuthenticatedClient();

        var beforeContext = await fixture.CreateBusinessContextAsync();
        await ConfigureFinanceAsync(client, beforeContext);
        var beforeEvent = await fixture.CreateGoodsReceiptPostedEventAsync(beforeContext, lineNetAmount: 900m);
        fixture.FinanceAttemptHook.FailBeforeOnce();
        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        var beforeDelivery = await fixture.GetDeliveryAsync(beforeEvent.EventId, OutboxConsumerCodes.FinanceFinancialConsequence);
        Assert.Equal(OutboxDeliveryStatuses.RetryPending, beforeDelivery.Status);
        Assert.Equal(nameof(TimeoutException), beforeDelivery.LastError);
        Assert.Equal(0, await fixture.CountSourcePostingsAsync(beforeEvent.EventId));
        Assert.Equal(0, await fixture.CountJournalsAsync(beforeEvent.EventId));

        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        Assert.Equal(OutboxDeliveryStatuses.Completed,
            (await fixture.GetDeliveryAsync(beforeEvent.EventId, OutboxConsumerCodes.FinanceFinancialConsequence)).Status);
        Assert.Equal(1, await fixture.CountSourcePostingsAsync(beforeEvent.EventId));
        Assert.Equal(1, await fixture.CountJournalsAsync(beforeEvent.EventId));

        var afterContext = await fixture.CreateBusinessContextAsync();
        await ConfigureFinanceAsync(client, afterContext);
        var afterEvent = await fixture.CreateGoodsReceiptPostedEventAsync(afterContext, lineNetAmount: 1100m);
        fixture.FinanceAttemptHook.FailAfterOnce();
        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        var afterDelivery = await fixture.GetDeliveryAsync(afterEvent.EventId, OutboxConsumerCodes.FinanceFinancialConsequence);
        Assert.Equal(OutboxDeliveryStatuses.RetryPending, afterDelivery.Status);
        Assert.Equal(nameof(TimeoutException), afterDelivery.LastError);
        Assert.Equal(1, await fixture.CountSourcePostingsAsync(afterEvent.EventId));
        Assert.Equal(1, await fixture.CountJournalsAsync(afterEvent.EventId));

        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        Assert.Equal(OutboxDeliveryStatuses.Completed,
            (await fixture.GetDeliveryAsync(afterEvent.EventId, OutboxConsumerCodes.FinanceFinancialConsequence)).Status);
        Assert.Equal(1, await fixture.CountSourcePostingsAsync(afterEvent.EventId));
        Assert.Equal(1, await fixture.CountJournalsAsync(afterEvent.EventId));
    }

    [Fact]
    public async Task Finance_tenant_security_posted_immutability_and_model_snapshot_are_enforced()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var context = await fixture.CreateBusinessContextAsync();
        await ConfigureFinanceAsync(client, context);
        var validEvent = await fixture.CreateGoodsReceiptPostedEventAsync(context, lineNetAmount: 600m);
        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        var journal = await fixture.GetJournalAsync(validEvent.EventId);
        var journalLines = await fixture.GetJournalLinesAsync(journal.Id);

        var forgedEvent = await fixture.CreateGoodsReceiptPostedEventAsync(context, payloadTenantId: BatchFFixture.TenantB);
        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        var forgedDelivery = await fixture.GetDeliveryAsync(forgedEvent.EventId, OutboxConsumerCodes.FinanceFinancialConsequence);
        Assert.Equal(OutboxDeliveryStatuses.TerminalRejected, forgedDelivery.Status);
        Assert.Equal("FINANCE.EVENT.TENANT_MISMATCH", forgedDelivery.LastError);
        Assert.Equal(0, await fixture.CountSourcePostingsAsync(forgedEvent.EventId));
        Assert.Equal(0, await fixture.CountJournalsAsync(forgedEvent.EventId));

        var states = await fixture.GetBatchFRlsStatesAsync();
        Assert.Equal(8, states.Count);
        Assert.All(states, state =>
        {
            Assert.True(state.Enabled, $"{state.Schema}.{state.Table} must have RLS enabled.");
            Assert.True(state.Forced, $"{state.Schema}.{state.Table} must force RLS.");
            Assert.Equal(1, state.PolicyCount);
        });

        var crossTenant = await Assert.ThrowsAsync<PostgresException>(() => fixture.AttemptCrossTenantJournalInsertAsRuntimeRoleAsync(context));
        Assert.Equal("42501", crossTenant.SqlState);

        var journalMutation = await Assert.ThrowsAsync<PostgresException>(() => fixture.AttemptPostedJournalUpdateAsync(journal.Id));
        Assert.Equal("55000", journalMutation.SqlState);
        var lineMutation = await Assert.ThrowsAsync<PostgresException>(() => fixture.AttemptJournalLineDeleteAsync(journalLines[0].Id));
        Assert.Equal("55000", lineMutation.SqlState);

        await using var scope = fixture.Services.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Database.HasPendingModelChanges());
    }

    private static async Task<FinanceSetupEvidence> ConfigureFinanceAsync(
        HttpClient client,
        FinanceBusinessContext context,
        bool includeRule = true,
        string functionalCurrency = "PHP")
    {
        Guid bookId;
        using (var response = await client.PostAsJsonAsync("/api/finance/books", new
        {
            legalEntityId = context.LegalEntityId,
            code = $"BOOK-{context.LegalEntityId:N}"[..20].ToUpperInvariant(),
            name = "Primary Finance Book",
            functionalCurrency
        }))
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Book setup failed {(int)response.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            bookId = json.RootElement.GetProperty("id").GetGuid();
        }

        var inventoryAccountId = await CreateAccountAsync(client, bookId, "1100", "Inventory Asset", FinanceAccountTypes.Asset, FinanceNormalBalances.Debit);
        var grniAccountId = await CreateAccountAsync(client, bookId, "2100", "Goods Received Not Invoiced", FinanceAccountTypes.Liability, FinanceNormalBalances.Credit);

        Guid periodId;
        using (var response = await client.PostAsJsonAsync("/api/finance/periods", new
        {
            accountingBookId = bookId,
            name = "August 2026",
            startBusinessDate = "2026-08-01",
            endBusinessDate = "2026-08-31"
        }))
        {
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            periodId = json.RootElement.GetProperty("id").GetGuid();
        }
        using (var open = await client.PostAsync($"/api/finance/periods/{periodId}/open", null))
        {
            open.EnsureSuccessStatusCode();
        }

        var ruleId = includeRule
            ? await CreateRuleAsync(client, bookId, inventoryAccountId, grniAccountId)
            : (Guid?)null;
        return new FinanceSetupEvidence(bookId, inventoryAccountId, grniAccountId, periodId, ruleId);
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client, Guid bookId, string code, string name, string accountType, string normalBalance)
    {
        using var response = await client.PostAsJsonAsync("/api/finance/accounts", new
        {
            accountingBookId = bookId,
            code,
            name,
            accountType,
            normalBalance
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateRuleAsync(HttpClient client, Guid bookId, Guid debitAccountId, Guid creditAccountId)
    {
        using var response = await client.PostAsJsonAsync("/api/finance/posting-rules/goods-receipt/versions", new
        {
            accountingBookId = bookId,
            version = 1,
            name = "Goods Receipt to Inventory and GRNI",
            effectiveFromBusinessDate = "2026-01-01",
            effectiveToBusinessDate = (string?)null,
            debitAccountId,
            creditAccountId
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }

    private sealed record FinanceSetupEvidence(Guid BookId, Guid InventoryAccountId, Guid GrniAccountId, Guid PeriodId, Guid? PostingRuleId);
}
