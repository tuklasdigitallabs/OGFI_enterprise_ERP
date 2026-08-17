using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Ogfi.Modules.Catalog;
using Ogfi.Modules.Catalog.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchCFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid UserAlice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid MembershipAliceA = Guid.Parse("31111111-1111-1111-1111-111111111111");
    public static readonly Guid LegalEntityA = Guid.Parse("41111111-1111-1111-1111-111111111111");
    public static readonly Guid LegalEntityB = Guid.Parse("42222222-2222-2222-2222-222222222222");
    public static readonly Guid OutletA1 = Guid.Parse("51111111-1111-1111-1111-111111111111");
    public static readonly Guid OutletB1 = Guid.Parse("52111111-1111-1111-1111-111111111111");
    public static readonly Guid TenantBItem = Guid.Parse("b1000000-0000-0000-0000-000000000001");

    public const string AliceSubject = "cognito|alice";
    private const string RlsTestRole = "ogfi_batch_c_rls_test";
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 8, 17, 19, 0, 0, TimeSpan.Zero);

    private readonly string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedUtcNow));
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
        }
        await EnsureRlsTestRoleAsync();
        await SeedAsync();
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.SubjectHeader, AliceSubject);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.TenantHeader, TenantA.ToString());
        return client;
    }

    public async Task<long> CountApprovalRequestsAsync(Guid purchaseOrderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select count(*)
            from procurement.outbox_messages
            where "Type" = 'Procurement.PurchaseOrderApprovalRequested'
              and "CausationId" = @causation;
            """, connection);
        command.Parameters.AddWithValue("causation", $"PO:{purchaseOrderId}:APPROVAL:1");
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<string> GetApprovalRequestPayloadAsync(Guid purchaseOrderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select "Payload"
            from procurement.outbox_messages
            where "Type" = 'Procurement.PurchaseOrderApprovalRequested'
              and "CausationId" = @causation;
            """, connection);
        command.Parameters.AddWithValue("causation", $"PO:{purchaseOrderId}:APPROVAL:1");
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Approval request outbox payload was not found."));
    }

    public async Task<IReadOnlyList<BatchCRlsState>> GetBatchCRlsStatesAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   (select count(*) from pg_policies p where p.schemaname = n.nspname and p.tablename = c.relname and p.policyname = 'tenant_isolation')
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where (n.nspname, c.relname) in (
                ('catalog','items'), ('catalog','item_packaging_conversions'),
                ('inventory','inventory_profiles'), ('inventory','stock_locations'),
                ('procurement','suppliers'), ('procurement','supplier_offers'),
                ('procurement','purchase_orders'), ('procurement','purchase_order_lines'),
                ('procurement','outbox_messages'))
            order by n.nspname, c.relname;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<BatchCRlsState>();
        while (await reader.ReadAsync())
        {
            rows.Add(new BatchCRlsState(
                $"{reader.GetString(0)}.{reader.GetString(1)}",
                reader.GetBoolean(2), reader.GetBoolean(3), reader.GetInt64(4)));
        }
        return rows;
    }

    public async Task<bool> RuntimeRoleCanSeeCatalogItemAsync(Guid activeTenantId, Guid itemId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetRuntimeRoleAndTenantAsync(connection, activeTenantId);
        await using var command = new NpgsqlCommand("select exists(select 1 from catalog.items where \"Id\" = @id);", connection);
        command.Parameters.AddWithValue("id", itemId);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    public async Task AttemptCrossTenantCatalogItemInsertAsRuntimeRoleAsync(Guid activeTenantId, Guid rowTenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetRuntimeRoleAndTenantAsync(connection, activeTenantId);
        await using var command = new NpgsqlCommand("""
            insert into catalog.items ("Id", "TenantId", "Code", "Name", "BaseUomId", "Status", "Version")
            values (@id, @tenant, @code, 'Blocked cross-tenant item', @uom, 'ACTIVE', 1);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", rowTenantId);
        command.Parameters.AddWithValue("code", $"BLOCK-{Guid.NewGuid():N}"[..20]);
        command.Parameters.AddWithValue("uom", UomIds.Kilogram);
        await command.ExecuteNonQueryAsync();
    }

    private async Task EnsureRlsTestRoleAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{RlsTestRole}') THEN
                    CREATE ROLE {RlsTestRole} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                END IF;
            END
            $$;
            GRANT USAGE ON SCHEMA catalog TO {RlsTestRole};
            GRANT SELECT, INSERT ON catalog.items TO {RlsTestRole};
            """);
    }

    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            INSERT INTO foundation.tenants ("Id", "Code", "Name") VALUES
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'TENANT-A', 'Tenant A'),
              ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'TENANT-B', 'Tenant B')
            ON CONFLICT DO NOTHING;

            INSERT INTO foundation.users ("Id", "ExternalSubject", "DisplayName") VALUES
              ('11111111-1111-1111-1111-111111111111', 'cognito|alice', 'Alice')
            ON CONFLICT DO NOTHING;
            """);

        await SetTenantAsync(connection, TenantA);
        await ExecuteAsync(connection, """
            INSERT INTO foundation.legal_entities ("Id", "TenantId", "Code", "Name") VALUES
              ('41111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'LE-A', 'Legal Entity A')
            ON CONFLICT DO NOTHING;
            INSERT INTO foundation.outlets ("Id", "TenantId", "LegalEntityId", "Code", "Name", "TimeZoneId", "BusinessDayStartMinutes") VALUES
              ('51111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '41111111-1111-1111-1111-111111111111', 'BGC', 'BGC Outlet', 'Asia/Manila', 240)
            ON CONFLICT DO NOTHING;
            INSERT INTO foundation.tenant_memberships ("Id", "TenantId", "UserId", "Status") VALUES
              ('31111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '11111111-1111-1111-1111-111111111111', 'ACTIVE')
            ON CONFLICT DO NOTHING;
            INSERT INTO foundation.permission_grants ("Id", "TenantId", "MembershipId", "PermissionCode") VALUES
              ('81111111-0000-0000-0000-000000000001', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'catalog.read'),
              ('81111111-0000-0000-0000-000000000002', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'catalog.write'),
              ('81111111-0000-0000-0000-000000000003', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'inventory.setup.read'),
              ('81111111-0000-0000-0000-000000000004', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'inventory.setup.write'),
              ('81111111-0000-0000-0000-000000000005', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.supplier.read'),
              ('81111111-0000-0000-0000-000000000006', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.supplier.write'),
              ('81111111-0000-0000-0000-000000000007', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.purchase_order.read'),
              ('81111111-0000-0000-0000-000000000008', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.purchase_order.write'),
              ('81111111-0000-0000-0000-000000000009', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.purchase_order.submit')
            ON CONFLICT DO NOTHING;
            INSERT INTO foundation.outlet_scope_grants ("Id", "TenantId", "MembershipId", "OutletId") VALUES
              ('71111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', '51111111-1111-1111-1111-111111111111')
            ON CONFLICT DO NOTHING;
            """);

        await SetTenantAsync(connection, TenantB);
        await ExecuteAsync(connection, """
            INSERT INTO foundation.legal_entities ("Id", "TenantId", "Code", "Name") VALUES
              ('42222222-2222-2222-2222-222222222222', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'LE-B', 'Legal Entity B')
            ON CONFLICT DO NOTHING;
            INSERT INTO foundation.outlets ("Id", "TenantId", "LegalEntityId", "Code", "Name", "TimeZoneId", "BusinessDayStartMinutes") VALUES
              ('52111111-1111-1111-1111-111111111111', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '42222222-2222-2222-2222-222222222222', 'CEB', 'Cebu Outlet', 'Asia/Manila', 240)
            ON CONFLICT DO NOTHING;
            INSERT INTO catalog.items ("Id", "TenantId", "Code", "Name", "BaseUomId", "Status", "Version") VALUES
              ('b1000000-0000-0000-0000-000000000001', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'B-PRIVATE', 'Tenant B Private Item', '10000000-0000-0000-0000-000000000002', 'ACTIVE', 1)
            ON CONFLICT DO NOTHING;
            """);
    }

    private static Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
        => ExecuteAsync(connection, $"select set_config('app.tenant_id', '{tenantId}', false);");

    private static Task SetRuntimeRoleAndTenantAsync(NpgsqlConnection connection, Guid tenantId)
        => ExecuteAsync(connection, $"SET ROLE {RlsTestRole}; select set_config('app.tenant_id', '{tenantId}', false);");

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record BatchCRlsState(string Table, bool Enabled, bool Forced, long PolicyCount);
