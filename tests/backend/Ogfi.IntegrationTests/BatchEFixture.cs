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
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Inventory;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow.Persistence;
using Ogfi.Workers;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchEFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    public static readonly Guid TenantB = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    public static readonly Guid UserAlice = Guid.Parse("e1111111-1111-1111-1111-111111111111");
    public static readonly Guid UserBob = Guid.Parse("e2222222-2222-2222-2222-222222222222");
    public static readonly Guid MembershipAlice = Guid.Parse("e3111111-1111-1111-1111-111111111111");
    public static readonly Guid MembershipBob = Guid.Parse("e3222222-2222-2222-2222-222222222222");
    public static readonly Guid LegalEntityA = Guid.Parse("e4111111-1111-1111-1111-111111111111");
    public static readonly Guid OutletA = Guid.Parse("e5111111-1111-1111-1111-111111111111");
    public static readonly Guid OutletA2 = Guid.Parse("e5222222-2222-2222-2222-222222222222");
    public static readonly Guid LegalEntityB = Guid.Parse("f4111111-1111-1111-1111-111111111111");
    public static readonly Guid OutletB = Guid.Parse("f5111111-1111-1111-1111-111111111111");
    public static readonly Guid ActiveLocation = Guid.Parse("ec111111-1111-1111-1111-111111111111");
    public static readonly Guid InactiveLocation = Guid.Parse("ec222222-2222-2222-2222-222222222222");
    public static readonly Guid OtherOutletLocation = Guid.Parse("ec333333-3333-3333-3333-333333333333");
    public static readonly Guid ForeignLocation = Guid.Parse("fc111111-1111-1111-1111-111111111111");
    public static readonly Guid ForeignReceipt = Guid.Parse("fd111111-1111-1111-1111-111111111111");
    public const string AliceSubject = "cognito|batch-e-alice";
    public const string BobSubject = "cognito|batch-e-bob";

    private static readonly Guid SupplierId = Guid.Parse("ea111111-1111-1111-1111-111111111111");
    private static readonly Guid ForeignSupplierId = Guid.Parse("fa111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
    private const string RlsTestRole = "ogfi_batch_e_rls_test";
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
            services.AddSingleton<TestStockConsequenceAttemptHook>();
            services.AddSingleton<IStockConsequenceAttemptHook>(sp => sp.GetRequiredService<TestStockConsequenceAttemptHook>());
            services.AddScoped<StockConsequenceProcessor>();
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
        }
        await EnsureRlsTestRoleAsync();
        await SeedAsync();
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

    public async Task<SeededPurchaseOrder> CreatePurchaseOrderAsync(string status, decimal orderQuantity = 10m, long conversionNumerator = 5, long conversionDenominator = 1)
    {
        var itemId = Guid.NewGuid();
        var purchaseOrderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var suffix = itemId.ToString("N")[..10].ToUpperInvariant();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, TenantA);
        await using var command = new NpgsqlCommand("""
            insert into catalog.items ("Id","TenantId","Code","Name","BaseUomId","Status","Version")
            values (@item,@tenant,@itemCode,@itemName,@baseUom,'ACTIVE',1);
            insert into inventory.inventory_profiles ("Id","TenantId","CatalogItemId","BaseUomId","IsStocked","NegativeStockAllowed")
            values (@profile,@tenant,@item,@baseUom,true,false);
            insert into procurement.purchase_orders
              ("Id","TenantId","Number","SupplierId","SupplierCodeSnapshot","SupplierNameSnapshot","LegalEntityId","OutletId","Currency","Status","BusinessDate","TotalNetAmount","Version","CreatedByUserId","CreatedAtUtc","SubmittedByUserId","SubmittedAtUtc")
            values (@po,@tenant,@number,@supplier,'SUP-E','Batch E Supplier',@legal,@outlet,'PHP',@status,'2026-08-18',@total,2,@user,@now,@user,@now);
            insert into procurement.purchase_order_lines
              ("Id","TenantId","PurchaseOrderId","LineNumber","SupplierOfferId","CatalogItemId","CatalogItemCodeSnapshot","CatalogItemNameSnapshot","OrderQuantity","ReceivedQuantity","PurchaseUomId","PurchaseUomCodeSnapshot","BaseUomId","BaseUomCodeSnapshot","ConversionNumerator","ConversionDenominator","UnitPrice","LineNetAmount")
            values (@line,@tenant,@po,1,@offer,@item,@itemCode,@itemName,@quantity,0,@purchaseUom,'CASE',@baseUom,'KG',@numerator,@denominator,100,@total);
            """, connection);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("tenant", TenantA);
        command.Parameters.AddWithValue("itemCode", $"E-{suffix}");
        command.Parameters.AddWithValue("itemName", $"Batch E Item {suffix}");
        command.Parameters.AddWithValue("baseUom", UomIds.Kilogram);
        command.Parameters.AddWithValue("profile", Guid.NewGuid());
        command.Parameters.AddWithValue("po", purchaseOrderId);
        command.Parameters.AddWithValue("number", $"PO-E-{purchaseOrderId:N}"[..24].ToUpperInvariant());
        command.Parameters.AddWithValue("supplier", SupplierId);
        command.Parameters.AddWithValue("legal", LegalEntityA);
        command.Parameters.AddWithValue("outlet", OutletA);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("total", decimal.Round(orderQuantity * 100m, 4));
        command.Parameters.AddWithValue("user", UserAlice);
        command.Parameters.AddWithValue("now", FixedUtcNow);
        command.Parameters.AddWithValue("line", lineId);
        command.Parameters.AddWithValue("offer", Guid.NewGuid());
        command.Parameters.AddWithValue("quantity", orderQuantity);
        command.Parameters.AddWithValue("purchaseUom", UomIds.Case);
        command.Parameters.AddWithValue("numerator", conversionNumerator);
        command.Parameters.AddWithValue("denominator", conversionDenominator);
        await command.ExecuteNonQueryAsync();
        return new SeededPurchaseOrder(purchaseOrderId, lineId, itemId, orderQuantity, conversionNumerator, conversionDenominator);
    }

    public async Task ProcessStockConsequenceAsync(Guid tenantId)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>();
        context.SetCandidateTenant(tenantId);
        await scope.ServiceProvider.GetRequiredService<StockConsequenceProcessor>().ProcessTenantAsync(tenantId, CancellationToken.None);
    }

    public TestStockConsequenceAttemptHook AttemptHook => Services.GetRequiredService<TestStockConsequenceAttemptHook>();

    public Task<long> CountMovementsAsync(Guid receiptId) => ScalarInt64Async(TenantA, "select count(*) from inventory.inventory_movements where \"SourceDocumentId\"=@id;", receiptId);
    public Task<long> CountSourceEffectsAsync(Guid eventId) => ScalarInt64Async(TenantA, "select count(*) from inventory.inventory_source_effects where \"SourceEventId\"=@id;", eventId);
    public Task<long> CountReceiptOutboxAsync(Guid receiptId) => ScalarInt64Async(TenantA, "select count(*) from procurement.outbox_messages where \"CausationId\"=@text;", $"GR:{receiptId}:POST");

    public async Task<Guid> GetReceiptEventIdAsync(Guid receiptId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("select \"Id\" from procurement.outbox_messages where \"CausationId\"=@causation;", connection);
        command.Parameters.AddWithValue("causation", $"GR:{receiptId}:POST");
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Receipt event not found."));
    }

    public async Task<OutboxState> GetOutboxStateAsync(Guid eventId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("select \"ProcessedAtUtc\",\"LastError\",\"AttemptCount\" from procurement.outbox_messages where \"Id\"=@id;", connection);
        command.Parameters.AddWithValue("id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Outbox row not found.");
        return new OutboxState(reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2));
    }

    public async Task RedeliverReceiptEventAsync(Guid receiptId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("update procurement.outbox_messages set \"ProcessedAtUtc\"=null,\"LastError\"=null where \"CausationId\"=@causation;", connection);
        command.Parameters.AddWithValue("causation", $"GR:{receiptId}:POST");
        await command.ExecuteNonQueryAsync();
    }

    public async Task<decimal> GetPositionQuantityAsync(Guid itemId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("select \"QuantityOnHand\" from inventory.stock_positions where \"CatalogItemId\"=@item and \"StockLocationId\"=@location;", connection);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("location", ActiveLocation);
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }

    public async Task<decimal> GetReceivedQuantityAsync(Guid purchaseOrderLineId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("select \"ReceivedQuantity\" from procurement.purchase_order_lines where \"Id\"=@id;", connection);
        command.Parameters.AddWithValue("id", purchaseOrderLineId);
        return (decimal)(await command.ExecuteScalarAsync() ?? 0m);
    }

    public async Task<InventoryMovementEvidence> GetMovementAsync(Guid receiptId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("select \"Id\",\"SourceEventId\",\"SourceLineId\",\"PurchaseOrderId\",\"PurchaseOrderLineId\",\"CatalogItemId\",\"StockLocationId\",\"BaseUomId\",\"QuantityBaseUom\",\"MovementType\",\"CorrelationId\" from inventory.inventory_movements where \"SourceDocumentId\"=@id;", connection);
        command.Parameters.AddWithValue("id", receiptId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Movement not found.");
        return new InventoryMovementEvidence(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetGuid(6), reader.GetGuid(7), reader.GetDecimal(8), reader.GetString(9), reader.GetString(10));
    }

    public async Task CorruptPositionAsync(Guid itemId, decimal quantity)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("update inventory.stock_positions set \"QuantityOnHand\"=@quantity where \"CatalogItemId\"=@item and \"StockLocationId\"=@location;", connection);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("location", ActiveLocation);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ChangeCatalogItemNameAsync(Guid itemId, string name)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("update catalog.items set \"Name\"=@name,\"Version\"=\"Version\"+1 where \"Id\"=@id;", connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("id", itemId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task CorruptReceiptConversionAsync(Guid receiptId, long numerator)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("update procurement.goods_receipt_lines set \"ConversionNumerator\"=@numerator where \"GoodsReceiptId\"=@id;", connection);
        command.Parameters.AddWithValue("numerator", numerator);
        command.Parameters.AddWithValue("id", receiptId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task AttemptMovementUpdateAsync(Guid movementId)
    {
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("update inventory.inventory_movements set \"QuantityBaseUom\"=\"QuantityBaseUom\"+1 where \"Id\"=@id;", connection);
        command.Parameters.AddWithValue("id", movementId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Guid> CreateForgedTenantEventAsync()
    {
        var eventId = Guid.NewGuid();
        var payload = new GoodsReceiptPostedV1(eventId, TenantB, Guid.NewGuid(), "GR-FORGED", Guid.NewGuid(), "PO-FORGED", ForeignSupplierId, "SUP-F", "Foreign Supplier", LegalEntityB, OutletB, ForeignLocation, "FOREIGN", "PHP", new DateOnly(2026, 8, 18), UserAlice, "forged-tenant", FixedUtcNow, new[] { new GoodsReceiptPostedLineV1(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), "FORGED", "Forged Item", 1, UomIds.Case, "CASE", UomIds.Kilogram, "KG", 5, 1, 5, 100, 100) });
        await using var connection = await OpenTenantConnectionAsync(TenantA);
        await using var command = new NpgsqlCommand("insert into procurement.outbox_messages (\"Id\",\"TenantId\",\"Type\",\"SchemaVersion\",\"OccurredAtUtc\",\"CorrelationId\",\"CausationId\",\"Payload\",\"AttemptCount\") values (@id,@tenant,'Procurement.GoodsReceiptPosted',1,@now,'forged-tenant',@causation,@payload::jsonb,0);", connection);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("tenant", TenantA);
        command.Parameters.AddWithValue("now", FixedUtcNow);
        command.Parameters.AddWithValue("causation", $"FORGED:{eventId}");
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync();
        return eventId;
    }

    public async Task<IReadOnlyList<RlsState>> GetBatchERlsStatesAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select n.nspname,c.relname,c.relrowsecurity,c.relforcerowsecurity,
              (select count(*) from pg_policies p where p.schemaname=n.nspname and p.tablename=c.relname and p.policyname='tenant_isolation')
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            where (n.nspname='procurement' and c.relname in ('goods_receipts','goods_receipt_lines','goods_receipt_posting_commands'))
               or (n.nspname='inventory' and c.relname in ('inventory_source_effects','inventory_movements','stock_positions'))
            order by n.nspname,c.relname;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var states = new List<RlsState>();
        while (await reader.ReadAsync()) states.Add(new RlsState(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetInt64(4)));
        return states;
    }

    public async Task AttemptCrossTenantStockPositionInsertAsRuntimeRoleAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"SET ROLE {RlsTestRole}; select set_config('app.tenant_id','{TenantA}',false);");
        await using var command = new NpgsqlCommand("insert into inventory.stock_positions (\"Id\",\"TenantId\",\"CatalogItemId\",\"CatalogItemCodeSnapshot\",\"CatalogItemNameSnapshot\",\"StockLocationId\",\"StockLocationCodeSnapshot\",\"OutletId\",\"BaseUomId\",\"BaseUomCodeSnapshot\",\"QuantityOnHand\",\"Version\") values (@id,@tenant,@item,'BLOCKED','Blocked',@location,'FOREIGN',@outlet,@uom,'KG',1,1);", connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", TenantB);
        command.Parameters.AddWithValue("item", Guid.NewGuid());
        command.Parameters.AddWithValue("location", ForeignLocation);
        command.Parameters.AddWithValue("outlet", OutletB);
        command.Parameters.AddWithValue("uom", UomIds.Kilogram);
        await command.ExecuteNonQueryAsync();
    }

    private async Task EnsureRlsTestRoleAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"""
            DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{RlsTestRole}') THEN CREATE ROLE {RlsTestRole} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS; END IF; END $$;
            GRANT USAGE ON SCHEMA inventory,procurement TO {RlsTestRole};
            GRANT SELECT,INSERT ON inventory.stock_positions TO {RlsTestRole};
            """);
    }

    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            insert into foundation.tenants ("Id","Code","Name") values
              ('eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','TENANT-E','Tenant E'),
              ('ffffffff-ffff-ffff-ffff-ffffffffffff','TENANT-F','Tenant F') on conflict do nothing;
            insert into foundation.users ("Id","ExternalSubject","DisplayName") values
              ('e1111111-1111-1111-1111-111111111111','cognito|batch-e-alice','Batch E Alice'),
              ('e2222222-2222-2222-2222-222222222222','cognito|batch-e-bob','Batch E Bob') on conflict do nothing;
            """);

        await SetTenantAsync(connection, TenantA);
        await ExecuteAsync(connection, """
            insert into foundation.legal_entities ("Id","TenantId","Code","Name") values
              ('e4111111-1111-1111-1111-111111111111','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','LE-E','Legal Entity E') on conflict do nothing;
            insert into foundation.outlets ("Id","TenantId","LegalEntityId","Code","Name","TimeZoneId","BusinessDayStartMinutes") values
              ('e5111111-1111-1111-1111-111111111111','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e4111111-1111-1111-1111-111111111111','MNL','Manila Outlet','Asia/Manila',240),
              ('e5222222-2222-2222-2222-222222222222','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e4111111-1111-1111-1111-111111111111','MNL2','Other Outlet','Asia/Manila',240) on conflict do nothing;
            insert into foundation.tenant_memberships ("Id","TenantId","UserId","Status") values
              ('e3111111-1111-1111-1111-111111111111','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e1111111-1111-1111-1111-111111111111','ACTIVE'),
              ('e3222222-2222-2222-2222-222222222222','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e2222222-2222-2222-2222-222222222222','ACTIVE') on conflict do nothing;
            insert into foundation.permission_grants ("Id","TenantId","MembershipId","PermissionCode") values
              ('e8111111-0000-0000-0000-000000000001','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e3111111-1111-1111-1111-111111111111','procurement.goods_receipt.read'),
              ('e8111111-0000-0000-0000-000000000002','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e3111111-1111-1111-1111-111111111111','procurement.goods_receipt.write'),
              ('e8111111-0000-0000-0000-000000000003','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e3111111-1111-1111-1111-111111111111','procurement.goods_receipt.post'),
              ('e8111111-0000-0000-0000-000000000004','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e3111111-1111-1111-1111-111111111111','inventory.stock.read'),
              ('e8111111-0000-0000-0000-000000000005','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e3111111-1111-1111-1111-111111111111','inventory.movement.read'),
              ('e8111111-0000-0000-0000-000000000006','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e3111111-1111-1111-1111-111111111111','inventory.stock.rebuild') on conflict do nothing;
            insert into foundation.outlet_scope_grants ("Id","TenantId","MembershipId","OutletId") values
              ('e7111111-1111-1111-1111-111111111111','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e3111111-1111-1111-1111-111111111111','e5111111-1111-1111-1111-111111111111'),
              ('e7222222-2222-2222-2222-222222222222','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e3222222-2222-2222-2222-222222222222','e5111111-1111-1111-1111-111111111111') on conflict do nothing;
            insert into inventory.stock_locations ("Id","TenantId","OutletId","Code","Name","LocationType","IsActive") values
              ('ec111111-1111-1111-1111-111111111111','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e5111111-1111-1111-1111-111111111111','MAIN','Main Store','STORE',true),
              ('ec222222-2222-2222-2222-222222222222','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e5111111-1111-1111-1111-111111111111','INACTIVE','Inactive Store','STORE',false),
              ('ec333333-3333-3333-3333-333333333333','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','e5222222-2222-2222-2222-222222222222','OTHER','Other Outlet Store','STORE',true) on conflict do nothing;
            insert into procurement.suppliers ("Id","TenantId","Code","Name","Status","Version") values
              ('ea111111-1111-1111-1111-111111111111','eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee','SUP-E','Batch E Supplier','ACTIVE',1) on conflict do nothing;
            """);

        await SetTenantAsync(connection, TenantB);
        await ExecuteAsync(connection, """
            insert into foundation.legal_entities ("Id","TenantId","Code","Name") values
              ('f4111111-1111-1111-1111-111111111111','ffffffff-ffff-ffff-ffff-ffffffffffff','LE-F','Legal Entity F') on conflict do nothing;
            insert into foundation.outlets ("Id","TenantId","LegalEntityId","Code","Name","TimeZoneId","BusinessDayStartMinutes") values
              ('f5111111-1111-1111-1111-111111111111','ffffffff-ffff-ffff-ffff-ffffffffffff','f4111111-1111-1111-1111-111111111111','CEB','Foreign Outlet','Asia/Manila',240) on conflict do nothing;
            insert into inventory.stock_locations ("Id","TenantId","OutletId","Code","Name","LocationType","IsActive") values
              ('fc111111-1111-1111-1111-111111111111','ffffffff-ffff-ffff-ffff-ffffffffffff','f5111111-1111-1111-1111-111111111111','FOREIGN','Foreign Store','STORE',true) on conflict do nothing;
            insert into procurement.suppliers ("Id","TenantId","Code","Name","Status","Version") values
              ('fa111111-1111-1111-1111-111111111111','ffffffff-ffff-ffff-ffff-ffffffffffff','SUP-F','Foreign Supplier','ACTIVE',1) on conflict do nothing;
            insert into procurement.purchase_orders ("Id","TenantId","Number","SupplierId","SupplierCodeSnapshot","SupplierNameSnapshot","LegalEntityId","OutletId","Currency","Status","BusinessDate","TotalNetAmount","Version","CreatedByUserId","CreatedAtUtc") values
              ('fb111111-1111-1111-1111-111111111111','ffffffff-ffff-ffff-ffff-ffffffffffff','PO-FOREIGN','fa111111-1111-1111-1111-111111111111','SUP-F','Foreign Supplier','f4111111-1111-1111-1111-111111111111','f5111111-1111-1111-1111-111111111111','PHP','APPROVED','2026-08-18',100,1,'e1111111-1111-1111-1111-111111111111',@now) on conflict do nothing;
            insert into procurement.goods_receipts ("Id","TenantId","Number","PurchaseOrderId","PurchaseOrderNumberSnapshot","SupplierId","SupplierCodeSnapshot","SupplierNameSnapshot","LegalEntityId","OutletId","StockLocationId","StockLocationCodeSnapshot","Currency","BusinessDate","Status","TotalNetAmount","Version","CreatedByUserId","CreatedAtUtc") values
              ('fd111111-1111-1111-1111-111111111111','ffffffff-ffff-ffff-ffff-ffffffffffff','GR-FOREIGN','fb111111-1111-1111-1111-111111111111','PO-FOREIGN','fa111111-1111-1111-1111-111111111111','SUP-F','Foreign Supplier','f4111111-1111-1111-1111-111111111111','f5111111-1111-1111-1111-111111111111','fc111111-1111-1111-1111-111111111111','FOREIGN','PHP','2026-08-18','DRAFT',0,1,'e1111111-1111-1111-1111-111111111111',@now) on conflict do nothing;
            """, new Dictionary<string, object> { ["now"] = FixedUtcNow });
    }

    private async Task<NpgsqlConnection> OpenTenantConnectionAsync(Guid tenantId)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, tenantId);
        return connection;
    }

    private async Task<long> ScalarInt64Async(Guid tenantId, string sql, Guid id)
    {
        await using var connection = await OpenTenantConnectionAsync(tenantId);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> ScalarInt64Async(Guid tenantId, string sql, string text)
    {
        await using var connection = await OpenTenantConnectionAsync(tenantId);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("text", text);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
        => ExecuteAsync(connection, $"select set_config('app.tenant_id','{tenantId}',false);");

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, IReadOnlyDictionary<string, object>? parameters = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (parameters is not null) foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class TestStockConsequenceAttemptHook : IStockConsequenceAttemptHook
{
    private int failBefore;
    private int failAfter;
    public void FailBeforeOnce() => Interlocked.Exchange(ref failBefore, 1);
    public void FailAfterOnce() => Interlocked.Exchange(ref failAfter, 1);
    public Task BeforeApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref failBefore, 0) == 1) throw new TimeoutException("Injected transient failure before Inventory effect.");
        return Task.CompletedTask;
    }
    public Task AfterApplyAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref failAfter, 0) == 1) throw new TimeoutException("Injected crash after Inventory commit and before source outbox acknowledgement.");
        return Task.CompletedTask;
    }
}

public sealed record SeededPurchaseOrder(Guid PurchaseOrderId, Guid PurchaseOrderLineId, Guid CatalogItemId, decimal OrderQuantity, long ConversionNumerator, long ConversionDenominator);
public sealed record OutboxState(DateTimeOffset? ProcessedAtUtc, string? LastError, int AttemptCount);
public sealed record InventoryMovementEvidence(Guid Id, Guid SourceEventId, Guid SourceLineId, Guid PurchaseOrderId, Guid PurchaseOrderLineId, Guid CatalogItemId, Guid StockLocationId, Guid BaseUomId, decimal QuantityBaseUom, string MovementType, string CorrelationId);
public sealed record RlsState(string Schema, string Table, bool Enabled, bool Forced, long PolicyCount);
