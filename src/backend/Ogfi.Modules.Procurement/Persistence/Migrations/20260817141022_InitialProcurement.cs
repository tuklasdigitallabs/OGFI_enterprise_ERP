using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Procurement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialProcurement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "procurement");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "procurement",
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
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                schema: "procurement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.Id);
                    table.UniqueConstraint("AK_suppliers_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                schema: "procurement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierCodeSnapshot = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SupplierNameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalNetAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_orders", x => x.Id);
                    table.UniqueConstraint("AK_purchase_orders_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_purchase_orders_suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "procurement",
                        principalTable: "suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "supplier_offers",
                schema: "procurement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemCodeSnapshot = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CatalogItemNameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SupplierItemCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PurchaseUomId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseUomCodeSnapshot = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BaseUomId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseUomCodeSnapshot = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConversionNumerator = table.Column<long>(type: "bigint", nullable: false),
                    ConversionDenominator = table.Column<long>(type: "bigint", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveFromBusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveToBusinessDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_offers", x => x.Id);
                    table.CheckConstraint("CK_supplier_offer_conversion_denominator", "\"ConversionDenominator\" > 0");
                    table.CheckConstraint("CK_supplier_offer_conversion_numerator", "\"ConversionNumerator\" > 0");
                    table.CheckConstraint("CK_supplier_offer_dates", "\"EffectiveToBusinessDate\" IS NULL OR \"EffectiveToBusinessDate\" >= \"EffectiveFromBusinessDate\"");
                    table.CheckConstraint("CK_supplier_offer_price", "\"UnitPrice\" >= 0");
                    table.ForeignKey(
                        name: "FK_supplier_offers_suppliers_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalSchema: "procurement",
                        principalTable: "suppliers",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchase_order_lines",
                schema: "procurement",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    SupplierOfferId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemCodeSnapshot = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CatalogItemNameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrderQuantity = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false),
                    PurchaseUomId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseUomCodeSnapshot = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BaseUomId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseUomCodeSnapshot = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConversionNumerator = table.Column<long>(type: "bigint", nullable: false),
                    ConversionDenominator = table.Column<long>(type: "bigint", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    LineNetAmount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_order_lines", x => x.Id);
                    table.CheckConstraint("CK_purchase_order_line_conversion_denominator", "\"ConversionDenominator\" > 0");
                    table.CheckConstraint("CK_purchase_order_line_conversion_numerator", "\"ConversionNumerator\" > 0");
                    table.CheckConstraint("CK_purchase_order_line_quantity", "\"OrderQuantity\" > 0");
                    table.ForeignKey(
                        name: "FK_purchase_order_lines_purchase_orders_TenantId_PurchaseOrder~",
                        columns: x => new { x.TenantId, x.PurchaseOrderId },
                        principalSchema: "procurement",
                        principalTable: "purchase_orders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAtUtc_OccurredAtUtc",
                schema: "procurement",
                table: "outbox_messages",
                columns: new[] { "ProcessedAtUtc", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_TenantId_Type_CausationId",
                schema: "procurement",
                table: "outbox_messages",
                columns: new[] { "TenantId", "Type", "CausationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_order_lines_TenantId_PurchaseOrderId_LineNumber",
                schema: "procurement",
                table: "purchase_order_lines",
                columns: new[] { "TenantId", "PurchaseOrderId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_TenantId_Number",
                schema: "procurement",
                table: "purchase_orders",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_TenantId_SupplierId",
                schema: "procurement",
                table: "purchase_orders",
                columns: new[] { "TenantId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_offers_TenantId_SupplierId_CatalogItemId_PurchaseU~",
                schema: "procurement",
                table: "supplier_offers",
                columns: new[] { "TenantId", "SupplierId", "CatalogItemId", "PurchaseUomId", "EffectiveFromBusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_TenantId_Code",
                schema: "procurement",
                table: "suppliers",
                columns: new[] { "TenantId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "purchase_order_lines",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "supplier_offers",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "purchase_orders",
                schema: "procurement");

            migrationBuilder.DropTable(
                name: "suppliers",
                schema: "procurement");
        }
    }
}
