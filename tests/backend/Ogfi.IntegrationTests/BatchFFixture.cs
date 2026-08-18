using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Catalog;
using Ogfi.Modules.Catalog.Persistence;
using Ogfi.Modules.Finance.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow.Persistence;
using Ogfi.Workers;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchFFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("a6000000-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("b6000000-0000-0000-0000-000000000001");
    public static readonly Guid UserAlice = Guid.Parse("a6111111-1111-1111-1111-111111111111");
    public static readonly Guid UserBob = Guid.Parse("a6222222-2222-2222-2222-222222222222");
    public static readonly Guid MembershipAlice = Guid.Parse("a6311111-1111-1111-1111-111111111111");
    public static readonly Guid MembershipBob = Guid.Parse("a6322222-2222-2222-2222-222222222222");
    public const string AliceSubject = "cognito|batch-f-alice";
    public const string BobSubject = "cognito|batch-f-bob";

    private static readonly DateTimeOffset FixedUtcNow = new(2026, 8, 18, 4, 0, 0, TimeSpan.Zero);
    private const string RlsTestRole = "ogfi_batch_f_rls_test";
    private readonly string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedUtcNow));
            services.RemoveAll<IStockConsequenceAttemptHook>();
            services.RemoveAll<IFinancialConsequenceAttemptHook>();
            services.AddSingleton<TestStockConsequenceAttemptHook>();
            services.AddSingleton<IStockConsequenceAttemptHook>(sp => sp.GetRequiredService<TestStockConsequenceAttemptHook>());
            services.AddSingleton<TestFinancialConsequenceAttemptHook>();
            services.AddSingleton<IFinancialConsequenceAttemptHook>(sp => sp.GetRequiredService<TestFinancialConsequenceAttemptHook>());
            services.AddScoped<OutboxDeliveryStore>();
            services.AddScoped<StockConsequenceProcessor>();
            services.AddScoped<FinancialConsequenceProcessor>();
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<FoundationDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<InventoryDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<ProcurementDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Database.MigrateAsync();
        }
        await EnsureRlsTestRoleAsync();
        await SeedIdentityAsync();
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public HttpClient CreateAuthenticatedClient(string subject = AliceSubject, Guid? tenantId = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.SubjectHeader, subject);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.TenantHeader, (tenantId ?? TenantA).ToString());
        return client;
    }

    public TestFinancialConsequenceAttemptHook FinanceAttemptHook
        => Services.GetRequiredService<TestFinancialConsequenceAttemptHook>();

    public async Task<FinanceBusinessContext> CreateBusinessContextAsync(Guid? tenantId = null)
    {
        var tenant = tenantId ?? TenantA;
        var legalEntityId = Guid.NewGuid();
        var outletId = Guid.NewGuid();
        var stockLocationId = Guid.NewGuid();
        var catalogItemId = Guid.NewGuid();
        var suffix = legalEntityId.ToString("N")[..8].ToUpperInvariant();

        await using var connection = await OpenTenantConnectionAsync(tenant);
        await using var command = new NpgsqlCommand("""
            insert into foundation.legal_entities ("Id","TenantId","Code","Name")
            values (@legal,@tenant,@legalCode,@legalName);
            insert into foundation.outlets ("Id","TenantId","LegalEntityId","Code","Name","TimeZoneId","BusinessDayStartMinutes")
            values (@outlet,@tenant,@legal,@outletCode,@outletName,'Asia/Manila',240);
            insert into catalog.items ("Id","TenantId","Code","Name","BaseUomId","Status","Version")
            values (@item,@tenant,@itemCode,@itemName,@baseUom,'ACTIVE',1);
            insert into inventory.inventory_profiles ("Id","TenantId","CatalogItemId","BaseUomId","IsStocked","NegativeStockAllowed")
            values (@profile,@tenant,@item,@baseUom,true,false);
            insert into inventory.stock_locations ("Id","TenantId","OutletId","Code","Name","LocationType","IsActive")
            values (@location,@tenant,@outlet,@locationCode,@locationName,'STORE',true);
            """, connection);
        command.Parameters.AddWithValue("legal", legalEntityId);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("legalCode", $"LE-{suffix}");
        command.Parameters.AddWithValue("legalName", $"Finance Legal Entity {suffix}");
        command.Parameters.AddWithValue("outlet", outletId);
        command.Parameters.AddWithValue("outletCode", $"OUT-{suffix}");
        command.Parameters.AddWithValue("outletName", $"Finance Outlet {suffix}");
        command.Parameters.AddWithValue("item", catalogItemId);
        command.Parameters.AddWithValue("itemCode", $"ITEM-{suffix}");
        command.Parameters.AddWithValue("itemName", $"Finance Item {suffix}");
        command.Parameters.AddWithValue("baseUom", UomIds.Kilogram);
        command.Parameters.AddWithValue("profile", Guid.NewGuid());
        command.Parameters.AddWithValue("location", stockLocationId);
        command.Parameters.AddWithValue("locationCode", $"LOC-{suffix}");
        command.Parameters.AddWithValue("locationName", $"Finance Store {suffix}");
        await command.ExecuteNonQueryAsync();

        if (tenant == TenantA)
        {
            await using var grant = new NpgsqlCommand("""
                insert into foundation.outlet_scope_grants ("Id","TenantId","MembershipId","OutletId")
                values (@id,@tenant,@membership,@outlet);
                """, connection);
            grant.Parameters.AddWithValue("id", Guid.NewGuid());
            grant.Parameters.AddWithValue("tenant", tenant);
            grant.Parameters.AddWithValue("membership", MembershipAlice);
            grant.Parameters.AddWithValue("outlet", outletId);
            await grant.ExecuteNonQueryAsync();
        }

        return new FinanceBusinessContext(tenant, legalEntityId, outletId, stockLocationId, catalogItemId, $"ITEM-{suffix}", $"Finance Item {suffix}");
    }

    public async Task<SeededFinanceEvent> CreateGoodsReceiptPostedEventAsync(
        FinanceBusinessContext context,
        string currency = "PHP",
        decimal lineNetAmount = 1800m,
        DateOnly? businessDate = null,
        Guid? payloadTenantId = null)
    {
        var eventId = Guid.NewGuid();
        var goodsReceiptId = Guid.NewGuid();
        var goodsReceiptLineId = Guid.NewGuid();
        var purchaseOrderId = Guid.NewGuid();
        var purchaseOrderLineId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var date = businessDate ?? new DateOnly(2026, 8, 18);
        var payload = new GoodsReceiptPostedV1(
            eventId,
            payloadTenantId ?? context.TenantId,
            goodsReceiptId,
            $"GR-F-{goodsReceiptId:N}"[..24].ToUpperInvariant(),
            purchaseOrderId,
            $"PO-F-{purchaseOrderId:N}"[..24].ToUpperInvariant(),
            supplierId,
            "SUP-F",
            "Finance Supplier",
            context.LegalEntityId,
            context.OutletId,
            context.StockLocationId,
            $"LOC-{context.StockLocationId:N}"[..14].ToUpperInvariant(),
            currency.ToUpperInvariant(),
            date,
            UserAlice,
            $"batch-f-{eventId:N}",
            FixedUtcNow,
            new[]
            {
                new GoodsReceiptPostedLineV1(
                    goodsReceiptLineId,
                    1,
                    purchaseOrderLineId,
                    context.CatalogItemId,
                    context.CatalogItemCode,
                    context.CatalogItemName,
                    2m,
                    UomIds.Kilogram,
                    "KG",
                    UomIds.Kilogram,
                    "KG",
                    1,
                    1,
                    2m,
                    decimal.Round(lineNetAmount / 2m, 4),
                    decimal.Round(lineNetAmount, 4))
            });

        await using var connection = await OpenTenantConnectionAsync(context.TenantId);
        await using var command = new NpgsqlCommand("""
            insert into procurement.outbox_messages
              ("Id","TenantId","Type","SchemaVersion","OccurredAtUtc","CorrelationId","CausationId","Payload","AttemptCount")
            values (@id,@tenant,'Procurement.GoodsReceiptPosted',1,@now,@correlation,@causation,@payload::jsonb,0);
            """, connection);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("tenant", context.TenantId);
        command.Parameters.AddWithValue("now", FixedUtcNow);
        command.Parameters.AddWithValue("correlation", payload.CorrelationId);
        command.Parameters.AddWithValue("causation", $"GR:{goodsReceiptId}:POST");
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync();
        return new SeededFinanceEvent(eventId, goodsReceiptId, goodsReceiptLineId, purchaseOrderId, purchaseOrderLineId, supplierId, lineNetAmount, payload);
    }

    public Task ProcessInventoryAsync(Guid tenantId) => ProcessAsync<StockConsequenceProcessor>(tenantId, (processor, token) => processor.ProcessTenantAsync(tenantId, token));
    public Task ProcessFinanceAsync(Guid tenantId) => ProcessAsync<FinancialConsequenceProcessor>(tenantId, (processor, token) => processor.ProcessTenantAsync(tenantId, token));

    public async Task ProcessBothAsync(Guid tenantId)
    {
        await ProcessInventoryAsync(tenantId);
        await ProcessFinanceAsync(tenantId);
    }

    public Task<long> CountSourcePostingsAsync(Guid eventId)
        => ScalarInt64Async(TenantA, "select count(*) from finance.source_postings where \"SourceEventId\"=@id;", eventId);

    public Task<long> CountJournalsAsync(Guid eventId)
        => ScalarInt64Async(TenantA, "select count(*) from finance.journals where \"SourceEventId\"=@id;", eventId);

    public Task<long> CountInventoryMovementsAsync(Guid eventId)
        => ScalarInt64Async(TenantA, "select count(*) from inventory.inventory_movements where \"SourceEventId\"=@id;", eventId);

    public async Task<FinanceSourcePostingEvidence> GetSourcePostingAsync(Guid eventId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("""
            select "Id","Status","ErrorCode","JournalId","AttemptCount","ReplayCount","PayloadHash","GoodsReceiptId","OutletId"
              from finance.source_postings where "SourceEventId"=@id;
            """, connection);
        command.Parameters.AddWithValue("id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Finance Source Posting not found.");
        return new FinanceSourcePostingEvidence(
            reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetString(6), reader.GetGuid(7), reader.GetGuid(8));
    }

    public async Task<JournalEvidence> GetJournalAsync(Guid eventId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("""
            select "Id","Number","TotalDebit","TotalCredit","SourcePostingId","GoodsReceiptId","PostingRuleVersionId","CorrelationId"
              from finance.journals where "SourceEventId"=@id;
            """, connection);
        command.Parameters.AddWithValue("id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Finance Journal not found.");
        return new JournalEvidence(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetGuid(6), reader.GetString(7));
    }

    public async Task<IReadOnlyList<JournalLineEvidence>> GetJournalLinesAsync(Guid journalId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("""
            select "Id","LineNumber","AccountId","AccountCodeSnapshot","DebitAmount","CreditAmount","GoodsReceiptLineId","PurchaseOrderId","PurchaseOrderLineId","StockLocationId","CatalogItemId","SourceLineAmount"
              from finance.journal_lines where "JournalId"=@journal order by "LineNumber";
            """, connection);
        command.Parameters.AddWithValue("journal", journalId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<JournalLineEvidence>();
        while (await reader.ReadAsync())
        {
            rows.Add(new JournalLineEvidence(reader.GetGuid(0), reader.GetInt32(1), reader.GetGuid(2), reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetGuid(6), reader.GetGuid(7), reader.GetGuid(8), reader.GetGuid(9), reader.GetGuid(10), reader.GetDecimal(11)));
        }
        return rows;
    }

    public async Task<OutboxDeliveryEvidence> GetDeliveryAsync(Guid eventId, string consumerCode)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("""
            select "Status","AttemptCount","LastError","CompletedAtUtc"
              from procurement.outbox_deliveries
             where "OutboxMessageId"=@event and "ConsumerCode"=@consumer;
            """, connection);
        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("consumer", consumerCode);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Outbox delivery not found.");
        return new OutboxDeliveryEvidence(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async Task<DateTimeOffset?> GetOutboxProcessedAtAsync(Guid eventId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("select \"ProcessedAtUtc\" from procurement.outbox_messages where \"Id\"=@id;", connection);
        command.Parameters.AddWithValue("id", eventId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (DateTimeOffset)value;
    }

    public async Task SetPeriodStatusAsync(Guid periodId, string status)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("update finance.accounting_periods set \"Status\"=@status where \"Id\"=@id;", connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", periodId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<RlsState>> GetBatchFRlsStatesAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select n.nspname,c.relname,c.relrowsecurity,c.relforcerowsecurity,
              (select count(*) from pg_policies p where p.schemaname=n.nspname and p.tablename=c.relname and p.policyname='tenant_isolation')
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            where (n.nspname='finance' and c.relname in ('accounting_books','accounts','accounting_periods','posting_rule_versions','source_postings','journals','journal_lines'))
               or (n.nspname='procurement' and c.relname='outbox_deliveries')
            order by n.nspname,c.relname;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var states = new List<RlsState>();
        while (await reader.ReadAsync()) states.Add(new RlsState(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetInt64(4)));
        return states;
    }

    public async Task AttemptCrossTenantJournalInsertAsRuntimeRoleAsync(FinanceBusinessContext context)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"SET ROLE {RlsTestRole}; select set_config('app.tenant_id','{TenantA}',false);");
        await using var command = new NpgsqlCommand("""
            insert into finance.journals
              ("Id","TenantId","AccountingBookId","Number","LegalEntityId","BusinessDate","PostingDate","Currency","Status","SourcePostingId","SourceEventId","GoodsReceiptId","GoodsReceiptNumber","PostingRuleVersionId","PostingRuleCodeSnapshot","PostingRuleVersionNumber","TotalDebit","TotalCredit","CorrelationId","PostedAtUtc")
            values (@id,@tenant,@book,@number,@legal,'2026-08-18','2026-08-18','PHP','POSTED',@source,@event,@receipt,'GR-FOREIGN',@rule,'RULE',1,1,1,'cross-tenant',@now);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", TenantB);
        command.Parameters.AddWithValue("book", Guid.NewGuid());
        command.Parameters.AddWithValue("number", $"JRN-X-{Guid.NewGuid():N}"[..20]);
        command.Parameters.AddWithValue("legal", context.LegalEntityId);
        command.Parameters.AddWithValue("source", Guid.NewGuid());
        command.Parameters.AddWithValue("event", Guid.NewGuid());
        command.Parameters.AddWithValue("receipt", Guid.NewGuid());
        command.Parameters.AddWithValue("rule", Guid.NewGuid());
        command.Parameters.AddWithValue("now", FixedUtcNow);
        await command.ExecuteNonQueryAsync();
    }

    public async Task AttemptPostedJournalUpdateAsync(Guid journalId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("update finance.journals set \"TotalDebit\"=\"TotalDebit\"+1,\"TotalCredit\"=\"TotalCredit\"+1 where \"Id\"=@id;", connection);
        command.Parameters.AddWithValue("id", journalId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task AttemptJournalLineDeleteAsync(Guid journalLineId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("delete from finance.journal_lines where \"Id\"=@id;", connection);
        command.Parameters.AddWithValue("id", journalLineId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedIdentityAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            insert into foundation.tenants ("Id","Code","Name") values
              ('a6000000-0000-0000-0000-000000000001','TENANT-F-A','Batch F Tenant A'),
              ('b6000000-0000-0000-0000-000000000001','TENANT-F-B','Batch F Tenant B') on conflict do nothing;
            insert into foundation.users ("Id","ExternalSubject","DisplayName") values
              ('a6111111-1111-1111-1111-111111111111','cognito|batch-f-alice','Batch F Alice'),
              ('a6222222-2222-2222-2222-222222222222','cognito|batch-f-bob','Batch F Bob') on conflict do nothing;
            """);

        await SetTenantAsync(connection, TenantA);
        await ExecuteAsync(connection, """
            insert into foundation.tenant_memberships ("Id","TenantId","UserId","Status") values
              ('a6311111-1111-1111-1111-111111111111','a6000000-0000-0000-0000-000000000001','a6111111-1111-1111-1111-111111111111','ACTIVE'),
              ('a6322222-2222-2222-2222-222222222222','a6000000-0000-0000-0000-000000000001','a6222222-2222-2222-2222-222222222222','ACTIVE') on conflict do nothing;
            insert into foundation.permission_grants ("Id","TenantId","MembershipId","PermissionCode") values
              ('a6811111-0000-0000-0000-000000000001','a6000000-0000-0000-0000-000000000001','a6311111-1111-1111-1111-111111111111','finance.setup.manage'),
              ('a6811111-0000-0000-0000-000000000002','a6000000-0000-0000-0000-000000000001','a6311111-1111-1111-1111-111111111111','finance.journal.read'),
              ('a6811111-0000-0000-0000-000000000003','a6000000-0000-0000-0000-000000000001','a6311111-1111-1111-1111-111111111111','finance.source_posting.read'),
              ('a6811111-0000-0000-0000-000000000004','a6000000-0000-0000-0000-000000000001','a6311111-1111-1111-1111-111111111111','finance.source_posting.replay') on conflict do nothing;
            """);
    }

    private async Task EnsureRlsTestRoleAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"""
            DO $$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{RlsTestRole}') THEN
                    CREATE ROLE {RlsTestRole} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                END IF;
            END $$;
            GRANT USAGE ON SCHEMA finance,procurement TO {RlsTestRole};
            GRANT SELECT,INSERT,UPDATE,DELETE ON finance.journals,finance.journal_lines,finance.source_postings TO {RlsTestRole};
            GRANT SELECT,INSERT,UPDATE ON procurement.outbox_deliveries TO {RlsTestRole};
            """);
    }

    private async Task ProcessAsync<T>(Guid tenantId, Func<T, CancellationToken, Task> action) where T : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>();
        context.SetCandidateTenant(tenantId);
        await action(scope.ServiceProvider.GetRequiredService<T>(), CancellationToken.None);
    }

    private async Task<long> ScalarInt64Async(Guid tenantId, string sql, Guid id)
    {
        await using var connection = await OpenTenantConnectionAsync(tenantId);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<NpgsqlConnection> OpenTenantConnectionAsync(Guid tenantId)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, tenantId);
        return connection;
    }

    private static Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
        => ExecuteAsync(connection, $"select set_config('app.tenant_id','{tenantId}',false);");

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class TestFinancialConsequenceAttemptHook : IFinancialConsequenceAttemptHook
{
    private int failBefore;
    private int failAfter;

    public void FailBeforeOnce() => Interlocked.Exchange(ref failBefore, 1);
    public void FailAfterOnce() => Interlocked.Exchange(ref failAfter, 1);

    public Task BeforeApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref failBefore, 0) == 1) throw new TimeoutException("Simulated Finance failure before local effect.");
        return Task.CompletedTask;
    }

    public Task AfterApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref failAfter, 0) == 1) throw new TimeoutException("Simulated Finance failure after local effect before acknowledgement.");
        return Task.CompletedTask;
    }
}

