using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Ogfi.Modules.Foundation.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class SecuritySpineFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid UserAlice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid UserBob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid MembershipAliceA = Guid.Parse("31111111-1111-1111-1111-111111111111");
    public static readonly Guid MembershipBobA = Guid.Parse("32222222-2222-2222-2222-222222222222");
    public static readonly Guid LegalEntityA = Guid.Parse("41111111-1111-1111-1111-111111111111");
    public static readonly Guid LegalEntityB = Guid.Parse("42222222-2222-2222-2222-222222222222");
    public static readonly Guid OutletA1 = Guid.Parse("51111111-1111-1111-1111-111111111111");
    public static readonly Guid OutletA2 = Guid.Parse("51222222-2222-2222-2222-222222222222");
    public static readonly Guid OutletB1 = Guid.Parse("52111111-1111-1111-1111-111111111111");

    public const string AliceSubject = "cognito|alice";
    public const string BobSubject = "cognito|bob";
    private const string RlsTestRole = "ogfi_rls_test";

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
            var dbContext = scope.ServiceProvider.GetRequiredService<FoundationDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        await EnsureRlsTestRoleAsync();
        await SeedAsync();
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public HttpClient CreateAuthenticatedClient(string subject, Guid tenantId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.SubjectHeader, subject);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.TenantHeader, tenantId.ToString());
        return client;
    }

    public async Task<long> CountVisibleOutletsAsRuntimeRoleAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetRuntimeRoleAndTenantAsync(connection, tenantId);
        await using var command = new NpgsqlCommand("select count(*) from foundation.outlets;", connection);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task AttemptCrossTenantLegalEntityInsertAsRuntimeRoleAsync(Guid activeTenantId, Guid rowTenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetRuntimeRoleAndTenantAsync(connection, activeTenantId);
        await using var command = new NpgsqlCommand("""
            insert into foundation.legal_entities ("Id", "TenantId", "Code", "Name")
            values (@id, @tenant, 'RLS-BLOCK', 'Must Be Blocked');
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", rowTenantId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<(bool Enabled, bool Forced, long Policies)> GetOutletRlsStateAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select c.relrowsecurity, c.relforcerowsecurity,
                   (select count(*) from pg_policies p where p.schemaname = 'foundation' and p.tablename = 'outlets')
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'foundation' and c.relname = 'outlets';
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetInt64(2));
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
            GRANT USAGE ON SCHEMA foundation TO {RlsTestRole};
            GRANT SELECT ON foundation.outlets TO {RlsTestRole};
            GRANT INSERT ON foundation.legal_entities TO {RlsTestRole};
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
              ('11111111-1111-1111-1111-111111111111', 'cognito|alice', 'Alice'),
              ('22222222-2222-2222-2222-222222222222', 'cognito|bob', 'Bob')
            ON CONFLICT DO NOTHING;
            """);

        await SetTenantAsync(connection, TenantA);
        await ExecuteAsync(connection, """
            INSERT INTO foundation.legal_entities ("Id", "TenantId", "Code", "Name") VALUES
              ('41111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'LE-A', 'Legal Entity A')
            ON CONFLICT DO NOTHING;

            INSERT INTO foundation.outlets ("Id", "TenantId", "LegalEntityId", "Code", "Name", "TimeZoneId", "BusinessDayStartMinutes") VALUES
              ('51111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '41111111-1111-1111-1111-111111111111', 'BGC', 'BGC Outlet', 'Asia/Manila', 240),
              ('51222222-2222-2222-2222-222222222222', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '41111111-1111-1111-1111-111111111111', 'MKT', 'Makati Outlet', 'Asia/Manila', 240)
            ON CONFLICT DO NOTHING;

            INSERT INTO foundation.tenant_memberships ("Id", "TenantId", "UserId", "Status") VALUES
              ('31111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '11111111-1111-1111-1111-111111111111', 'ACTIVE'),
              ('32222222-2222-2222-2222-222222222222', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '22222222-2222-2222-2222-222222222222', 'ACTIVE')
            ON CONFLICT DO NOTHING;

            INSERT INTO foundation.permission_grants ("Id", "TenantId", "MembershipId", "PermissionCode") VALUES
              ('61111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'foundation.context.read')
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
            """);
    }

    private static Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        return ExecuteAsync(connection, $"select set_config('app.tenant_id', '{tenantId}', false);");
    }

    private static Task SetRuntimeRoleAndTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        return ExecuteAsync(connection, $"SET ROLE {RlsTestRole}; select set_config('app.tenant_id', '{tenantId}', false);");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
