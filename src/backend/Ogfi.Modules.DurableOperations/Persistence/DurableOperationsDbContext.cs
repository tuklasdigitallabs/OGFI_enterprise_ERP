using Microsoft.EntityFrameworkCore;

namespace Ogfi.Modules.DurableOperations.Persistence;

public sealed class DurableOperationsDbContext(DbContextOptions<DurableOperationsDbContext> options) : DbContext(options)
{
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<OperationAttempt> OperationAttempts => Set<OperationAttempt>();
    public DbSet<OperationCheckpoint> OperationCheckpoints => Set<OperationCheckpoint>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<ProcessingFailureProjection> ProcessingFailures => Set<ProcessingFailureProjection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("operations");

        modelBuilder.Entity<Operation>(entity =>
        {
            entity.ToTable("operations", table =>
            {
                table.HasCheckConstraint("CK_operations_status", "\"Status\" IN ('QUEUED','RUNNING','SUCCEEDED','FAILED','CANCEL_REQUESTED','CANCELLED')");
                table.HasCheckConstraint("CK_operations_version", "\"Version\" > 0");
                table.HasCheckConstraint("CK_operations_safe_detail_size", "\"SafeDetailJson\" IS NULL OR octet_length(\"SafeDetailJson\"::text) <= 8192");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.ReplayRequestKey).HasMaxLength(128);
            entity.Property(x => x.OperationType).HasMaxLength(100);
            entity.Property(x => x.OwnerModule).HasMaxLength(60);
            entity.Property(x => x.Status).HasMaxLength(24);
            entity.Property(x => x.OriginalCausationId).HasMaxLength(128);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.ResultReferenceType).HasMaxLength(100);
            entity.Property(x => x.SafeErrorCode).HasMaxLength(120);
            entity.Property(x => x.SafeDetailJson).HasColumnType("jsonb");
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.ReplayRequestKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.OriginalSourceEventId });
            entity.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.TenantId, x.CorrelationId });
        });

        modelBuilder.Entity<OperationAttempt>(entity =>
        {
            entity.ToTable("operation_attempts", table =>
            {
                table.HasCheckConstraint("CK_operation_attempt_number", "\"AttemptNumber\" > 0");
                table.HasCheckConstraint("CK_operation_attempt_status", "\"Status\" IN ('RUNNING','SUCCEEDED','FAILED','ABANDONED')");
                table.HasCheckConstraint("CK_operation_attempt_version", "\"Version\" > 0");
                table.HasCheckConstraint("CK_operation_attempt_lease", "\"LeaseExpiresAtUtc\" >= \"LeaseAcquiredAtUtc\" AND \"LastLeaseHeartbeatAtUtc\" >= \"LeaseAcquiredAtUtc\"");
                table.HasCheckConstraint("CK_operation_attempt_safe_detail_size", "octet_length(\"SafeDetailJson\"::text) <= 8192");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.WorkerCode).HasMaxLength(100);
            entity.Property(x => x.LeaseOwner).HasMaxLength(100);
            entity.Property(x => x.SafeErrorCode).HasMaxLength(120);
            entity.Property(x => x.SafeDetailJson).HasColumnType("jsonb");
            entity.Property(x => x.OriginalCausationId).HasMaxLength(128);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.OperationId, x.AttemptNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.OperationId })
                .HasFilter("\"Status\" = 'RUNNING'")
                .IsUnique();
            entity.HasOne<Operation>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.OperationId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OperationCheckpoint>(entity =>
        {
            entity.ToTable("operation_checkpoints", table =>
            {
                table.HasCheckConstraint("CK_operation_checkpoint_sequence", "\"Sequence\" > 0");
                table.HasCheckConstraint("CK_operation_checkpoint_progress", "\"ProgressPercentage\" BETWEEN 0 AND 100");
                table.HasCheckConstraint("CK_operation_checkpoint_safe_detail_size", "octet_length(\"SafeDetailJson\"::text) <= 8192");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.CheckpointKey).HasMaxLength(100);
            entity.Property(x => x.SafeDetailJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.TenantId, x.OperationId, x.Sequence }).IsUnique();
            entity.HasOne<Operation>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.OperationId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkerHeartbeat>(entity =>
        {
            entity.ToTable("worker_heartbeats", table =>
            {
                table.HasCheckConstraint("CK_worker_heartbeat_counts", "\"PendingCount\" >= 0 AND \"RetryPendingCount\" >= 0 AND \"TerminalFailureCount\" >= 0");
                table.HasCheckConstraint("CK_worker_heartbeat_observation_sequence", "\"ObservationSequence\" > 0");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.WorkerCode).HasMaxLength(100);
            entity.Property(x => x.LastSafeErrorCode).HasMaxLength(120);
            entity.HasIndex(x => new { x.TenantId, x.WorkerCode }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.UpdatedAtUtc });
        });

        modelBuilder.Entity<ProcessingFailureProjection>(entity =>
        {
            entity.ToTable("processing_failure_projections", table =>
            {
                table.HasCheckConstraint("CK_processing_failure_attempts", "\"AttemptCount\" > 0");
                table.HasCheckConstraint("CK_processing_failure_state", "\"State\" IN ('PENDING','RETRY_PENDING','BUSINESS_FAILED','TERMINAL_REJECTED','STALLED','RECOVERED','COMPLETED')");
                table.HasCheckConstraint("CK_processing_failure_classification", "\"FailureClassification\" IN ('TRANSIENT','BUSINESS','FORGED_TENANT','MALFORMED_CONTRACT','AUTHORIZATION','SECURITY_TERMINAL')");
                table.HasCheckConstraint("CK_processing_failure_version", "\"Version\" > 0");
                table.HasCheckConstraint("CK_processing_failure_safe_detail_size", "octet_length(\"SafeDetailJson\"::text) <= 8192");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.OwnerModule).HasMaxLength(60);
            entity.Property(x => x.ProcessorCode).HasMaxLength(100);
            entity.Property(x => x.FailureClassification).HasMaxLength(40);
            entity.Property(x => x.OriginalCausationId).HasMaxLength(128);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.ResourceType).HasMaxLength(100);
            entity.Property(x => x.SafeErrorCode).HasMaxLength(120);
            entity.Property(x => x.SafeDetailJson).HasColumnType("jsonb");
            entity.Property(x => x.State).HasMaxLength(24);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.OwnerModule, x.ProcessorCode, x.OriginalSourceEventId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.State, x.LastFailedAtUtc });
            entity.HasOne<Operation>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CurrentOperationId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.RecoveryOperationId });
            entity.HasOne<Operation>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.RecoveryOperationId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
