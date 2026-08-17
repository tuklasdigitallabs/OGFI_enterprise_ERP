using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

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
    }
}
