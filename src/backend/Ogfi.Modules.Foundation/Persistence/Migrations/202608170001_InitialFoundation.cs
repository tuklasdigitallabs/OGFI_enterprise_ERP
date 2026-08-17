using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("202608170001_InitialFoundation")]
public partial class InitialFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "foundation");

        migrationBuilder.CreateTable(
            name: "outbox_messages",
            schema: "foundation",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CausationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_outbox_messages", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_ProcessedAtUtc_OccurredAtUtc",
            schema: "foundation",
            table: "outbox_messages",
            columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_TenantId_Id",
            schema: "foundation",
            table: "outbox_messages",
            columns: new[] { "TenantId", "Id" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "outbox_messages", schema: "foundation");
    }
}
