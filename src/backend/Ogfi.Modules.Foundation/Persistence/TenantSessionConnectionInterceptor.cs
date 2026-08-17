using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ogfi.BuildingBlocks.Multitenancy;

namespace Ogfi.Modules.Foundation.Persistence;

public sealed class TenantSessionConnectionInterceptor(ITenantExecutionContextAccessor executionContext) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        // Npgsql pools physical connections. Always overwrite the session-scoped tenant GUC
        // on every logical open so a previously used tenant context can never leak into a
        // later tenant-less or different-tenant request.
        var tenantValue = executionContext.TenantId?.ToString() ?? string.Empty;

        await using var command = connection.CreateCommand();
        command.CommandText = "select set_config('app.tenant_id', @tenant_id, false);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.Value = tenantValue;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
