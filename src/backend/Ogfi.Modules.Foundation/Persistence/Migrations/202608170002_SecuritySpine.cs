using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("202608170002_SecuritySpine")]
public partial class SecuritySpine : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE foundation.tenants (
                "Id" uuid NOT NULL,
                "Code" character varying(50) NOT NULL,
                "Name" character varying(200) NOT NULL,
                CONSTRAINT "PK_tenants" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_tenants_Code" UNIQUE ("Code")
            );

            CREATE TABLE foundation.users (
                "Id" uuid NOT NULL,
                "ExternalSubject" character varying(200) NOT NULL,
                "DisplayName" character varying(200) NOT NULL,
                CONSTRAINT "PK_users" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_users_ExternalSubject" UNIQUE ("ExternalSubject")
            );

            CREATE TABLE foundation.legal_entities (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "Code" character varying(50) NOT NULL,
                "Name" character varying(200) NOT NULL,
                CONSTRAINT "PK_legal_entities" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_legal_entities_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_legal_entities_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES foundation.tenants ("Id") ON DELETE RESTRICT,
                CONSTRAINT "AK_legal_entities_TenantId_Code" UNIQUE ("TenantId", "Code")
            );

            CREATE TABLE foundation.outlets (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "LegalEntityId" uuid NOT NULL,
                "Code" character varying(50) NOT NULL,
                "Name" character varying(200) NOT NULL,
                "TimeZoneId" character varying(100) NOT NULL,
                "BusinessDayStartMinutes" integer NOT NULL,
                CONSTRAINT "PK_outlets" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_outlets_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_outlets_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES foundation.tenants ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_outlets_legal_entities_TenantId_LegalEntityId" FOREIGN KEY ("TenantId", "LegalEntityId") REFERENCES foundation.legal_entities ("TenantId", "Id") ON DELETE RESTRICT,
                CONSTRAINT "AK_outlets_TenantId_Code" UNIQUE ("TenantId", "Code"),
                CONSTRAINT "CK_outlets_business_day_start" CHECK ("BusinessDayStartMinutes" >= 0 AND "BusinessDayStartMinutes" < 1440)
            );

            CREATE TABLE foundation.tenant_memberships (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                "Status" character varying(20) NOT NULL,
                CONSTRAINT "PK_tenant_memberships" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_tenant_memberships_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_tenant_memberships_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES foundation.tenants ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_tenant_memberships_users_UserId" FOREIGN KEY ("UserId") REFERENCES foundation.users ("Id") ON DELETE RESTRICT,
                CONSTRAINT "AK_tenant_memberships_TenantId_UserId" UNIQUE ("TenantId", "UserId")
            );

            CREATE TABLE foundation.permission_grants (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "MembershipId" uuid NOT NULL,
                "PermissionCode" character varying(120) NOT NULL,
                CONSTRAINT "PK_permission_grants" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_permission_grants_membership" FOREIGN KEY ("TenantId", "MembershipId") REFERENCES foundation.tenant_memberships ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "AK_permission_grants_scope" UNIQUE ("TenantId", "MembershipId", "PermissionCode")
            );

            CREATE TABLE foundation.outlet_scope_grants (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "MembershipId" uuid NOT NULL,
                "OutletId" uuid NOT NULL,
                CONSTRAINT "PK_outlet_scope_grants" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_outlet_scope_grants_membership" FOREIGN KEY ("TenantId", "MembershipId") REFERENCES foundation.tenant_memberships ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "FK_outlet_scope_grants_outlet" FOREIGN KEY ("TenantId", "OutletId") REFERENCES foundation.outlets ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "AK_outlet_scope_grants_scope" UNIQUE ("TenantId", "MembershipId", "OutletId")
            );

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
            DROP TABLE IF EXISTS foundation.outlet_scope_grants;
            DROP TABLE IF EXISTS foundation.permission_grants;
            DROP TABLE IF EXISTS foundation.tenant_memberships;
            DROP TABLE IF EXISTS foundation.outlets;
            DROP TABLE IF EXISTS foundation.legal_entities;
            DROP TABLE IF EXISTS foundation.users;
            DROP TABLE IF EXISTS foundation.tenants;
            """);
    }
}
