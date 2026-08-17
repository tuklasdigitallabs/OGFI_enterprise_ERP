namespace Ogfi.BuildingBlocks.Messaging;

public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public required string Type { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset OccurredAtUtc { get; init; }
    public required string CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
