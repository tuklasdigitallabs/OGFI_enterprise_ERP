using Microsoft.EntityFrameworkCore;

namespace Ogfi.Modules.Audit.Persistence;

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Rs01TraceProjection> Rs01TraceProjections => Set<Rs01TraceProjection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("audit");

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events", table =>
            {
                table.HasCheckConstraint("CK_audit_event_revision", "\"ResourceRevision\" IS NULL OR \"ResourceRevision\" > 0");
                table.HasCheckConstraint("CK_audit_event_safe_evidence_size", "octet_length(\"SafeEvidenceJson\"::text) <= 16384");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.ActorType).HasMaxLength(20);
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.Property(x => x.SourceModule).HasMaxLength(80);
            entity.Property(x => x.ResourceType).HasMaxLength(100);
            entity.Property(x => x.Outcome).HasMaxLength(20);
            entity.Property(x => x.ErrorCode).HasMaxLength(120);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.CausationId).HasMaxLength(128);
            entity.Property(x => x.SafeEvidenceJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.TenantId, x.SourceModule, x.SourceEventId, x.Action })
                .HasFilter("\"SourceEventId\" IS NOT NULL")
                .IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.ResourceType, x.ResourceId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.CorrelationId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.PurchaseOrderId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.GoodsReceiptId, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.JournalId, x.OccurredAtUtc });
        });

        modelBuilder.Entity<Rs01TraceProjection>(entity =>
        {
            entity.ToTable("rs01_trace_projections", table =>
            {
                table.HasCheckConstraint("CK_audit_trace_event_count", "\"EvidenceEventCount\" > 0");
                table.HasCheckConstraint("CK_audit_trace_movement_count", "\"InventoryMovementCount\" >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.State).HasMaxLength(20);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.InventoryMovementIdsJson).HasColumnType("jsonb");
            entity.Property(x => x.MissingLinksJson).HasColumnType("jsonb");
            entity.Property(x => x.InvalidReason).HasMaxLength(600);
            entity.HasIndex(x => new { x.TenantId, x.PurchaseOrderId, x.GoodsReceiptId })
                .HasFilter("\"GoodsReceiptId\" IS NOT NULL")
                .IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.PurchaseOrderId })
                .HasFilter("\"GoodsReceiptId\" IS NULL")
                .IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.GoodsReceiptId });
            entity.HasIndex(x => new { x.TenantId, x.JournalId });
            entity.HasIndex(x => new { x.TenantId, x.CorrelationId });
            entity.HasIndex(x => new { x.TenantId, x.State, x.LastEventAtUtc });
        });
    }
}