public sealed record FinanceBusinessContext(Guid TenantId, Guid LegalEntityId, Guid OutletId, Guid StockLocationId, Guid CatalogItemId, string CatalogItemCode, string CatalogItemName);
public sealed record SeededFinanceEvent(Guid EventId, Guid GoodsReceiptId, Guid GoodsReceiptLineId, Guid PurchaseOrderId, Guid PurchaseOrderLineId, Guid SupplierId, decimal LineNetAmount, GoodsReceiptPostedV1 Payload);
public sealed record FinanceSourcePostingEvidence(Guid Id, string Status, string? ErrorCode, Guid? JournalId, int AttemptCount, int ReplayCount, string PayloadHash, Guid GoodsReceiptId, Guid OutletId);
public sealed record JournalEvidence(Guid Id, string Number, decimal TotalDebit, decimal TotalCredit, Guid SourcePostingId, Guid GoodsReceiptId, Guid PostingRuleVersionId, string CorrelationId);
public sealed record JournalLineEvidence(Guid Id, int LineNumber, Guid AccountId, string AccountCode, decimal Debit, decimal Credit, Guid GoodsReceiptLineId, Guid PurchaseOrderId, Guid PurchaseOrderLineId, Guid StockLocationId, Guid CatalogItemId, decimal SourceLineAmount);
public sealed record OutboxDeliveryEvidence(string Status, int AttemptCount, string? LastError, DateTimeOffset? CompletedAtUtc);
