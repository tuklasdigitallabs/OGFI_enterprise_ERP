using System.Security.Claims;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Security;

namespace Ogfi.Api.Security;

public sealed class TenantExecutionContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        MembershipResolver membershipResolver)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            await next(httpContext);
            return;
        }

        var tenantClaim = httpContext.User.FindFirstValue(OgfiClaimTypes.TenantId);
        var externalSubject = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.User.FindFirstValue("sub");

        if (!Guid.TryParse(tenantClaim, out var tenantId) || string.IsNullOrWhiteSpace(externalSubject))
        {
            await WriteProblemAsync(httpContext, StatusCodes.Status401Unauthorized, "AUTH.TENANT_DENIED", "Authenticated session is missing valid OGFI tenant identity context.");
            return;
        }

        executionContext.SetCandidateTenant(tenantId);
        var membership = await membershipResolver.ResolveAsync(tenantId, externalSubject, httpContext.RequestAborted);
        if (membership is null)
        {
            await WriteProblemAsync(httpContext, StatusCodes.Status403Forbidden, "AUTH.TENANT_DENIED", "The authenticated user has no active membership in the requested tenant context.");
            return;
        }

        executionContext.Resolve(membership.UserId, membership.MembershipId);
        await next(httpContext);
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        return Results.Problem(
            statusCode: status,
            title: "OGFI authorization failure",
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier
            }).ExecuteAsync(context);
    }
}
