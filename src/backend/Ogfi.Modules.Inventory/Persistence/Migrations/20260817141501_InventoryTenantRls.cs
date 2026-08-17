using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Inventory.Persistence.Migrations;

[DbContext(typeof(InventoryDbContext))]
[Migration("20260817141501_InventoryTenantRls")]
public sealed class InventoryTenantRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE inventory.inventory_profiles ENABLE ROW LEVEL SECURITY;
            ALTER TABLE inventory.inventory_profiles FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON inventory.inventory_profiles;
            CREATE POLICY tenant_isolation ON inventory.inventory_profiles
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);

            ALTER TABLE inventory.stock_locations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE inventory.stock_locations FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON inventory.stock_locations;
            CREATE POLICY tenant_isolation ON inventory.stock_locations
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS tenant_isolation ON inventory.stock_locations;
            ALTER TABLE inventory.stock_locations NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE inventory.stock_locations DISABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON inventory.inventory_profiles;
            ALTER TABLE inventory.inventory_profiles NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE inventory.inventory_profiles DISABLE ROW LEVEL SECURITY;
            """);
    }
}
