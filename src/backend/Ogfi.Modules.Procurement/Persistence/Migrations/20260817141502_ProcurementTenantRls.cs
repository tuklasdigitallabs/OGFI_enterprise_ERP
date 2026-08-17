using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Procurement.Persistence.Migrations;

[DbContext(typeof(ProcurementDbContext))]
[Migration("20260817141502_ProcurementTenantRls")]
public sealed class ProcurementTenantRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "suppliers", "supplier_offers", "purchase_orders", "purchase_order_lines", "outbox_messages" })
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
        foreach (var table in new[] { "outbox_messages", "purchase_order_lines", "purchase_orders", "supplier_offers", "suppliers" })
        {
            migrationBuilder.Sql($$"""
                DROP POLICY IF EXISTS tenant_isolation ON procurement.{{table}};
                ALTER TABLE procurement.{{table}} NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE procurement.{{table}} DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
