using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Catalog.Persistence.Migrations;

[DbContext(typeof(CatalogDbContext))]
[Migration("20260817141500_CatalogTenantRls")]
public sealed class CatalogTenantRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE catalog.items ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog.items FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON catalog.items;
            CREATE POLICY tenant_isolation ON catalog.items
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);

            ALTER TABLE catalog.item_packaging_conversions ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog.item_packaging_conversions FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON catalog.item_packaging_conversions;
            CREATE POLICY tenant_isolation ON catalog.item_packaging_conversions
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS tenant_isolation ON catalog.item_packaging_conversions;
            ALTER TABLE catalog.item_packaging_conversions NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE catalog.item_packaging_conversions DISABLE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON catalog.items;
            ALTER TABLE catalog.items NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE catalog.items DISABLE ROW LEVEL SECURITY;
            """);
    }
}
