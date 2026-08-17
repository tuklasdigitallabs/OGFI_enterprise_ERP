using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Inventory.Persistence.Migrations;

[DbContext(typeof(InventoryDbContext))]
[Migration("20260818100110_InventoryMovementTenantRls")]
public sealed class InventoryMovementTenantRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "inventory_source_effects", "inventory_movements", "stock_positions" })
        {
            migrationBuilder.Sql($$"""
                ALTER TABLE inventory.{{table}} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE inventory.{{table}} FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS tenant_isolation ON inventory.{{table}};
                CREATE POLICY tenant_isolation ON inventory.{{table}}
                    USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "stock_positions", "inventory_movements", "inventory_source_effects" })
        {
            migrationBuilder.Sql($$"""
                DROP POLICY IF EXISTS tenant_isolation ON inventory.{{table}};
                ALTER TABLE inventory.{{table}} NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE inventory.{{table}} DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
