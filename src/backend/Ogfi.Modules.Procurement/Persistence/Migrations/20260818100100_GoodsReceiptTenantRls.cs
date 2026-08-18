using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Procurement.Persistence.Migrations;

[DbContext(typeof(ProcurementDbContext))]
[Migration("20260818100100_GoodsReceiptTenantRls")]
public sealed class GoodsReceiptTenantRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "goods_receipts", "goods_receipt_lines", "goods_receipt_posting_commands" })
        {
            migrationBuilder.Sql($$"""
                ALTER TABLE procurement.{{table}} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE procurement.{{table}} FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS tenant_isolation ON procurement.{{table}};
                CREATE POLICY tenant_isolation ON procurement.{{table}}
                    USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "goods_receipt_posting_commands", "goods_receipt_lines", "goods_receipts" })
        {
            migrationBuilder.Sql($$"""
                DROP POLICY IF EXISTS tenant_isolation ON procurement.{{table}};
                ALTER TABLE procurement.{{table}} NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE procurement.{{table}} DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
