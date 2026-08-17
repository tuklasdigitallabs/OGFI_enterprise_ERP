namespace Ogfi.Api.Infrastructure;

public readonly record struct EndpointPage(int Offset, int Limit)
{
    public const int DefaultLimit = 50;
    public const int MaximumLimit = 200;

    public static EndpointPage Normalize(HttpContext context, int? offset, int? limit)
    {
        var normalizedOffset = Math.Max(0, offset ?? 0);
        var normalizedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaximumLimit);
        context.Response.Headers["X-OGFI-Page-Offset"] = normalizedOffset.ToString();
        context.Response.Headers["X-OGFI-Page-Limit"] = normalizedLimit.ToString();
        return new EndpointPage(normalizedOffset, normalizedLimit);
    }
}
