using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260817132100_SecuritySpineRls")]
public partial class SecuritySpineRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE foundation.legal_entities ENABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.legal_entities FORCE ROW LEVEL SECURITY;
            ALTER TABLE foundation.outlets ENABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.outlets FORCE ROW LEVEL SECURITY;
            ALTER TABLE foundation.tenant_memberships ENABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.tenant_memberships FORCE ROW LEVEL SECURITY;
            ALTER TABLE foundation.permission_grants ENABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.permission_grants FORCE ROW LEVEL SECURITY;
            ALTER TABLE foundation.outlet_scope_grants ENABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.outlet_scope_grants FORCE ROW LEVEL SECURITY;

            CREATE POLICY tenant_isolation ON foundation.legal_entities
                USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            CREATE POLICY tenant_isolation ON foundation.outlets
                USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            CREATE POLICY tenant_isolation ON foundation.tenant_memberships
                USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            CREATE POLICY tenant_isolation ON foundation.permission_grants
                USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            CREATE POLICY tenant_isolation ON foundation.outlet_scope_grants
                USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS tenant_isolation ON foundation.outlet_scope_grants;
            DROP POLICY IF EXISTS tenant_isolation ON foundation.permission_grants;
            DROP POLICY IF EXISTS tenant_isolation ON foundation.tenant_memberships;
            DROP POLICY IF EXISTS tenant_isolation ON foundation.outlets;
            DROP POLICY IF EXISTS tenant_isolation ON foundation.legal_entities;

            ALTER TABLE foundation.outlet_scope_grants DISABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.permission_grants DISABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.tenant_memberships DISABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.outlets DISABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.legal_entities DISABLE ROW LEVEL SECURITY;
            """);
    }
}
