using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Ogfi.Modules.Foundation.Security;

#nullable disable

namespace Ogfi.Modules.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
partial class FoundationDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("foundation");

        modelBuilder.Entity("Ogfi.BuildingBlocks.Messaging.OutboxMessage", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<int>("AttemptCount").HasColumnType("integer");
            b.Property<string>("CausationId").HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<string>("CorrelationId").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("LastError").HasColumnType("text");
            b.Property<DateTimeOffset>("OccurredAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("Payload").IsRequired().HasColumnType("jsonb");
            b.Property<DateTimeOffset?>("ProcessedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("SchemaVersion").HasColumnType("integer");
            b.Property<Guid>("TenantId").HasColumnType("uuid");
            b.Property<string>("Type").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.HasKey("Id");
            b.HasIndex("ProcessedAtUtc", "OccurredAtUtc");
            b.HasIndex("TenantId", "Id").IsUnique();
            b.ToTable("outbox_messages", "foundation");
        });

        modelBuilder.Entity<Tenant>(b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.HasKey("Id");
            b.HasIndex("Code").IsUnique();
            b.ToTable("tenants", "foundation");
        });

        modelBuilder.Entity<ErpUser>(b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<string>("ExternalSubject").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("DisplayName").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.HasKey("Id");
            b.HasIndex("ExternalSubject").IsUnique();
            b.ToTable("users", "foundation");
        });

        modelBuilder.Entity<LegalEntity>(b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<Guid>("TenantId").HasColumnType("uuid");
            b.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.HasKey("Id");
            b.HasAlternateKey("TenantId", "Id");
            b.HasIndex("TenantId", "Code").IsUnique();
            b.ToTable("legal_entities", "foundation");
        });

        modelBuilder.Entity<Outlet>(b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<Guid>("TenantId").HasColumnType("uuid");
            b.Property<Guid>("LegalEntityId").HasColumnType("uuid");
            b.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("TimeZoneId").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<int>("BusinessDayStartMinutes").HasColumnType("integer");
            b.HasKey("Id");
            b.HasAlternateKey("TenantId", "Id");
            b.HasIndex("TenantId", "Code").IsUnique();
            b.ToTable("outlets", "foundation");
        });

        modelBuilder.Entity<TenantMembership>(b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<Guid>("TenantId").HasColumnType("uuid");
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.Property<string>("Status").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)");
            b.HasKey("Id");
            b.HasAlternateKey("TenantId", "Id");
            b.HasIndex("TenantId", "UserId").IsUnique();
            b.ToTable("tenant_memberships", "foundation");
        });

        modelBuilder.Entity<PermissionGrant>(b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<Guid>("TenantId").HasColumnType("uuid");
            b.Property<Guid>("MembershipId").HasColumnType("uuid");
            b.Property<string>("PermissionCode").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)");
            b.HasKey("Id");
            b.HasIndex("TenantId", "MembershipId", "PermissionCode").IsUnique();
            b.ToTable("permission_grants", "foundation");
        });

        modelBuilder.Entity<OutletScopeGrant>(b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid");
            b.Property<Guid>("TenantId").HasColumnType("uuid");
            b.Property<Guid>("MembershipId").HasColumnType("uuid");
            b.Property<Guid>("OutletId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("TenantId", "MembershipId", "OutletId").IsUnique();
            b.ToTable("outlet_scope_grants", "foundation");
        });

        modelBuilder.Entity<LegalEntity>()
            .HasOne<Tenant>().WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        modelBuilder.Entity<Outlet>()
            .HasOne<Tenant>().WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        modelBuilder.Entity<Outlet>()
            .HasOne<LegalEntity>().WithMany().HasForeignKey("TenantId", "LegalEntityId").HasPrincipalKey("TenantId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        modelBuilder.Entity<TenantMembership>()
            .HasOne<Tenant>().WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        modelBuilder.Entity<TenantMembership>()
            .HasOne<ErpUser>().WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        modelBuilder.Entity<PermissionGrant>()
            .HasOne<TenantMembership>().WithMany().HasForeignKey("TenantId", "MembershipId").HasPrincipalKey("TenantId", "Id").OnDelete(DeleteBehavior.Cascade).IsRequired();
        modelBuilder.Entity<OutletScopeGrant>()
            .HasOne<TenantMembership>().WithMany().HasForeignKey("TenantId", "MembershipId").HasPrincipalKey("TenantId", "Id").OnDelete(DeleteBehavior.Cascade).IsRequired();
        modelBuilder.Entity<OutletScopeGrant>()
            .HasOne<Outlet>().WithMany().HasForeignKey("TenantId", "OutletId").HasPrincipalKey("TenantId", "Id").OnDelete(DeleteBehavior.Cascade).IsRequired();
    }
}
