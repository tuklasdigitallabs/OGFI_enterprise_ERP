using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Procurement.Persistence.Migrations;

[DbContext(typeof(ProcurementDbContext))]
[Migration("20260818100000_GoodsReceiptStockConsequence")]
public sealed class GoodsReceiptStockConsequence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE procurement.purchase_order_lines
              ADD COLUMN "ReceivedQuantity" numeric(19,6) NOT NULL DEFAULT 0,
              ADD CONSTRAINT "AK_purchase_order_lines_TenantId_Id" UNIQUE ("TenantId", "Id"),
              ADD CONSTRAINT "CK_purchase_order_line_received_quantity"
                CHECK ("ReceivedQuantity" >= 0 AND "ReceivedQuantity" <= "OrderQuantity");

            CREATE TABLE procurement.goods_receipts (
              "Id" uuid PRIMARY KEY,
              "TenantId" uuid NOT NULL,
              "Number" varchar(60) NOT NULL,
              "PurchaseOrderId" uuid NOT NULL,
              "PurchaseOrderNumberSnapshot" varchar(60) NOT NULL,
              "SupplierId" uuid NOT NULL,
              "SupplierCodeSnapshot" varchar(60) NOT NULL,
              "SupplierNameSnapshot" varchar(200) NOT NULL,
              "LegalEntityId" uuid NOT NULL,
              "OutletId" uuid NOT NULL,
              "StockLocationId" uuid NOT NULL,
              "StockLocationCodeSnapshot" varchar(50) NOT NULL,
              "Currency" varchar(3) NOT NULL,
              "BusinessDate" date NOT NULL,
              "Status" varchar(20) NOT NULL,
              "TotalNetAmount" numeric(19,4) NOT NULL,
              "Version" bigint NOT NULL,
              "CreatedByUserId" uuid NOT NULL,
              "CreatedAtUtc" timestamptz NOT NULL,
              "PostedByUserId" uuid NULL,
              "PostedAtUtc" timestamptz NULL,
              CONSTRAINT "AK_goods_receipts_TenantId_Id" UNIQUE ("TenantId", "Id"),
              CONSTRAINT "FK_goods_receipts_purchase_orders_TenantId_PurchaseOrderId"
                FOREIGN KEY ("TenantId", "PurchaseOrderId")
                REFERENCES procurement.purchase_orders ("TenantId", "Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_goods_receipts_TenantId_Number"
              ON procurement.goods_receipts ("TenantId", "Number");
            CREATE INDEX "IX_goods_receipts_TenantId_PurchaseOrderId_CreatedAtUtc"
              ON procurement.goods_receipts ("TenantId", "PurchaseOrderId", "CreatedAtUtc");

            CREATE TABLE procurement.goods_receipt_lines (
              "Id" uuid PRIMARY KEY,
              "TenantId" uuid NOT NULL,
              "GoodsReceiptId" uuid NOT NULL,
              "LineNumber" integer NOT NULL,
              "PurchaseOrderLineId" uuid NOT NULL,
              "CatalogItemId" uuid NOT NULL,
              "CatalogItemCodeSnapshot" varchar(60) NOT NULL,
              "CatalogItemNameSnapshot" varchar(200) NOT NULL,
              "ReceivedQuantity" numeric(19,6) NOT NULL CHECK ("ReceivedQuantity" > 0),
              "PurchaseUomId" uuid NOT NULL,
              "PurchaseUomCodeSnapshot" varchar(30) NOT NULL,
              "BaseUomId" uuid NOT NULL,
              "BaseUomCodeSnapshot" varchar(30) NOT NULL,
              "ConversionNumerator" bigint NOT NULL CHECK ("ConversionNumerator" > 0),
              "ConversionDenominator" bigint NOT NULL CHECK ("ConversionDenominator" > 0),
              "NormalizedBaseQuantity" numeric(19,6) NOT NULL CHECK ("NormalizedBaseQuantity" > 0),
              "UnitPrice" numeric(19,4) NOT NULL,
              "LineNetAmount" numeric(19,4) NOT NULL,
              CONSTRAINT "FK_goods_receipt_lines_goods_receipts_TenantId_GoodsReceiptId"
                FOREIGN KEY ("TenantId", "GoodsReceiptId")
                REFERENCES procurement.goods_receipts ("TenantId", "Id") ON DELETE CASCADE,
              CONSTRAINT "FK_goods_receipt_lines_purchase_order_lines_TenantId_PurchaseOrderLineId"
                FOREIGN KEY ("TenantId", "PurchaseOrderLineId")
                REFERENCES procurement.purchase_order_lines ("TenantId", "Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_goods_receipt_lines_TenantId_GoodsReceiptId_LineNumber"
              ON procurement.goods_receipt_lines ("TenantId", "GoodsReceiptId", "LineNumber");
            CREATE INDEX "IX_goods_receipt_lines_TenantId_PurchaseOrderLineId"
              ON procurement.goods_receipt_lines ("TenantId", "PurchaseOrderLineId");

            CREATE TABLE procurement.goods_receipt_posting_commands (
              "Id" uuid PRIMARY KEY,
              "TenantId" uuid NOT NULL,
              "IdempotencyKey" varchar(128) NOT NULL,
              "RequestHash" varchar(64) NOT NULL,
              "GoodsReceiptId" uuid NOT NULL,
              "ResultVersion" bigint NOT NULL,
              "CreatedAtUtc" timestamptz NOT NULL,
              CONSTRAINT "FK_goods_receipt_posting_commands_goods_receipts_TenantId_GoodsReceiptId"
                FOREIGN KEY ("TenantId", "GoodsReceiptId")
                REFERENCES procurement.goods_receipts ("TenantId", "Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_goods_receipt_posting_commands_TenantId_IdempotencyKey"
              ON procurement.goods_receipt_posting_commands ("TenantId", "IdempotencyKey");
            CREATE UNIQUE INDEX "IX_goods_receipt_posting_commands_TenantId_GoodsReceiptId"
              ON procurement.goods_receipt_posting_commands ("TenantId", "GoodsReceiptId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE procurement.goods_receipt_lines;
            DROP TABLE procurement.goods_receipt_posting_commands;
            DROP TABLE procurement.goods_receipts;
            ALTER TABLE procurement.purchase_order_lines
              DROP CONSTRAINT "CK_purchase_order_line_received_quantity",
              DROP CONSTRAINT "AK_purchase_order_lines_TenantId_Id",
              DROP COLUMN "ReceivedQuantity";
            """);
    }
}
