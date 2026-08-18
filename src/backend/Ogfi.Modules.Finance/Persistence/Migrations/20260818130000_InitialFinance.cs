using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Finance.Persistence.Migrations;

[DbContext(typeof(FinanceDbContext))]
[Migration("20260818130000_InitialFinance")]
public sealed class InitialFinance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SCHEMA IF NOT EXISTS finance;

            CREATE TABLE finance.accounting_books (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "LegalEntityId" uuid NOT NULL,
                "Code" varchar(40) NOT NULL,
                "Name" varchar(160) NOT NULL,
                "FunctionalCurrency" varchar(3) NOT NULL,
                "IsPrimary" boolean NOT NULL,
                "Status" varchar(20) NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX "IX_accounting_books_TenantId_Code" ON finance.accounting_books ("TenantId", "Code");
            CREATE UNIQUE INDEX "IX_accounting_books_TenantId_LegalEntityId_IsPrimary" ON finance.accounting_books ("TenantId", "LegalEntityId", "IsPrimary") WHERE "IsPrimary" = TRUE;
            ALTER TABLE finance.accounting_books ADD CONSTRAINT "AK_accounting_books_TenantId_Id" UNIQUE ("TenantId", "Id");

            CREATE TABLE finance.accounts (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "AccountingBookId" uuid NOT NULL,
                "Code" varchar(40) NOT NULL,
                "Name" varchar(160) NOT NULL,
                "AccountType" varchar(20) NOT NULL,
                "NormalBalance" varchar(10) NOT NULL,
                "PostingEnabled" boolean NOT NULL,
                "Status" varchar(20) NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX "IX_accounts_TenantId_AccountingBookId_Code" ON finance.accounts ("TenantId", "AccountingBookId", "Code");
            ALTER TABLE finance.accounts ADD CONSTRAINT "AK_accounts_TenantId_Id" UNIQUE ("TenantId", "Id");

            CREATE TABLE finance.accounting_periods (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "AccountingBookId" uuid NOT NULL,
                "Name" varchar(80) NOT NULL,
                "StartBusinessDate" date NOT NULL,
                "EndBusinessDate" date NOT NULL,
                "Status" varchar(20) NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                "OpenedAtUtc" timestamptz NULL,
                "OpenedByUserId" uuid NULL,
                CONSTRAINT "CK_finance_period_dates" CHECK ("EndBusinessDate" >= "StartBusinessDate")
            );
            CREATE UNIQUE INDEX "IX_accounting_periods_TenantId_AccountingBookId_StartBusinessDate_EndBusinessDate" ON finance.accounting_periods ("TenantId", "AccountingBookId", "StartBusinessDate", "EndBusinessDate");
            ALTER TABLE finance.accounting_periods ADD CONSTRAINT "AK_accounting_periods_TenantId_Id" UNIQUE ("TenantId", "Id");

            CREATE TABLE finance.posting_rule_versions (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "AccountingBookId" uuid NOT NULL,
                "Code" varchar(80) NOT NULL,
                "SourceType" varchar(120) NOT NULL,
                "Version" integer NOT NULL,
                "Name" varchar(160) NOT NULL,
                "EffectiveFromBusinessDate" date NOT NULL,
                "EffectiveToBusinessDate" date NULL,
                "DebitAccountId" uuid NOT NULL,
                "CreditAccountId" uuid NOT NULL,
                "Status" varchar(20) NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "CK_finance_rule_version" CHECK ("Version" > 0),
                CONSTRAINT "CK_finance_rule_dates" CHECK ("EffectiveToBusinessDate" IS NULL OR "EffectiveToBusinessDate" >= "EffectiveFromBusinessDate"),
                CONSTRAINT "CK_finance_rule_accounts" CHECK ("DebitAccountId" <> "CreditAccountId")
            );
            CREATE UNIQUE INDEX "IX_posting_rule_versions_TenantId_AccountingBookId_Code_Version" ON finance.posting_rule_versions ("TenantId", "AccountingBookId", "Code", "Version");
            CREATE INDEX "IX_posting_rule_versions_TenantId_AccountingBookId_SourceType_EffectiveFromBusinessDate" ON finance.posting_rule_versions ("TenantId", "AccountingBookId", "SourceType", "EffectiveFromBusinessDate");
            ALTER TABLE finance.posting_rule_versions ADD CONSTRAINT "AK_posting_rule_versions_TenantId_Id" UNIQUE ("TenantId", "Id");

            CREATE TABLE finance.source_postings (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "SourceEventId" uuid NOT NULL,
                "SourceType" varchar(120) NOT NULL,
                "SourceSchemaVersion" integer NOT NULL,
                "GoodsReceiptId" uuid NOT NULL,
                "GoodsReceiptNumber" varchar(60) NOT NULL,
                "PurchaseOrderId" uuid NOT NULL,
                "SupplierId" uuid NOT NULL,
                "LegalEntityId" uuid NOT NULL,
                "OutletId" uuid NOT NULL,
                "BusinessDate" date NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "PayloadJson" jsonb NOT NULL,
                "PayloadHash" varchar(64) NOT NULL,
                "Status" varchar(20) NOT NULL,
                "ErrorCode" varchar(120) NULL,
                "ErrorDetail" varchar(600) NULL,
                "JournalId" uuid NULL,
                "AttemptCount" integer NOT NULL,
                "ReplayCount" integer NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                "LastAttemptAtUtc" timestamptz NULL,
                "LastReplayAtUtc" timestamptz NULL,
                "LastReplayByUserId" uuid NULL,
                "PostedAtUtc" timestamptz NULL
            );
            CREATE UNIQUE INDEX "IX_source_postings_TenantId_SourceType_SourceEventId" ON finance.source_postings ("TenantId", "SourceType", "SourceEventId");
            CREATE INDEX "IX_source_postings_TenantId_Status_CreatedAtUtc" ON finance.source_postings ("TenantId", "Status", "CreatedAtUtc");
            ALTER TABLE finance.source_postings ADD CONSTRAINT "AK_source_postings_TenantId_Id" UNIQUE ("TenantId", "Id");

            CREATE TABLE finance.journals (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "AccountingBookId" uuid NOT NULL,
                "Number" varchar(60) NOT NULL,
                "LegalEntityId" uuid NOT NULL,
                "BusinessDate" date NOT NULL,
                "PostingDate" date NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "Status" varchar(20) NOT NULL,
                "SourcePostingId" uuid NOT NULL,
                "SourceEventId" uuid NOT NULL,
                "GoodsReceiptId" uuid NOT NULL,
                "GoodsReceiptNumber" varchar(60) NOT NULL,
                "PostingRuleVersionId" uuid NOT NULL,
                "PostingRuleCodeSnapshot" varchar(80) NOT NULL,
                "PostingRuleVersionNumber" integer NOT NULL,
                "TotalDebit" numeric(19,4) NOT NULL,
                "TotalCredit" numeric(19,4) NOT NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "PostedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "CK_finance_journal_positive" CHECK ("TotalDebit" > 0 AND "TotalCredit" > 0),
                CONSTRAINT "CK_finance_journal_balanced" CHECK ("TotalDebit" = "TotalCredit")
            );
            CREATE UNIQUE INDEX "IX_journals_TenantId_Number" ON finance.journals ("TenantId", "Number");
            CREATE UNIQUE INDEX "IX_journals_TenantId_SourcePostingId" ON finance.journals ("TenantId", "SourcePostingId");
            CREATE INDEX "IX_journals_TenantId_LegalEntityId_BusinessDate" ON finance.journals ("TenantId", "LegalEntityId", "BusinessDate");
            ALTER TABLE finance.journals ADD CONSTRAINT "AK_journals_TenantId_Id" UNIQUE ("TenantId", "Id");

            CREATE TABLE finance.journal_lines (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "JournalId" uuid NOT NULL,
                "LineNumber" integer NOT NULL,
                "AccountId" uuid NOT NULL,
                "AccountCodeSnapshot" varchar(40) NOT NULL,
                "AccountNameSnapshot" varchar(160) NOT NULL,
                "DebitAmount" numeric(19,4) NOT NULL,
                "CreditAmount" numeric(19,4) NOT NULL,
                "GoodsReceiptLineId" uuid NOT NULL,
                "PurchaseOrderId" uuid NOT NULL,
                "PurchaseOrderLineId" uuid NOT NULL,
                "SupplierId" uuid NOT NULL,
                "OutletId" uuid NOT NULL,
                "StockLocationId" uuid NOT NULL,
                "CatalogItemId" uuid NOT NULL,
                "SourceLineAmount" numeric(19,4) NOT NULL,
                "Description" varchar(240) NOT NULL,
                CONSTRAINT "CK_finance_journal_line_one_side" CHECK (("DebitAmount" > 0 AND "CreditAmount" = 0) OR ("CreditAmount" > 0 AND "DebitAmount" = 0)),
                CONSTRAINT "CK_finance_journal_line_source_amount" CHECK ("SourceLineAmount" > 0)
            );
            CREATE UNIQUE INDEX "IX_journal_lines_TenantId_JournalId_LineNumber" ON finance.journal_lines ("TenantId", "JournalId", "LineNumber");
            CREATE UNIQUE INDEX "IX_journal_lines_TenantId_GoodsReceiptLineId_AccountId" ON finance.journal_lines ("TenantId", "GoodsReceiptLineId", "AccountId");

            CREATE OR REPLACE FUNCTION finance.prevent_posted_journal_mutation() RETURNS trigger AS $$
            BEGIN
                IF OLD."Status" = 'POSTED' THEN
                    RAISE EXCEPTION 'Posted Finance Journal is immutable' USING ERRCODE = '55000';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER trg_finance_journal_immutable
                BEFORE UPDATE OR DELETE ON finance.journals
                FOR EACH ROW EXECUTE FUNCTION finance.prevent_posted_journal_mutation();

            CREATE OR REPLACE FUNCTION finance.prevent_journal_line_mutation() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'Finance Journal Line is immutable' USING ERRCODE = '55000';
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER trg_finance_journal_line_immutable
                BEFORE UPDATE OR DELETE ON finance.journal_lines
                FOR EACH ROW EXECUTE FUNCTION finance.prevent_journal_line_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS finance CASCADE;");
    }
}
