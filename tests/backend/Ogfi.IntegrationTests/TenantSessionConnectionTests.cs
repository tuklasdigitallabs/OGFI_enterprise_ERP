using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class TenantSessionConnectionTests
{
    [Fact]
    public async Task Pooled_connection_clears_previous_tenant_when_next_context_has_no_tenant()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        Assert.False(string.IsNullOrWhiteSpace(baseConnectionString));

        var poolName = $"tenant-session-{Guid.NewGuid():N}";
        var connectionString = $"{baseConnectionString};Maximum Pool Size=1;Application Name={poolName}";
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var tenantContext = new TenantExecutionContextAccessor();
        tenantContext.SetCandidateTenant(tenantId);

        await using (var db = CreateContext(connectionString, tenantContext))
        {
            await db.Database.OpenConnectionAsync();
            Assert.Equal(tenantId.ToString(), await ReadTenantSettingAsync(db));
        }

        var tenantlessContext = new TenantExecutionContextAccessor();
        await using (var db = CreateContext(connectionString, tenantlessContext))
        {
            await db.Database.OpenConnectionAsync();
            Assert.Equal(string.Empty, await ReadTenantSettingAsync(db));
        }
    }

    private static FoundationDbContext CreateContext(
        string connectionString,
        ITenantExecutionContextAccessor executionContext)
    {
        var interceptor = new TenantSessionConnectionInterceptor(executionContext);
        var options = new DbContextOptionsBuilder<FoundationDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        return new FoundationDbContext(options);
    }

    private static async Task<string> ReadTenantSettingAsync(FoundationDbContext db)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "select current_setting('app.tenant_id', true);";
        return (string?)await command.ExecuteScalarAsync() ?? string.Empty;
    }
}
