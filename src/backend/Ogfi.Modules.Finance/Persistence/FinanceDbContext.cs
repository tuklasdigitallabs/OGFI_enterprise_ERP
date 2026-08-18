using Microsoft.EntityFrameworkCore;

namespace Ogfi.Modules.Finance.Persistence;

public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public DbSet<AccountingBook> AccountingBooks => Set<AccountingBook>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();
    public DbSet<PostingRuleVersion> PostingRuleVersions => Set<PostingRuleVersion>();
    public DbSet<FinanceSourcePosting> SourcePostings => Set<FinanceSourcePosting>();
    public DbSet<Journal> Journals => Set<Journal>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("finance");

        modelBuilder.Entity<AccountingBook>(entity =>
        {
            entity.ToTable("accounting_books");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(40);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.FunctionalCurrency).HasMaxLength(3);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.IsPrimary })
                .HasFilter("\"IsPrimary\" = TRUE")
                .IsUnique();
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(40);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.AccountType).HasMaxLength(20);
            entity.Property(x => x.NormalBalance).HasMaxLength(10);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.AccountingBookId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<AccountingPeriod>(entity =>
        {
            entity.ToTable("accounting_periods", table =>
            {
                table.HasCheckConstraint("CK_finance_period_dates", "\"EndBusinessDate\" >= \"StartBusinessDate\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.AccountingBookId, x.StartBusinessDate, x.EndBusinessDate }).IsUnique();
        });

        modelBuilder.Entity<PostingRuleVersion>(entity =>
        {
            entity.ToTable("posting_rule_versions", table =>
            {
                table.HasCheckConstraint("CK_finance_rule_version", "\"Version\" > 0");
                table.HasCheckConstraint("CK_finance_rule_dates", "\"EffectiveToBusinessDate\" IS NULL OR \"EffectiveToBusinessDate\" >= \"EffectiveFromBusinessDate\"");
                table.HasCheckConstraint("CK_finance_rule_accounts", "\"DebitAccountId\" <> \"CreditAccountId\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(80);
            entity.Property(x => x.SourceType).HasMaxLength(120);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.AccountingBookId, x.Code, x.Version }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.AccountingBookId, x.SourceType, x.EffectiveFromBusinessDate });
        });

        modelBuilder.Entity<FinanceSourcePosting>(entity =>
        {
            entity.ToTable("source_postings");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.SourceType).HasMaxLength(120);
            entity.Property(x => x.GoodsReceiptNumber).HasMaxLength(60);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.PayloadHash).HasMaxLength(64);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.ErrorCode).HasMaxLength(120);
            entity.Property(x => x.ErrorDetail).HasMaxLength(600);
            entity.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceEventId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
        });

        modelBuilder.Entity<Journal>(entity =>
        {
            entity.ToTable("journals", table =>
            {
                table.HasCheckConstraint("CK_finance_journal_positive", "\"TotalDebit\" > 0 AND \"TotalCredit\" > 0");
                table.HasCheckConstraint("CK_finance_journal_balanced", "\"TotalDebit\" = \"TotalCredit\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Number).HasMaxLength(60);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.GoodsReceiptNumber).HasMaxLength(60);
            entity.Property(x => x.PostingRuleCodeSnapshot).HasMaxLength(80);
            entity.Property(x => x.TotalDebit).HasPrecision(19, 4);
            entity.Property(x => x.TotalCredit).HasPrecision(19, 4);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.SourcePostingId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.BusinessDate });
        });

        modelBuilder.Entity<JournalLine>(entity =>
        {
            entity.ToTable("journal_lines", table =>
            {
                table.HasCheckConstraint("CK_finance_journal_line_one_side", "(\"DebitAmount\" > 0 AND \"CreditAmount\" = 0) OR (\"CreditAmount\" > 0 AND \"DebitAmount\" = 0)");
                table.HasCheckConstraint("CK_finance_journal_line_source_amount", "\"SourceLineAmount\" > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AccountCodeSnapshot).HasMaxLength(40);
            entity.Property(x => x.AccountNameSnapshot).HasMaxLength(160);
            entity.Property(x => x.DebitAmount).HasPrecision(19, 4);
            entity.Property(x => x.CreditAmount).HasPrecision(19, 4);
            entity.Property(x => x.SourceLineAmount).HasPrecision(19, 4);
            entity.Property(x => x.Description).HasMaxLength(240);
            entity.HasIndex(x => new { x.TenantId, x.JournalId, x.LineNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.GoodsReceiptLineId, x.AccountId }).IsUnique();
        });
    }
}
