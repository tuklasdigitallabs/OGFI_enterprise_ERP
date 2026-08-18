using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Finance.Persistence;

namespace Ogfi.Modules.Finance;

public sealed class FinanceSetupService(FinanceDbContext dbContext)
{
    public async Task<AccountingBook> CreatePrimaryBookAsync(
        Guid tenantId,
        Guid legalEntityId,
        string code,
        string name,
        string functionalCurrency,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedCode = Required(code, "FINANCE.BOOK.INVALID", "Accounting Book code is required.").ToUpperInvariant();
        var normalizedName = Required(name, "FINANCE.BOOK.INVALID", "Accounting Book name is required.");
        var currency = NormalizeCurrency(functionalCurrency);
        if (await dbContext.AccountingBooks.AnyAsync(
                x => x.TenantId == tenantId && x.LegalEntityId == legalEntityId && x.IsPrimary,
                cancellationToken))
        {
            throw new FinanceRuleException("FINANCE.BOOK.PRIMARY_EXISTS", "A primary Accounting Book already exists for this Legal Entity.");
        }

        var book = new AccountingBook
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            LegalEntityId = legalEntityId,
            Code = normalizedCode,
            Name = normalizedName,
            FunctionalCurrency = currency,
            IsPrimary = true,
            Status = FinanceStatuses.Active,
            CreatedAtUtc = now
        };
        dbContext.AccountingBooks.Add(book);
        await dbContext.SaveChangesAsync(cancellationToken);
        return book;
    }

    public async Task<Account> CreateAccountAsync(
        Guid tenantId,
        Guid accountingBookId,
        string code,
        string name,
        string accountType,
        string normalBalance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var bookExists = await dbContext.AccountingBooks.AnyAsync(
            x => x.TenantId == tenantId && x.Id == accountingBookId && x.Status == FinanceStatuses.Active,
            cancellationToken);
        if (!bookExists)
        {
            throw new FinanceRuleException("FINANCE.BOOK.MISSING", "Accounting Book does not exist or is inactive.");
        }

        var type = Required(accountType, "FINANCE.ACCOUNT.INVALID", "Account type is required.").ToUpperInvariant();
        if (type is not (FinanceAccountTypes.Asset or FinanceAccountTypes.Liability or FinanceAccountTypes.Equity or FinanceAccountTypes.Revenue or FinanceAccountTypes.Expense))
        {
            throw new FinanceRuleException("FINANCE.ACCOUNT.INVALID", "Account type is not supported.");
        }
        var balance = Required(normalBalance, "FINANCE.ACCOUNT.INVALID", "Normal balance is required.").ToUpperInvariant();
        if (balance is not (FinanceNormalBalances.Debit or FinanceNormalBalances.Credit))
        {
            throw new FinanceRuleException("FINANCE.ACCOUNT.INVALID", "Normal balance must be DEBIT or CREDIT.");
        }

        var account = new Account
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AccountingBookId = accountingBookId,
            Code = Required(code, "FINANCE.ACCOUNT.INVALID", "Account code is required.").ToUpperInvariant(),
            Name = Required(name, "FINANCE.ACCOUNT.INVALID", "Account name is required."),
            AccountType = type,
            NormalBalance = balance,
            PostingEnabled = true,
            Status = FinanceStatuses.Active,
            CreatedAtUtc = now
        };
        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task<AccountingPeriod> CreatePeriodAsync(
        Guid tenantId,
        Guid accountingBookId,
        string name,
        DateOnly startBusinessDate,
        DateOnly endBusinessDate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (endBusinessDate < startBusinessDate)
        {
            throw new FinanceRuleException("FINANCE.PERIOD.INVALID", "Accounting Period end date cannot precede start date.");
        }
        if (!await dbContext.AccountingBooks.AnyAsync(
                x => x.TenantId == tenantId && x.Id == accountingBookId && x.Status == FinanceStatuses.Active,
                cancellationToken))
        {
            throw new FinanceRuleException("FINANCE.BOOK.MISSING", "Accounting Book does not exist or is inactive.");
        }
        if (await dbContext.AccountingPeriods.AnyAsync(
                x => x.TenantId == tenantId
                     && x.AccountingBookId == accountingBookId
                     && x.StartBusinessDate <= endBusinessDate
                     && x.EndBusinessDate >= startBusinessDate,
                cancellationToken))
        {
            throw new FinanceRuleException("FINANCE.PERIOD.OVERLAP", "Accounting Period overlaps an existing period.");
        }

        var period = new AccountingPeriod
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AccountingBookId = accountingBookId,
            Name = Required(name, "FINANCE.PERIOD.INVALID", "Accounting Period name is required."),
            StartBusinessDate = startBusinessDate,
            EndBusinessDate = endBusinessDate,
            Status = FinanceStatuses.Future,
            CreatedAtUtc = now
        };
        dbContext.AccountingPeriods.Add(period);
        await dbContext.SaveChangesAsync(cancellationToken);
        return period;
    }

    public async Task<AccountingPeriod> SetPeriodStatusAsync(
        Guid tenantId,
        Guid accountingPeriodId,
        string status,
        Guid actorUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalized = Required(status, "FINANCE.PERIOD.INVALID", "Accounting Period status is required.").ToUpperInvariant();
        if (normalized is not (FinanceStatuses.Future or FinanceStatuses.Open or FinanceStatuses.SoftClosed or FinanceStatuses.Closed))
        {
            throw new FinanceRuleException("FINANCE.PERIOD.INVALID", "Accounting Period status is not supported.");
        }
        var period = await dbContext.AccountingPeriods.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == accountingPeriodId,
            cancellationToken)
            ?? throw new FinanceRuleException("FINANCE.PERIOD.MISSING", "Accounting Period does not exist.");
        period.Status = normalized;
        if (normalized == FinanceStatuses.Open)
        {
            period.OpenedAtUtc = now;
            period.OpenedByUserId = actorUserId;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return period;
    }

    public async Task<PostingRuleVersion> CreateGoodsReceiptPostingRuleAsync(
        Guid tenantId,
        Guid accountingBookId,
        int version,
        string name,
        DateOnly effectiveFromBusinessDate,
        DateOnly? effectiveToBusinessDate,
        Guid debitAccountId,
        Guid creditAccountId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (version <= 0 || (effectiveToBusinessDate is DateOnly end && end < effectiveFromBusinessDate))
        {
            throw new FinanceRuleException("FINANCE.POSTING_RULE.INVALID", "Posting Rule version or effective dates are invalid.");
        }
        var accounts = await dbContext.Accounts
            .Where(x => x.TenantId == tenantId
                        && x.AccountingBookId == accountingBookId
                        && (x.Id == debitAccountId || x.Id == creditAccountId))
            .ToListAsync(cancellationToken);
        if (accounts.Count != 2 || accounts.Any(x => x.Status != FinanceStatuses.Active || !x.PostingEnabled) || debitAccountId == creditAccountId)
        {
            throw new FinanceRuleException("FINANCE.ACCOUNT.INVALID", "Posting Rule accounts must be distinct, active, posting-enabled Accounts in the same Book.");
        }
        if (await dbContext.PostingRuleVersions.AnyAsync(
                x => x.TenantId == tenantId
                     && x.AccountingBookId == accountingBookId
                     && x.SourceType == FinanceSourceTypes.GoodsReceiptPosted
                     && x.EffectiveFromBusinessDate <= (effectiveToBusinessDate ?? DateOnly.MaxValue)
                     && (x.EffectiveToBusinessDate == null || x.EffectiveToBusinessDate >= effectiveFromBusinessDate),
                cancellationToken))
        {
            throw new FinanceRuleException("FINANCE.POSTING_RULE.OVERLAP", "Posting Rule effective range overlaps an existing version.");
        }

        var rule = new PostingRuleVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AccountingBookId = accountingBookId,
            Code = "PROCUREMENT.GOODS_RECEIPT_POSTED",
            SourceType = FinanceSourceTypes.GoodsReceiptPosted,
            Version = version,
            Name = Required(name, "FINANCE.POSTING_RULE.INVALID", "Posting Rule name is required."),
            EffectiveFromBusinessDate = effectiveFromBusinessDate,
            EffectiveToBusinessDate = effectiveToBusinessDate,
            DebitAccountId = debitAccountId,
            CreditAccountId = creditAccountId,
            Status = FinanceStatuses.Active,
            CreatedAtUtc = now
        };
        dbContext.PostingRuleVersions.Add(rule);
        await dbContext.SaveChangesAsync(cancellationToken);
        return rule;
    }

    private static string Required(string value, string code, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FinanceRuleException(code, message);
        return value.Trim();
    }

    private static string NormalizeCurrency(string value)
    {
        var currency = Required(value, "FINANCE.CURRENCY.INVALID", "Currency is required.").ToUpperInvariant();
        if (currency.Length != 3) throw new FinanceRuleException("FINANCE.CURRENCY.INVALID", "Currency must be a three-character code.");
        return currency;
    }
}

