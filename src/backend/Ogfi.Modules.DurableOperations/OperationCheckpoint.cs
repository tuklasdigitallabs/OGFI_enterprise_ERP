namespace Ogfi.Modules.DurableOperations;

public sealed class OperationCheckpoint
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OperationId { get; set; }
    public int Sequence { get; set; }
    public required string CheckpointKey { get; set; }
    public int ProgressPercentage { get; set; }
    public required string SafeDetailJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
