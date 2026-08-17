using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Security;

namespace Ogfi.Api.Endpoints;

public static class FoundationContextEndpoints
{
    public static IEndpointRouteBuilder MapFoundationContextEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/context/outlets/{outletId:guid}", ResolveOutletContextAsync)
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> ResolveOutletContextAsync(
        Guid outletId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        BusinessTimeResolver businessTime,
        CancellationToken cancellationToken)
    {
        if (!executionContext.IsResolved || executionContext.TenantId is not Guid tenantId || executionContext.UserId is not Guid userId)
        {
            return Problem(httpContext, StatusCodes.Status401Unauthorized, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        }

        if (!await authorization.HasPermissionAsync(FoundationPermissionCodes.ContextRead, cancellationToken))
        {
            return Problem(httpContext, StatusCodes.Status403Forbidden, "AUTH.PERMISSION_DENIED", "The action is not permitted for this tenant membership.");
        }

        var businessContext = await businessTime.ResolveAsync(outletId, cancellationToken);
        if (businessContext is null)
        {
            return Results.NotFound();
        }

        if (!await authorization.HasOutletScopeAsync(outletId, cancellationToken))
        {
            return Problem(httpContext, StatusCodes.Status403Forbidden, "AUTH.SCOPE_DENIED", "The requested Outlet is outside the user's organization scope.");
        }

        httpContext.Response.Headers["X-OGFI-Business-Date"] = businessContext.BusinessDate.ToString();

        return Results.Ok(new
        {
            tenantId,
            userId,
            outletId = businessContext.OutletId,
            outletCode = businessContext.OutletCode,
            timeZoneId = businessContext.TimeZoneId,
            businessDate = businessContext.BusinessDate.ToString()
        });
    }

    private static IResult Problem(HttpContext context, int status, string code, string detail)
    {
        return Results.Problem(
            statusCode: status,
            title: "OGFI authorization failure",
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier
            });
    }
}