public sealed class FinancePostingService(FinanceDbContext dbContext)
{
    public async Task<FinanceEligibilityResult> EvaluateAsync(
        Guid tenantId,
        GoodsReceiptPostedV1 payload,
        CancellationToken cancellationToken)
    {
        if (payload.TenantId != tenantId)
        {
            return Failure("FINANCE.EVENT.TENANT_MISMATCH", "Goods Receipt event tenant does not match the active tenant.");
        }
        if (payload.Lines.Count == 0 || payload.Lines.Any(x => x.LineNetAmount <= 0))
        {
            return Failure("FINANCE.EVENT.INVALID", "Goods Receipt event must contain positive line amounts.");
        }

        var books = await dbContext.AccountingBooks.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && x.LegalEntityId == payload.LegalEntityId
                        && x.IsPrimary
                        && x.Status == FinanceStatuses.Active)
            .ToListAsync(cancellationToken);
        if (books.Count == 0) return Failure("FINANCE.BOOK.MISSING", "Primary Accounting Book is not configured.");
        if (books.Count != 1) return Failure("FINANCE.BOOK.AMBIGUOUS", "Primary Accounting Book configuration is ambiguous.");
        var book = books[0];

        if (!string.Equals(book.FunctionalCurrency, payload.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("FINANCE.CURRENCY.UNSUPPORTED", "Goods Receipt currency differs from the Accounting Book functional currency.", book.Id);
        }

        var periods = await dbContext.AccountingPeriods.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && x.AccountingBookId == book.Id
                        && x.StartBusinessDate <= payload.BusinessDate
                        && x.EndBusinessDate >= payload.BusinessDate)
            .ToListAsync(cancellationToken);
        if (periods.Count == 0) return Failure("FINANCE.PERIOD.MISSING", "No Accounting Period covers the Goods Receipt Business Date.", book.Id);
        if (periods.Count != 1) return Failure("FINANCE.PERIOD.AMBIGUOUS", "Multiple Accounting Periods cover the Goods Receipt Business Date.", book.Id);
        var period = periods[0];
        if (period.Status != FinanceStatuses.Open)
        {
            return Failure("FINANCE.PERIOD.NOT_OPEN", $"Accounting Period is {period.Status}; only OPEN permits posting.", book.Id, period.Id);
        }

