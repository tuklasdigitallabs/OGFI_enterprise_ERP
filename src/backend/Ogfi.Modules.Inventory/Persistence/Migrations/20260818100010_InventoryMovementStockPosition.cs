using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Inventory.Persistence.Migrations;

[DbContext(typeof(InventoryDbContext))]
[Migration("20260818100010_InventoryMovementStockPosition")]
public sealed class InventoryMovementStockPosition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE inventory.inventory_source_effects (
              "Id" uuid PRIMARY KEY,
              "TenantId" uuid NOT NULL,
              "SourceEventId" uuid NOT NULL,
              "SourceType" varchar(160) NOT NULL,
              "SourceDocumentId" uuid NOT NULL,
              "CorrelationId" varchar(64) NOT NULL,
              "OccurredAtUtc" timestamptz NOT NULL,
              "ProcessedAtUtc" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX "IX_inventory_source_effects_TenantId_SourceEventId"
              ON inventory.inventory_source_effects ("TenantId", "SourceEventId");
            CREATE INDEX "IX_inventory_source_effects_TenantId_SourceType_SourceDocumentId"
              ON inventory.inventory_source_effects ("TenantId", "SourceType", "SourceDocumentId");

            CREATE TABLE inventory.inventory_movements (
              "Id" uuid PRIMARY KEY,
              "TenantId" uuid NOT NULL,
              "MovementType" varchar(40) NOT NULL,
              "SourceEventId" uuid NOT NULL,
              "SourceDocumentId" uuid NOT NULL,
              "SourceLineId" uuid NOT NULL,
              "PurchaseOrderId" uuid NOT NULL,
              "PurchaseOrderLineId" uuid NOT NULL,
              "CatalogItemId" uuid NOT NULL,
              "CatalogItemCodeSnapshot" varchar(60) NOT NULL,
              "CatalogItemNameSnapshot" varchar(200) NOT NULL,
              "StockLocationId" uuid NOT NULL,
              "StockLocationCodeSnapshot" varchar(50) NOT NULL,
              "OutletId" uuid NOT NULL,
              "BaseUomId" uuid NOT NULL,
              "BaseUomCodeSnapshot" varchar(30) NOT NULL,
              "QuantityBaseUom" numeric(19,6) NOT NULL CHECK ("QuantityBaseUom" <> 0),
              "BusinessDate" date NOT NULL,
              "OccurredAtUtc" timestamptz NOT NULL,
              "CorrelationId" varchar(64) NOT NULL
            );
            CREATE UNIQUE INDEX "IX_inventory_movements_TenantId_SourceEventId_SourceLineId"
              ON inventory.inventory_movements ("TenantId", "SourceEventId", "SourceLineId");
            CREATE INDEX "IX_inventory_movements_TenantId_CatalogItemId_StockLocationId_OccurredAtUtc"
              ON inventory.inventory_movements ("TenantId", "CatalogItemId", "StockLocationId", "OccurredAtUtc");

            CREATE TABLE inventory.stock_positions (
              "Id" uuid PRIMARY KEY,
              "TenantId" uuid NOT NULL,
              "CatalogItemId" uuid NOT NULL,
              "CatalogItemCodeSnapshot" varchar(60) NOT NULL,
              "CatalogItemNameSnapshot" varchar(200) NOT NULL,
              "StockLocationId" uuid NOT NULL,
              "StockLocationCodeSnapshot" varchar(50) NOT NULL,
              "OutletId" uuid NOT NULL,
              "BaseUomId" uuid NOT NULL,
              "BaseUomCodeSnapshot" varchar(30) NOT NULL,
              "QuantityOnHand" numeric(19,6) NOT NULL,
              "LastMovementOccurredAtUtc" timestamptz NULL,
              "Version" bigint NOT NULL
            );
            CREATE UNIQUE INDEX "IX_stock_positions_TenantId_CatalogItemId_StockLocationId_BaseUomId"
              ON inventory.stock_positions ("TenantId", "CatalogItemId", "StockLocationId", "BaseUomId");
            CREATE INDEX "IX_stock_positions_TenantId_OutletId_CatalogItemId"
              ON inventory.stock_positions ("TenantId", "OutletId", "CatalogItemId");

            CREATE OR REPLACE FUNCTION inventory.prevent_inventory_movement_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              RAISE EXCEPTION 'Inventory Movement is append-only' USING ERRCODE = '55000';
            END;
            $$;
            CREATE TRIGGER inventory_movements_append_only
              BEFORE UPDATE OR DELETE ON inventory.inventory_movements
              FOR EACH ROW EXECUTE FUNCTION inventory.prevent_inventory_movement_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER IF EXISTS inventory_movements_append_only ON inventory.inventory_movements;
            DROP FUNCTION IF EXISTS inventory.prevent_inventory_movement_mutation();
            DROP TABLE inventory.inventory_source_effects;
            DROP TABLE inventory.inventory_movements;
            DROP TABLE inventory.stock_positions;
            """);
    }
}
