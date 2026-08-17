using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Catalog.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;
using Ogfi.Modules.Workflow.Persistence;
using Ogfi.Workers;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchDFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid UserAlice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid UserBob = Guid.Parse("12222222-2222-2222-2222-222222222222");
    public static readonly Guid MembershipAliceA = Guid.Parse("31111111-1111-1111-1111-111111111111");
    public static readonly Guid MembershipBobA = Guid.Parse("32222222-2222-2222-2222-222222222222");
    public static readonly Guid LegalEntityA = Guid.Parse("41111111-1111-1111-1111-111111111111");
    public static readonly Guid LegalEntityB = Guid.Parse("42222222-2222-2222-2222-222222222222");
    public static readonly Guid OutletA1 = Guid.Parse("51111111-1111-1111-1111-111111111111");
    public static readonly Guid OutletB1 = Guid.Parse("52111111-1111-1111-1111-111111111111");

    public const string AliceSubject = "cognito|alice";
    public const string BobSubject = "cognito|bob";
    private const string RlsTestRole = "ogfi_batch_d_rls_test";
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 8, 18, 1, 0, 0, TimeSpan.Zero);

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
            services.AddScoped<PurchaseOrderApprovalOutcomeService>();
            services.AddScoped<ApprovalSpineProcessor>();
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

    public async Task ProcessApprovalSpineAsync(Guid tenantId)
    {
        await using var scope = Services.CreateAsyncScope();
        var executionContext = scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>();
        executionContext.SetCandidateTenant(tenantId);
        await scope.ServiceProvider.GetRequiredService<ApprovalSpineProcessor>()
            .ProcessTenantAsync(tenantId, CancellationToken.None);
    }

    public async Task RedeliverApprovalStartAsync(Guid purchaseOrderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, TenantA);
        await using var command = new NpgsqlCommand("""
            update procurement.outbox_messages
               set "ProcessedAtUtc" = null, "LastError" = null
             where "Type" = 'Procurement.PurchaseOrderApprovalRequested'
               and "CausationId" = @causation;
            """, connection);
        command.Parameters.AddWithValue("causation", $"PO:{purchaseOrderId}:APPROVAL:1");
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> CountWorkflowInstancesAsync(Guid purchaseOrderId)
    {
        return await ScalarInt64Async(TenantA, """
            select count(*) from workflow.workflow_instances
             where "SubjectType" = 'PURCHASE_ORDER' and "SubjectId" = @id;
            """, purchaseOrderId);
    }

    public async Task<long> CountApprovalDecisionsAsync(Guid taskId)
    {
        return await ScalarInt64Async(TenantA, "select count(*) from workflow.approval_decisions where \"TaskId\" = @id;", taskId);
    }

    public async Task<long> CountApprovalOutcomeMessagesAsync(Guid instanceId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, TenantA);
        await using var command = new NpgsqlCommand("""
            select count(*) from workflow.outbox_messages
             where "Type" = 'Workflow.PurchaseOrderApprovalCompleted'
               and "CausationId" like @prefix;
            """, connection);
        command.Parameters.AddWithValue("prefix", $"WF:{instanceId}:TASK:%");
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<string?> GetApprovalOutcomeLastErrorAsync(Guid instanceId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, TenantA);
        await using var command = new NpgsqlCommand("""
            select "LastError" from workflow.outbox_messages
             where "Type" = 'Workflow.PurchaseOrderApprovalCompleted'
               and "CausationId" like @prefix
             order by "OccurredAtUtc" desc limit 1;
            """, connection);
        command.Parameters.AddWithValue("prefix", $"WF:{instanceId}:TASK:%");
        return await command.ExecuteScalarAsync() as string;
    }

    public async Task BumpPurchaseOrderVersionAsync(Guid purchaseOrderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, TenantA);
        await using var command = new NpgsqlCommand(
            "update procurement.purchase_orders set \"Version\" = \"Version\" + 1 where \"Id\" = @id;", connection);
        command.Parameters.AddWithValue("id", purchaseOrderId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<(string Status, long Version)> GetPurchaseOrderStateAsync(Guid purchaseOrderId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, TenantA);
        await using var command = new NpgsqlCommand(
            "select \"Status\", \"Version\" from procurement.purchase_orders where \"Id\" = @id;", connection);
        command.Parameters.AddWithValue("id", purchaseOrderId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Purchase Order not found.");
        return (reader.GetString(0), reader.GetInt64(1));
    }

    public async Task<Guid> CreateForeignWorkflowTaskAsync()
    {
        var definitionId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, TenantB);
        await using var command = new NpgsqlCommand("""
            insert into workflow.workflow_definition_versions
              ("Id","TenantId","Code","Version","Name","CreatedAtUtc")
            values (@definition,@tenant,'RS01.PO.APPROVAL',99,'Tenant B private definition',@now);
            insert into workflow.workflow_instances
              ("Id","TenantId","DefinitionVersionId","SubjectType","SubjectId","ApprovalRound","SubjectVersion",
               "RequesterUserId","LegalEntityId","OutletId","BusinessDate","PurchaseOrderTotal","Currency","Status","CorrelationId","StartedAtUtc","CompletedAtUtc")
            values (@instance,@tenant,@definition,'PURCHASE_ORDER',@subject,1,1,@user,@legal,@outlet,'2026-08-18',100,'PHP','PENDING','tenant-b-private',@now,null);
            insert into workflow.workflow_tasks
              ("Id","TenantId","InstanceId","StepKey","Status","CreatedAtUtc","CompletedAtUtc")
            values (@task,@tenant,@instance,'PO_APPROVAL','PENDING',@now,null);
            insert into workflow.workflow_task_candidates ("Id","TenantId","TaskId","UserId")
            values (@candidate,@tenant,@task,@user);
            """, connection);
        command.Parameters.AddWithValue("definition", definitionId);
        command.Parameters.AddWithValue("tenant", TenantB);
        command.Parameters.AddWithValue("instance", instanceId);
        command.Parameters.AddWithValue("subject", Guid.NewGuid());
        command.Parameters.AddWithValue("user", UserAlice);
        command.Parameters.AddWithValue("legal", LegalEntityB);
        command.Parameters.AddWithValue("outlet", OutletB1);
        command.Parameters.AddWithValue("task", taskId);
        command.Parameters.AddWithValue("candidate", Guid.NewGuid());
        command.Parameters.AddWithValue("now", FixedUtcNow);
        await command.ExecuteNonQueryAsync();
        return taskId;
    }

    public async Task<IReadOnlyList<WorkflowRlsState>> GetWorkflowRlsStatesAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   (select count(*) from pg_policies p where p.schemaname = 'workflow' and p.tablename = c.relname and p.policyname = 'tenant_isolation')
              from pg_class c join pg_namespace n on n.oid = c.relnamespace
             where n.nspname = 'workflow'
               and c.relname in ('workflow_definition_versions','workflow_instances','workflow_tasks','workflow_task_candidates','approval_decisions','outbox_messages')
             order by c.relname;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var states = new List<WorkflowRlsState>();
        while (await reader.ReadAsync())
        {
            states.Add(new WorkflowRlsState(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt64(3)));
        }
        return states;
    }

    public async Task AttemptCrossTenantWorkflowDefinitionInsertAsRuntimeRoleAsync(Guid activeTenantId, Guid rowTenantId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetRuntimeRoleAndTenantAsync(connection, activeTenantId);
        await using var command = new NpgsqlCommand("""
            insert into workflow.workflow_definition_versions ("Id","TenantId","Code","Version","Name","CreatedAtUtc")
            values (@id,@tenant,'BLOCKED.TEST',1,'Blocked cross-tenant definition',@now);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", rowTenantId);
        command.Parameters.AddWithValue("now", FixedUtcNow);
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
            GRANT USAGE ON SCHEMA workflow TO {RlsTestRole};
            GRANT SELECT, INSERT ON workflow.workflow_definition_versions TO {RlsTestRole};
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
              ('12222222-2222-2222-2222-222222222222', 'cognito|bob', 'Bob')
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
              ('31111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '11111111-1111-1111-1111-111111111111', 'ACTIVE'),
              ('32222222-2222-2222-2222-222222222222', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '12222222-2222-2222-2222-222222222222', 'ACTIVE')
            ON CONFLICT DO NOTHING;
            INSERT INTO foundation.permission_grants ("Id", "TenantId", "MembershipId", "PermissionCode") VALUES
              ('82111111-0000-0000-0000-000000000001', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'catalog.read'),
              ('82111111-0000-0000-0000-000000000002', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'catalog.write'),
              ('82111111-0000-0000-0000-000000000003', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.supplier.read'),
              ('82111111-0000-0000-0000-000000000004', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.supplier.write'),
              ('82111111-0000-0000-0000-000000000005', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.purchase_order.read'),
              ('82111111-0000-0000-0000-000000000006', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.purchase_order.write'),
              ('82111111-0000-0000-0000-000000000007', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.purchase_order.submit'),
              ('82111111-0000-0000-0000-000000000008', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'procurement.purchase_order.approve'),
              ('82111111-0000-0000-0000-000000000009', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', 'workflow.definition.manage')
            ON CONFLICT DO NOTHING;
            INSERT INTO foundation.outlet_scope_grants ("Id", "TenantId", "MembershipId", "OutletId") VALUES
              ('72111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '31111111-1111-1111-1111-111111111111', '51111111-1111-1111-1111-111111111111'),
              ('72222222-2222-2222-2222-222222222222', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '32222222-2222-2222-2222-222222222222', '51111111-1111-1111-1111-111111111111')
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

    private async Task<long> ScalarInt64Async(Guid tenantId, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, tenantId);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
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

public sealed record WorkflowRlsState(string Table, bool Enabled, bool Forced, long PolicyCount);
