using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging;

namespace Ogfi.Modules.Procurement.Persistence;

public sealed class ProcurementDbContext(DbContextOptions<ProcurementDbContext> options) : DbContext(options)
{
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierOffer> SupplierOffers => Set<SupplierOffer>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("procurement");

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.ToTable("suppliers");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(60);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<SupplierOffer>(entity =>
        {
            entity.ToTable("supplier_offers", table =>
            {
                table.HasCheckConstraint("CK_supplier_offer_price", "\"UnitPrice\" >= 0");
                table.HasCheckConstraint("CK_supplier_offer_conversion_numerator", "\"ConversionNumerator\" > 0");
                table.HasCheckConstraint("CK_supplier_offer_conversion_denominator", "\"ConversionDenominator\" > 0");
                table.HasCheckConstraint("CK_supplier_offer_dates", "\"EffectiveToBusinessDate\" IS NULL OR \"EffectiveToBusinessDate\" >= \"EffectiveFromBusinessDate\"");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CatalogItemCodeSnapshot).HasMaxLength(60);
            entity.Property(x => x.CatalogItemNameSnapshot).HasMaxLength(200);
            entity.Property(x => x.SupplierItemCode).HasMaxLength(80);
            entity.Property(x => x.PurchaseUomCodeSnapshot).HasMaxLength(30);
            entity.Property(x => x.BaseUomCodeSnapshot).HasMaxLength(30);
            entity.Property(x => x.UnitPrice).HasPrecision(19, 4);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.HasIndex(x => new { x.TenantId, x.SupplierId, x.CatalogItemId, x.PurchaseUomId, x.EffectiveFromBusinessDate }).IsUnique();
            entity.HasOne<Supplier>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.SupplierId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrder>(entity =>
        {
            entity.ToTable("purchase_orders");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Number).HasMaxLength(60);
            entity.Property(x => x.SupplierCodeSnapshot).HasMaxLength(60);
            entity.Property(x => x.SupplierNameSnapshot).HasMaxLength(200);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.TotalNetAmount).HasPrecision(19, 4);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            entity.HasOne<Supplier>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.SupplierId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrderLine>(entity =>
        {
            entity.ToTable("purchase_order_lines", table =>
            {
                table.HasCheckConstraint("CK_purchase_order_line_quantity", "\"OrderQuantity\" > 0");
                table.HasCheckConstraint("CK_purchase_order_line_conversion_numerator", "\"ConversionNumerator\" > 0");
                table.HasCheckConstraint("CK_purchase_order_line_conversion_denominator", "\"ConversionDenominator\" > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CatalogItemCodeSnapshot).HasMaxLength(60);
            entity.Property(x => x.CatalogItemNameSnapshot).HasMaxLength(200);
            entity.Property(x => x.OrderQuantity).HasPrecision(19, 6);
            entity.Property(x => x.PurchaseUomCodeSnapshot).HasMaxLength(30);
            entity.Property(x => x.BaseUomCodeSnapshot).HasMaxLength(30);
            entity.Property(x => x.UnitPrice).HasPrecision(19, 4);
            entity.Property(x => x.LineNetAmount).HasPrecision(19, 4);
            entity.HasIndex(x => new { x.TenantId, x.PurchaseOrderId, x.LineNumber }).IsUnique();
            entity.HasOne<PurchaseOrder>().WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(200);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.CausationId).HasMaxLength(128);
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ProcessedAtUtc, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.Type, x.CausationId }).IsUnique();
        });
    }
}
