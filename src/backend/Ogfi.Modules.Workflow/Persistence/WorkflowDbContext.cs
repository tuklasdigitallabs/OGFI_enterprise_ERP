using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging;

namespace Ogfi.Modules.Workflow.Persistence;

public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowDefinitionVersion> WorkflowDefinitionVersions => Set<WorkflowDefinitionVersion>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();
    public DbSet<WorkflowTaskCandidate> WorkflowTaskCandidates => Set<WorkflowTaskCandidate>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("workflow");

        modelBuilder.Entity<WorkflowDefinitionVersion>(entity =>
        {
            entity.ToTable("workflow_definition_versions", table =>
                table.HasCheckConstraint("CK_workflow_definition_version", "\"Version\" > 0"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(100);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.HasIndex(x => new { x.TenantId, x.Code, x.Version }).IsUnique();
        });

        modelBuilder.Entity<WorkflowInstance>(entity =>
        {
            entity.ToTable("workflow_instances", table =>
            {
                table.HasCheckConstraint("CK_workflow_instance_round", "\"ApprovalRound\" > 0");
                table.HasCheckConstraint("CK_workflow_instance_subject_version", "\"SubjectVersion\" > 0");
                table.HasCheckConstraint("CK_workflow_instance_total", "\"PurchaseOrderTotal\" >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.SubjectType).HasMaxLength(60);
            entity.Property(x => x.Currency).HasMaxLength(3);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.PurchaseOrderTotal).HasPrecision(19, 4);
            entity.HasIndex(x => new { x.TenantId, x.SubjectType, x.SubjectId, x.ApprovalRound }).IsUnique();
            entity.HasOne<WorkflowDefinitionVersion>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.DefinitionVersionId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowTask>(entity =>
        {
            entity.ToTable("workflow_tasks");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.StepKey).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.InstanceId, x.StepKey }).IsUnique();
            entity.HasOne<WorkflowInstance>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.InstanceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowTaskCandidate>(entity =>
        {
            entity.ToTable("workflow_task_candidates");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.TaskId, x.UserId }).IsUnique();
            entity.HasOne<WorkflowTask>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.TaskId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApprovalDecision>(entity =>
        {
            entity.ToTable("approval_decisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Decision).HasMaxLength(20);
            entity.HasIndex(x => new { x.TenantId, x.TaskId }).IsUnique();
            entity.HasOne<WorkflowTask>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.TaskId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowInstance>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.InstanceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
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
