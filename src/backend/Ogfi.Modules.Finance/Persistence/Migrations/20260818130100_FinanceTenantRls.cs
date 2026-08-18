using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Finance.Persistence.Migrations;

[DbContext(typeof(FinanceDbContext))]
[Migration("20260818130100_FinanceTenantRls")]
public sealed class FinanceTenantRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
                 {
                     "accounting_books", "accounts", "accounting_periods", "posting_rule_versions",
                     "source_postings", "journals", "journal_lines"
                 })
        {
            migrationBuilder.Sql($$"""
                ALTER TABLE finance.{{table}} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE finance.{{table}} FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS tenant_isolation ON finance.{{table}};
                CREATE POLICY tenant_isolation ON finance.{{table}}
                    USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
                 {
                     "journal_lines", "journals", "source_postings", "posting_rule_versions",
                     "accounting_periods", "accounts", "accounting_books"
                 })
        {
            migrationBuilder.Sql($$"""
                DROP POLICY IF EXISTS tenant_isolation ON finance.{{table}};
                ALTER TABLE finance.{{table}} NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE finance.{{table}} DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
