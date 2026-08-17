namespace Ogfi.BuildingBlocks.Execution;

public sealed record CorrelationContext(string CorrelationId)
{
    public static CorrelationContext Create(string? value = null) =>
        new(string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
}
