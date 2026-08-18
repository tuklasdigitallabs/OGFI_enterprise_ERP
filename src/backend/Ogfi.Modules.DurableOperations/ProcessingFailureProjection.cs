namespace Ogfi.Modules.DurableOperations;

public static class ProcessingFailureStates
{
    public const string Pending = "PENDING";
    public const string RetryPending = "RETRY_PENDING";
    public const string BusinessFailed = "BUSINESS_FAILED";
    public const string TerminalRejected = "TERMINAL_REJECTED";
    public const string Stalled = "STALLED";
    public const string Recovered = "RECOVERED";
    public const string Completed = "COMPLETED";

    public static bool IsTerminal(string value) => value is TerminalRejected or Completed;

    public static bool IsReplayEligible(string value) => value is
        Pending or RetryPending or BusinessFailed or Stalled;
}

public static class ProcessingFailureClassifications
{
    public const string Transient = "TRANSIENT";
    public const string Business = "BUSINESS";
    public const string ForgedTenant = "FORGED_TENANT";
    public const string MalformedContract = "MALFORMED_CONTRACT";
    public const string Authorization = "AUTHORIZATION";
    public const string SecurityTerminal = "SECURITY_TERMINAL";

    public static bool IsApproved(string value) => value is
        Transient or Business or ForgedTenant or MalformedContract or Authorization or SecurityTerminal;

    public static bool IsTerminalInvalid(string value) => value is
        ForgedTenant or MalformedContract or Authorization or SecurityTerminal;
}

public sealed class ProcessingFailureProjection
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string OwnerModule { get; set; }
    public required string ProcessorCode { get; set; }
    public required string FailureClassification { get; set; }
    public Guid OriginalSourceEventId { get; set; }
    public string? OriginalCausationId { get; set; }
    public required string CorrelationId { get; set; }
    public required string ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public Guid? LegalEntityId { get; set; }
    public Guid? OutletId { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset FirstFailedAtUtc { get; set; }
    public DateTimeOffset LastFailedAtUtc { get; set; }
    public required string SafeErrorCode { get; set; }
    public required string SafeDetailJson { get; set; }
    public bool Replayable { get; set; }
    public Guid? CurrentOperationId { get; set; }
    public Guid? RecoveryOperationId { get; set; }
    public required string State { get; set; }
    public long Version { get; set; }
}
