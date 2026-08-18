using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Procurement.Persistence.Migrations;

[DbContext(typeof(ProcurementDbContext))]
[Migration("20260818120000_OutboxConsumerDeliveries")]
public sealed class OutboxConsumerDeliveries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE procurement.outbox_deliveries (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "OutboxMessageId" uuid NOT NULL,
                "ConsumerCode" varchar(80) NOT NULL,
                "Status" varchar(30) NOT NULL,
                "AttemptCount" integer NOT NULL DEFAULT 0,
                "LastError" varchar(160) NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                "UpdatedAtUtc" timestamptz NOT NULL,
                "CompletedAtUtc" timestamptz NULL
            );
            CREATE UNIQUE INDEX "IX_outbox_deliveries_TenantId_OutboxMessageId_ConsumerCode"
                ON procurement.outbox_deliveries ("TenantId", "OutboxMessageId", "ConsumerCode");
            CREATE INDEX "IX_outbox_deliveries_TenantId_ConsumerCode_Status_UpdatedAtUtc"
                ON procurement.outbox_deliveries ("TenantId", "ConsumerCode", "Status", "UpdatedAtUtc");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS procurement.outbox_deliveries;");
    }
}
