using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging;
using Ogfi.Modules.Foundation.Security;

namespace Ogfi.Modules.Foundation.Persistence;

public sealed class FoundationDbContext(DbContextOptions<FoundationDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ErpUser> Users => Set<ErpUser>();
    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();
    public DbSet<Outlet> Outlets => Set<Outlet>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();
    public DbSet<OutletScopeGrant> OutletScopeGrants => Set<OutletScopeGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("foundation");

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(200);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.CausationId).HasMaxLength(128);
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ProcessedAtUtc, x.OccurredAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.Id }).IsUnique();
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(50);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<ErpUser>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalSubject).HasMaxLength(200);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.HasIndex(x => x.ExternalSubject).IsUnique();
        });

        modelBuilder.Entity<LegalEntity>(entity =>
        {
            entity.ToTable("legal_entities");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(50);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Outlet>(entity =>
        {
            entity.ToTable("outlets");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(50);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.TimeZoneId).HasMaxLength(100);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LegalEntity>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.LegalEntityId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantMembership>(entity =>
        {
            entity.ToTable("tenant_memberships");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ErpUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PermissionGrant>(entity =>
        {
            entity.ToTable("permission_grants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PermissionCode).HasMaxLength(120);
            entity.HasIndex(x => new { x.TenantId, x.MembershipId, x.PermissionCode }).IsUnique();
            entity.HasOne<TenantMembership>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.MembershipId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutletScopeGrant>(entity =>
        {
            entity.ToTable("outlet_scope_grants");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.MembershipId, x.OutletId }).IsUnique();
            entity.HasOne<TenantMembership>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.MembershipId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Outlet>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.OutletId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
