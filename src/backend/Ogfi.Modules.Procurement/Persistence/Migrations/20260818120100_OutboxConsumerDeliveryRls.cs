using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Procurement.Persistence.Migrations;

[DbContext(typeof(ProcurementDbContext))]
[Migration("20260818120100_OutboxConsumerDeliveryRls")]
public sealed class OutboxConsumerDeliveryRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE procurement.outbox_deliveries ENABLE ROW LEVEL SECURITY;
            ALTER TABLE procurement.outbox_deliveries FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON procurement.outbox_deliveries;
            CREATE POLICY tenant_isolation ON procurement.outbox_deliveries
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS tenant_isolation ON procurement.outbox_deliveries;
            ALTER TABLE procurement.outbox_deliveries NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE procurement.outbox_deliveries DISABLE ROW LEVEL SECURITY;
            """);
    }
}
