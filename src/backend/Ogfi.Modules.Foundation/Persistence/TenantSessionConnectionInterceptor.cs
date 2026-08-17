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
        if (executionContext.TenantId is not Guid tenantId)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "select set_config('app.tenant_id', @tenant_id, false);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        parameter.Value = tenantId.ToString();
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
