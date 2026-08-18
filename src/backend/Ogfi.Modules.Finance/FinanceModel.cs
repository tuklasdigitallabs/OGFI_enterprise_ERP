namespace Ogfi.Modules.Finance;

public static class FinancePermissionCodes
{
    public const string SetupManage = "finance.setup.manage";
    public const string JournalRead = "finance.journal.read";
    public const string SourcePostingRead = "finance.source_posting.read";
    public const string SourcePostingReplay = "finance.source_posting.replay";
}

public static class FinanceStatuses
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";
    public const string Future = "FUTURE";
    public const string Open = "OPEN";
    public const string SoftClosed = "SOFT_CLOSED";
    public const string Closed = "CLOSED";
    public const string Pending = "PENDING";
    public const string Posted = "POSTED";
    public const string Failed = "FAILED";
}

public static class FinanceAccountTypes
{
    public const string Asset = "ASSET";
    public const string Liability = "LIABILITY";
    public const string Equity = "EQUITY";
    public const string Revenue = "REVENUE";
    public const string Expense = "EXPENSE";
}

public static class FinanceNormalBalances
{
    public const string Debit = "DEBIT";
    public const string Credit = "CREDIT";
}

public static class FinanceSourceTypes
{
    public const string GoodsReceiptPosted = "Procurement.GoodsReceiptPosted";
}

public sealed class AccountingBook
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string FunctionalCurrency { get; set; }
    public bool IsPrimary { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class Account
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AccountingBookId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string AccountType { get; set; }
    public required string NormalBalance { get; set; }
    public bool PostingEnabled { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AccountingPeriod
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AccountingBookId { get; set; }
    public required string Name { get; set; }
    public DateOnly StartBusinessDate { get; set; }
    public DateOnly EndBusinessDate { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? OpenedAtUtc { get; set; }
    public Guid? OpenedByUserId { get; set; }
}

public sealed class PostingRuleVersion
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AccountingBookId { get; set; }
    public required string Code { get; set; }
    public required string SourceType { get; set; }
    public int Version { get; set; }
    public required string Name { get; set; }
    public DateOnly EffectiveFromBusinessDate { get; set; }
    public DateOnly? EffectiveToBusinessDate { get; set; }
    public Guid DebitAccountId { get; set; }
    public Guid CreditAccountId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class FinanceSourcePosting
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SourceEventId { get; set; }
    public required string SourceType { get; set; }
    public int SourceSchemaVersion { get; set; }
    public Guid GoodsReceiptId { get; set; }
    public required string GoodsReceiptNumber { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid OutletId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public required string Currency { get; set; }
    public required string CorrelationId { get; set; }
    public required string PayloadJson { get; set; }
    public required string PayloadHash { get; set; }
    public required string Status { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public Guid? JournalId { get; set; }
    public int AttemptCount { get; set; }
    public int ReplayCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? LastReplayAtUtc { get; set; }
    public Guid? LastReplayByUserId { get; set; }
    public DateTimeOffset? PostedAtUtc { get; set; }
}

public sealed class Journal
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AccountingBookId { get; set; }
    public required string Number { get; set; }
    public Guid LegalEntityId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    public Guid SourcePostingId { get; set; }
    public Guid SourceEventId { get; set; }
    public Guid GoodsReceiptId { get; set; }
    public required string GoodsReceiptNumber { get; set; }
    public Guid PostingRuleVersionId { get; set; }
    public required string PostingRuleCodeSnapshot { get; set; }
    public int PostingRuleVersionNumber { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset PostedAtUtc { get; set; }
}

public sealed class JournalLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid JournalId { get; set; }
    public int LineNumber { get; set; }
    public Guid AccountId { get; set; }
    public required string AccountCodeSnapshot { get; set; }
    public required string AccountNameSnapshot { get; set; }
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public Guid GoodsReceiptLineId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid OutletId { get; set; }
    public Guid StockLocationId { get; set; }
    public Guid CatalogItemId { get; set; }
    public decimal SourceLineAmount { get; set; }
    public required string Description { get; set; }
}

public sealed record FinanceEligibilityResult(
    bool Eligible,
    string? ErrorCode,
    string? ErrorDetail,
    Guid? AccountingBookId,
    Guid? AccountingPeriodId,
    Guid? PostingRuleVersionId,
    Guid? DebitAccountId,
    Guid? CreditAccountId);

public sealed class FinanceRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
