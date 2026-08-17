using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "inventory_profiles",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseUomId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsStocked = table.Column<bool>(type: "boolean", nullable: false),
                    NegativeStockAllowed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_profiles", x => x.Id);
                    table.UniqueConstraint("AK_inventory_profiles_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "stock_locations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutletId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    LocationType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_locations", x => x.Id);
                    table.UniqueConstraint("AK_stock_locations_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_profiles_TenantId_CatalogItemId",
                schema: "inventory",
                table: "inventory_profiles",
                columns: new[] { "TenantId", "CatalogItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_locations_TenantId_OutletId_Code",
                schema: "inventory",
                table: "stock_locations",
                columns: new[] { "TenantId", "OutletId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_profiles",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_locations",
                schema: "inventory");
        }
    }
}