        var rules = await dbContext.PostingRuleVersions.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && x.AccountingBookId == book.Id
                        && x.SourceType == FinanceSourceTypes.GoodsReceiptPosted
                        && x.Status == FinanceStatuses.Active
                        && x.EffectiveFromBusinessDate <= payload.BusinessDate
                        && (x.EffectiveToBusinessDate == null || x.EffectiveToBusinessDate >= payload.BusinessDate))
            .ToListAsync(cancellationToken);
        if (rules.Count == 0) return Failure("FINANCE.POSTING_RULE.MISSING", "No effective Goods Receipt Posting Rule is configured.", book.Id, period.Id);
        if (rules.Count != 1) return Failure("FINANCE.POSTING_RULE.AMBIGUOUS", "Multiple Goods Receipt Posting Rules are effective.", book.Id, period.Id);
        var rule = rules[0];

        var accounts = await dbContext.Accounts.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && x.AccountingBookId == book.Id
                        && (x.Id == rule.DebitAccountId || x.Id == rule.CreditAccountId))
            .ToListAsync(cancellationToken);
        var debit = accounts.SingleOrDefault(x => x.Id == rule.DebitAccountId);
        var credit = accounts.SingleOrDefault(x => x.Id == rule.CreditAccountId);
        if (debit is null || credit is null
            || debit.Status != FinanceStatuses.Active || credit.Status != FinanceStatuses.Active
            || !debit.PostingEnabled || !credit.PostingEnabled)
        {
            return Failure("FINANCE.ACCOUNT.INVALID", "Posting Rule Accounts are missing, inactive or not posting-enabled.", book.Id, period.Id, rule.Id);
        }

        return new FinanceEligibilityResult(true, null, null, book.Id, period.Id, rule.Id, debit.Id, credit.Id);
    }

    public Task<FinanceSourcePosting> ApplyAsync(
        Guid tenantId,
        GoodsReceiptPostedV1 payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => ApplyCoreAsync(tenantId, payload, false, null, now, cancellationToken);

    public async Task<FinanceSourcePosting> ReplayAsync(
        Guid tenantId,
        Guid sourcePostingId,
        Guid actorUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var source = await dbContext.SourcePostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sourcePostingId, cancellationToken)
            ?? throw new FinanceRuleException("FINANCE.SOURCE_POSTING.NOT_FOUND", "Finance Source Posting does not exist.");
        if (source.Status != FinanceStatuses.Failed)
        {
            throw new FinanceRuleException("FINANCE.SOURCE_POSTING.NOT_REPLAYABLE", "Only a FAILED Finance Source Posting can be replayed.");
        }
        var payload = JsonSerializer.Deserialize<GoodsReceiptPostedV1>(source.PayloadJson)
            ?? throw new FinanceRuleException("FINANCE.EVENT.INVALID", "Stored source payload is invalid.");
        return await ApplyCoreAsync(tenantId, payload, true, actorUserId, now, cancellationToken);
    }

    public async Task<FinanceEligibilityResult> EvaluateStoredAsync(
        Guid tenantId,
        Guid sourcePostingId,
        CancellationToken cancellationToken)
    {
        var source = await dbContext.SourcePostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sourcePostingId, cancellationToken)
            ?? throw new FinanceRuleException("FINANCE.SOURCE_POSTING.NOT_FOUND", "Finance Source Posting does not exist.");
        var payload = JsonSerializer.Deserialize<GoodsReceiptPostedV1>(source.PayloadJson)
            ?? throw new FinanceRuleException("FINANCE.EVENT.INVALID", "Stored source payload is invalid.");
        return await EvaluateAsync(tenantId, payload, cancellationToken);
    }

    private async Task<FinanceSourcePosting> ApplyCoreAsync(
        Guid tenantId,
        GoodsReceiptPostedV1 payload,
        bool replay,
        Guid? replayActorUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var source = await dbContext.SourcePostings
            .SingleOrDefaultAsync(x => x.TenantId == tenantId
                                       && x.SourceType == FinanceSourceTypes.GoodsReceiptPosted
                                       && x.SourceEventId == payload.EventId,
                                  cancellationToken);
        if (source is not null)
        {
            if (!string.Equals(source.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                throw new FinanceRuleException("FINANCE.EVENT.CONFLICT", "Source event identity was reused with a different immutable payload.");
            }
            if (source.Status == FinanceStatuses.Posted)
            {
                await transaction.CommitAsync(cancellationToken);
                return source;
            }
            if (source.Status == FinanceStatuses.Failed && !replay)
            {
                await transaction.CommitAsync(cancellationToken);
                return source;
            }
        }
        else
        {
            source = new FinanceSourcePosting
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SourceEventId = payload.EventId,
                SourceType = FinanceSourceTypes.GoodsReceiptPosted,
                SourceSchemaVersion = 1,
                GoodsReceiptId = payload.GoodsReceiptId,
                GoodsReceiptNumber = payload.GoodsReceiptNumber,
                PurchaseOrderId = payload.PurchaseOrderId,
                SupplierId = payload.SupplierId,
                LegalEntityId = payload.LegalEntityId,
                OutletId = payload.OutletId,
                BusinessDate = payload.BusinessDate,
                Currency = payload.Currency.ToUpperInvariant(),
                CorrelationId = payload.CorrelationId,
                PayloadJson = payloadJson,
                PayloadHash = payloadHash,
                Status = FinanceStatuses.Pending,
                CreatedAtUtc = now
            };
            dbContext.SourcePostings.Add(source);
        }

        source.Status = FinanceStatuses.Pending;
        source.ErrorCode = null;
        source.ErrorDetail = null;
        source.AttemptCount++;
        source.LastAttemptAtUtc = now;
        if (replay)
        {
            source.ReplayCount++;
            source.LastReplayAtUtc = now;
            source.LastReplayByUserId = replayActorUserId;
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        var eligibility = await EvaluateAsync(tenantId, payload, cancellationToken);
        if (!eligibility.Eligible)
        {
            source.Status = FinanceStatuses.Failed;
            source.ErrorCode = eligibility.ErrorCode;
            source.ErrorDetail = eligibility.ErrorDetail;
            source.JournalId = null;
            source.PostedAtUtc = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return source;
        }

        var book = await dbContext.AccountingBooks.AsNoTracking().SingleAsync(x => x.Id == eligibility.AccountingBookId, cancellationToken);
        var rule = await dbContext.PostingRuleVersions.AsNoTracking().SingleAsync(x => x.Id == eligibility.PostingRuleVersionId, cancellationToken);
        var accounts = await dbContext.Accounts.AsNoTracking()
            .Where(x => x.Id == eligibility.DebitAccountId || x.Id == eligibility.CreditAccountId)
            .ToListAsync(cancellationToken);
        var debitAccount = accounts.Single(x => x.Id == eligibility.DebitAccountId);
        var creditAccount = accounts.Single(x => x.Id == eligibility.CreditAccountId);

        var total = decimal.Round(payload.Lines.Sum(x => x.LineNetAmount), 4, MidpointRounding.ToEven);
        if (total <= 0)
        {
            source.Status = FinanceStatuses.Failed;
            source.ErrorCode = "FINANCE.EVENT.INVALID";
            source.ErrorDetail = "Goods Receipt source total must be positive.";
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return source;
        }

        var journal = new Journal
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AccountingBookId = book.Id,
            Number = $"JRN-{payload.BusinessDate:yyyyMMdd}-{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            LegalEntityId = payload.LegalEntityId,
            BusinessDate = payload.BusinessDate,
            PostingDate = payload.BusinessDate,
            Currency = payload.Currency.ToUpperInvariant(),
            Status = FinanceStatuses.Posted,
            SourcePostingId = source.Id,
            SourceEventId = payload.EventId,
            GoodsReceiptId = payload.GoodsReceiptId,
            GoodsReceiptNumber = payload.GoodsReceiptNumber,
            PostingRuleVersionId = rule.Id,
            PostingRuleCodeSnapshot = rule.Code,
            PostingRuleVersionNumber = rule.Version,
            TotalDebit = total,
            TotalCredit = total,
            CorrelationId = payload.CorrelationId,
            PostedAtUtc = now
        };
        dbContext.Journals.Add(journal);

        var lineNumber = 1;
        foreach (var sourceLine in payload.Lines.OrderBy(x => x.LineNumber))
        {
            var amount = decimal.Round(sourceLine.LineNetAmount, 4, MidpointRounding.ToEven);
            dbContext.JournalLines.Add(new JournalLine
            {
                Id = Guid.NewGuid(), TenantId = tenantId, JournalId = journal.Id, LineNumber = lineNumber++,
                AccountId = debitAccount.Id, AccountCodeSnapshot = debitAccount.Code, AccountNameSnapshot = debitAccount.Name,
                DebitAmount = amount, CreditAmount = 0m,
                GoodsReceiptLineId = sourceLine.GoodsReceiptLineId, PurchaseOrderId = payload.PurchaseOrderId,
                PurchaseOrderLineId = sourceLine.PurchaseOrderLineId, SupplierId = payload.SupplierId, OutletId = payload.OutletId,
                StockLocationId = payload.StockLocationId, CatalogItemId = sourceLine.CatalogItemId,
                SourceLineAmount = amount,
                Description = $"Goods Receipt {payload.GoodsReceiptNumber} line {sourceLine.LineNumber} - {sourceLine.CatalogItemCodeSnapshot}"
            });
            dbContext.JournalLines.Add(new JournalLine
            {
                Id = Guid.NewGuid(), TenantId = tenantId, JournalId = journal.Id, LineNumber = lineNumber++,
                AccountId = creditAccount.Id, AccountCodeSnapshot = creditAccount.Code, AccountNameSnapshot = creditAccount.Name,
                DebitAmount = 0m, CreditAmount = amount,
                GoodsReceiptLineId = sourceLine.GoodsReceiptLineId, PurchaseOrderId = payload.PurchaseOrderId,
                PurchaseOrderLineId = sourceLine.PurchaseOrderLineId, SupplierId = payload.SupplierId, OutletId = payload.OutletId,
                StockLocationId = payload.StockLocationId, CatalogItemId = sourceLine.CatalogItemId,
                SourceLineAmount = amount,
                Description = $"Goods Receipt {payload.GoodsReceiptNumber} line {sourceLine.LineNumber} - {sourceLine.CatalogItemCodeSnapshot}"
            });
        }

        source.Status = FinanceStatuses.Posted;
        source.ErrorCode = null;
        source.ErrorDetail = null;
        source.JournalId = journal.Id;
        source.PostedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var debitTotal = await dbContext.JournalLines.Where(x => x.JournalId == journal.Id).SumAsync(x => x.DebitAmount, cancellationToken);
        var creditTotal = await dbContext.JournalLines.Where(x => x.JournalId == journal.Id).SumAsync(x => x.CreditAmount, cancellationToken);
        if (debitTotal != creditTotal || debitTotal != total)
        {
            throw new FinanceRuleException("FINANCE.JOURNAL.UNBALANCED", "Generated Journal is not balanced.");
        }

        await transaction.CommitAsync(cancellationToken);
        return source;
    }

    private static FinanceEligibilityResult Failure(
        string code,
        string detail,
        Guid? bookId = null,
        Guid? periodId = null,
        Guid? ruleId = null)
        => new(false, code, detail, bookId, periodId, ruleId, null, null);
}
