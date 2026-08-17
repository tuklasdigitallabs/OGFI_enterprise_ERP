using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogItemConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Version",
                schema: "catalog",
                table: "items",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                schema: "catalog",
                table: "items");
        }
    }
}
