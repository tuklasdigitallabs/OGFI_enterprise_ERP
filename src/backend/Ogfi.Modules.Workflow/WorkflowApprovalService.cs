using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging;
using Ogfi.Modules.Workflow.Persistence;

namespace Ogfi.Modules.Workflow;

public sealed class WorkflowApprovalService(WorkflowDbContext dbContext)
{
    public async Task<WorkflowDefinitionVersion> CreatePurchaseOrderDefinitionVersionAsync(
        Guid tenantId,
        int version,
        string name,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        if (version <= 0 || string.IsNullOrWhiteSpace(name))
        {
            throw new WorkflowRuleException("WORKFLOW.DEFINITION.INVALID", "Workflow definition version and name are required.");
        }

        var definition = new WorkflowDefinitionVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Code = WorkflowDefinitionCodes.PurchaseOrderApproval,
            Version = version,
            Name = name.Trim(),
            CreatedAtUtc = createdAtUtc
        };

        dbContext.WorkflowDefinitionVersions.Add(definition);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new WorkflowRuleException("WORKFLOW.DEFINITION.VERSION_EXISTS", "The requested Workflow Definition Version already exists.");
        }
        return definition;
    }

    public async Task<WorkflowStartResult> StartPurchaseOrderApprovalAsync(
        PurchaseOrderApprovalStartCommand command,
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken cancellationToken)
    {
        if (command.ApprovalRound <= 0 || command.SubjectVersion <= 0)
        {
            throw new WorkflowRuleException("WORKFLOW.APPROVAL.INVALID_TRIGGER", "Approval round and subject version must be positive.");
        }

        var candidates = candidateUserIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (candidates.Length == 0)
        {
            throw new WorkflowRuleException("WORKFLOW.APPROVAL.NO_CANDIDATE", "No authorized approver candidate exists for this Purchase Order context.");
        }

        var existing = await FindExistingStartAsync(command, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var definition = await dbContext.WorkflowDefinitionVersions.AsNoTracking()
            .Where(x => x.TenantId == command.TenantId && x.Code == WorkflowDefinitionCodes.PurchaseOrderApproval)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new WorkflowRuleException("WORKFLOW.DEFINITION.NOT_FOUND", "No versioned Purchase Order approval definition is configured.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var instance = new WorkflowInstance
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            DefinitionVersionId = definition.Id,
            SubjectType = WorkflowSubjectTypes.PurchaseOrder,
            SubjectId = command.PurchaseOrderId,
            ApprovalRound = command.ApprovalRound,
            SubjectVersion = command.SubjectVersion,
            RequesterUserId = command.RequestedByUserId,
            LegalEntityId = command.LegalEntityId,
            OutletId = command.OutletId,
            BusinessDate = command.BusinessDate,
            PurchaseOrderTotal = command.PurchaseOrderTotal,
            Currency = command.Currency.ToUpperInvariant(),
            Status = WorkflowStatuses.Pending,
            CorrelationId = command.CorrelationId,
            StartedAtUtc = command.OccurredAtUtc
        };
        var task = new WorkflowTask
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            InstanceId = instance.Id,
            StepKey = WorkflowTaskKeys.PurchaseOrderApproval,
            Status = WorkflowStatuses.Pending,
            CreatedAtUtc = command.OccurredAtUtc
        };

        dbContext.WorkflowInstances.Add(instance);
        dbContext.WorkflowTasks.Add(task);
        dbContext.WorkflowTaskCandidates.AddRange(candidates.Select(userId => new WorkflowTaskCandidate
        {
            Id = Guid.NewGuid(),
            TenantId = command.TenantId,
            TaskId = task.Id,
            UserId = userId
        }));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WorkflowStartResult(instance.Id, task.Id, definition.Id, definition.Version);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var raced = await FindExistingStartAsync(command, cancellationToken);
            if (raced is not null)
            {
                return raced;
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<ApprovalInboxItem>> GetInboxAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await (
            from task in dbContext.WorkflowTasks.AsNoTracking()
            join candidate in dbContext.WorkflowTaskCandidates.AsNoTracking()
                on new { task.TenantId, TaskId = task.Id } equals new { candidate.TenantId, candidate.TaskId }
            join instance in dbContext.WorkflowInstances.AsNoTracking()
                on new { task.TenantId, InstanceId = task.InstanceId } equals new { instance.TenantId, InstanceId = instance.Id }
            where task.TenantId == tenantId
                  && candidate.UserId == userId
                  && task.Status == WorkflowStatuses.Pending
                  && instance.Status == WorkflowStatuses.Pending
            orderby task.CreatedAtUtc
            select new ApprovalInboxItem(
                task.Id,
                instance.Id,
                instance.SubjectId,
                instance.ApprovalRound,
                instance.SubjectVersion,
                instance.PurchaseOrderTotal,
                instance.Currency,
                instance.OutletId,
                instance.RequesterUserId,
                instance.BusinessDate,
                task.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<ApprovalTaskDetail?> GetTaskForCandidateAsync(
        Guid tenantId,
        Guid userId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        return await (
            from task in dbContext.WorkflowTasks.AsNoTracking()
            join candidate in dbContext.WorkflowTaskCandidates.AsNoTracking()
                on new { task.TenantId, TaskId = task.Id } equals new { candidate.TenantId, candidate.TaskId }
            join instance in dbContext.WorkflowInstances.AsNoTracking()
                on new { task.TenantId, InstanceId = task.InstanceId } equals new { instance.TenantId, InstanceId = instance.Id }
            join definition in dbContext.WorkflowDefinitionVersions.AsNoTracking()
                on new { instance.TenantId, DefinitionVersionId = instance.DefinitionVersionId }
                equals new { definition.TenantId, DefinitionVersionId = definition.Id }
            where task.TenantId == tenantId && task.Id == taskId && candidate.UserId == userId
            select new ApprovalTaskDetail(
                task.Id,
                instance.Id,
                definition.Id,
                definition.Version,
                instance.SubjectId,
                instance.ApprovalRound,
                instance.SubjectVersion,
                instance.PurchaseOrderTotal,
                instance.Currency,
                instance.LegalEntityId,
                instance.OutletId,
                instance.RequesterUserId,
                instance.BusinessDate,
                task.Status,
                task.CreatedAtUtc,
                task.CompletedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<ApprovalDecision> ApproveTaskAsync(
        Guid tenantId,
        Guid taskId,
        Guid actorUserId,
        string correlationId,
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var task = await dbContext.WorkflowTasks
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == taskId, cancellationToken)
            ?? throw new WorkflowRuleException("WORKFLOW.TASK.NOT_FOUND", "Approval task does not exist.");

        var isCandidate = await dbContext.WorkflowTaskCandidates.AnyAsync(
            x => x.TenantId == tenantId && x.TaskId == taskId && x.UserId == actorUserId,
            cancellationToken);
        if (!isCandidate)
        {
            throw new WorkflowRuleException("WORKFLOW.TASK.UNAUTHORIZED", "The authenticated user is not an approver candidate for this task.");
        }

        if (task.Status == WorkflowStatuses.Approved)
        {
            var existingDecision = await dbContext.ApprovalDecisions.AsNoTracking()
                .SingleAsync(x => x.TenantId == tenantId && x.TaskId == taskId, cancellationToken);
            if (existingDecision.ActorUserId == actorUserId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return existingDecision;
            }
            throw new WorkflowRuleException("WORKFLOW.TASK.ALREADY_DECIDED", "The approval task already has an immutable decision.");
        }
        if (task.Status != WorkflowStatuses.Pending)
        {
            throw new WorkflowRuleException("WORKFLOW.TASK.ALREADY_DECIDED", "The approval task is no longer pending.");
        }

        var instance = await dbContext.WorkflowInstances
            .SingleAsync(x => x.TenantId == tenantId && x.Id == task.InstanceId, cancellationToken);
        if (instance.Status != WorkflowStatuses.Pending)
        {
            throw new WorkflowRuleException("WORKFLOW.INSTANCE.NOT_PENDING", "The Workflow Instance is no longer pending.");
        }

        var decision = new ApprovalDecision
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            InstanceId = instance.Id,
            TaskId = task.Id,
            Decision = WorkflowStatuses.Approved,
            ActorUserId = actorUserId,
            DecidedAtUtc = decidedAtUtc
        };
        task.Status = WorkflowStatuses.Approved;
        task.CompletedAtUtc = decidedAtUtc;
        instance.Status = WorkflowStatuses.Approved;
        instance.CompletedAtUtc = decidedAtUtc;

        var eventId = Guid.NewGuid();
        var payload = new PurchaseOrderApprovalCompletedV1(
            eventId,
            tenantId,
            instance.Id,
            task.Id,
            instance.SubjectId,
            instance.ApprovalRound,
            instance.SubjectVersion,
            WorkflowStatuses.Approved,
            actorUserId,
            decidedAtUtc,
            correlationId,
            decidedAtUtc);

        dbContext.ApprovalDecisions.Add(decision);
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = eventId,
            TenantId = tenantId,
            Type = "Workflow.PurchaseOrderApprovalCompleted",
            SchemaVersion = 1,
            OccurredAtUtc = decidedAtUtc,
            CorrelationId = correlationId,
            CausationId = $"WF:{instance.Id}:TASK:{task.Id}:APPROVED",
            Payload = JsonSerializer.Serialize(payload)
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return decision;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var existingDecision = await dbContext.ApprovalDecisions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.TaskId == taskId, cancellationToken);
            if (existingDecision is not null && existingDecision.ActorUserId == actorUserId)
            {
                return existingDecision;
            }
            if (existingDecision is not null)
            {
                throw new WorkflowRuleException("WORKFLOW.TASK.ALREADY_DECIDED", "The approval task already has an immutable decision.");
            }
            throw;
        }
    }

    private async Task<WorkflowStartResult?> FindExistingStartAsync(
        PurchaseOrderApprovalStartCommand command,
        CancellationToken cancellationToken)
    {
        return await (
            from instance in dbContext.WorkflowInstances.AsNoTracking()
            join task in dbContext.WorkflowTasks.AsNoTracking()
                on new { instance.TenantId, InstanceId = instance.Id } equals new { task.TenantId, InstanceId = task.InstanceId }
            join definition in dbContext.WorkflowDefinitionVersions.AsNoTracking()
                on new { instance.TenantId, DefinitionVersionId = instance.DefinitionVersionId }
                equals new { definition.TenantId, DefinitionVersionId = definition.Id }
            where instance.TenantId == command.TenantId
                  && instance.SubjectType == WorkflowSubjectTypes.PurchaseOrder
                  && instance.SubjectId == command.PurchaseOrderId
                  && instance.ApprovalRound == command.ApprovalRound
                  && task.StepKey == WorkflowTaskKeys.PurchaseOrderApproval
            select new WorkflowStartResult(instance.Id, task.Id, definition.Id, definition.Version))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
