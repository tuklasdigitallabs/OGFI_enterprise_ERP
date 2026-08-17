using Ogfi.BuildingBlocks.Multitenancy;

namespace Ogfi.Api.Infrastructure;

public static class EndpointSupport
{
    public static bool TryResolveActor(ITenantExecutionContextAccessor context, out Guid tenantId, out Guid userId)
    {
        tenantId = default;
        userId = default;
        if (!context.IsResolved || context.TenantId is not Guid tenant || context.UserId is not Guid user)
        {
            return false;
        }
        tenantId = tenant;
        userId = user;
        return true;
    }

    public static IResult Problem(HttpContext context, int status, string code, string detail)
        => Results.Problem(
            statusCode: status,
            title: "OGFI request rejected",
            detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier
            });

    public static string ToEtag(long version)
        => $"\"{Convert.ToBase64String(BitConverter.GetBytes(version))}\"";

    public static bool TryReadIfMatch(HttpContext context, out long version)
    {
        version = default;
        if (!context.Request.Headers.TryGetValue("If-Match", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        try
        {
            var token = raw.ToString().Trim().Trim('"');
            var bytes = Convert.FromBase64String(token);
            if (bytes.Length != sizeof(long))
            {
                return false;
            }
            version = BitConverter.ToInt64(bytes, 0);
            return version > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string CorrelationId(HttpContext context)
        => context.Items.TryGetValue("CorrelationId", out var value) && value is string correlationId
            ? correlationId
            : context.TraceIdentifier;
}
