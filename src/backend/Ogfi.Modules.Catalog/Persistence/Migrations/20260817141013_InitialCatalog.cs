using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Ogfi.Modules.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "uoms",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DimensionCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StandardNumerator = table.Column<long>(type: "bigint", nullable: false),
                    StandardDenominator = table.Column<long>(type: "bigint", nullable: false),
                    PrecisionScale = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_uoms", x => x.Id);
                    table.CheckConstraint("CK_uoms_denominator", "\"StandardDenominator\" > 0");
                    table.CheckConstraint("CK_uoms_numerator", "\"StandardNumerator\" > 0");
                    table.CheckConstraint("CK_uoms_precision", "\"PrecisionScale\" >= 0 AND \"PrecisionScale\" <= 9");
                });

            migrationBuilder.CreateTable(
                name: "items",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaseUomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_items", x => x.Id);
                    table.UniqueConstraint("AK_items_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_items_uoms_BaseUomId",
                        column: x => x.BaseUomId,
                        principalSchema: "catalog",
                        principalTable: "uoms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "item_packaging_conversions",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseUomId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseUomId = table.Column<Guid>(type: "uuid", nullable: false),
                    Numerator = table.Column<long>(type: "bigint", nullable: false),
                    Denominator = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveFromBusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveToBusinessDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_packaging_conversions", x => x.Id);
                    table.CheckConstraint("CK_item_pack_conversion_dates", "\"EffectiveToBusinessDate\" IS NULL OR \"EffectiveToBusinessDate\" >= \"EffectiveFromBusinessDate\"");
                    table.CheckConstraint("CK_item_pack_conversion_denominator", "\"Denominator\" > 0");
                    table.CheckConstraint("CK_item_pack_conversion_numerator", "\"Numerator\" > 0");
                    table.ForeignKey(
                        name: "FK_item_packaging_conversions_items_TenantId_CatalogItemId",
                        columns: x => new { x.TenantId, x.CatalogItemId },
                        principalSchema: "catalog",
                        principalTable: "items",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_item_packaging_conversions_uoms_BaseUomId",
                        column: x => x.BaseUomId,
                        principalSchema: "catalog",
                        principalTable: "uoms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_item_packaging_conversions_uoms_PurchaseUomId",
                        column: x => x.PurchaseUomId,
                        principalSchema: "catalog",
                        principalTable: "uoms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "uoms",
                columns: new[] { "Id", "Code", "DimensionCode", "Name", "PrecisionScale", "StandardDenominator", "StandardNumerator", "Status" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "EA", "COUNT", "Each", 3, 1L, 1L, "ACTIVE" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "KG", "MASS", "Kilogram", 3, 1L, 1L, "ACTIVE" },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "G", "MASS", "Gram", 3, 1000L, 1L, "ACTIVE" },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "CASE", "PACKAGE", "Case", 3, 1L, 1L, "ACTIVE" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_item_packaging_conversions_BaseUomId",
                schema: "catalog",
                table: "item_packaging_conversions",
                column: "BaseUomId");

            migrationBuilder.CreateIndex(
                name: "IX_item_packaging_conversions_PurchaseUomId",
                schema: "catalog",
                table: "item_packaging_conversions",
                column: "PurchaseUomId");

            migrationBuilder.CreateIndex(
                name: "IX_item_packaging_conversions_TenantId_CatalogItemId_PurchaseU~",
                schema: "catalog",
                table: "item_packaging_conversions",
                columns: new[] { "TenantId", "CatalogItemId", "PurchaseUomId", "EffectiveFromBusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_items_BaseUomId",
                schema: "catalog",
                table: "items",
                column: "BaseUomId");

            migrationBuilder.CreateIndex(
                name: "IX_items_TenantId_Code",
                schema: "catalog",
                table: "items",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_uoms_Code",
                schema: "catalog",
                table: "uoms",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_packaging_conversions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "items",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "uoms",
                schema: "catalog");
        }
    }
}
