using Ogfi.Api.Infrastructure;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Workflow;

namespace Ogfi.Api.Endpoints;

public static class WorkflowEndpoints
{
    private const string DefinitionManagePermission = "workflow.definition.manage";

    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/workflow/definitions/purchase-order/versions", CreateDefinitionVersionAsync)
            .RequireAuthorization()
            .Produces<WorkflowDefinitionVersionResponse>(StatusCodes.Status201Created);
        endpoints.MapGet("/api/workflow/approval-inbox", ListApprovalInboxAsync)
            .RequireAuthorization()
            .Produces<IReadOnlyList<ApprovalInboxItem>>();
        endpoints.MapGet("/api/workflow/tasks/{taskId:guid}", GetApprovalTaskAsync)
            .RequireAuthorization()
            .Produces<ApprovalTaskDetail>()
            .Produces(StatusCodes.Status404NotFound);
        endpoints.MapPost("/api/workflow/tasks/{taskId:guid}/approve", ApproveTaskAsync)
            .RequireAuthorization()
            .Produces<ApprovalDecisionResponse>();
        return endpoints;
    }

    private static async Task<IResult> CreateDefinitionVersionAsync(
        CreateWorkflowDefinitionVersionRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        WorkflowApprovalService workflow,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
        {
            return EndpointSupport.Problem(httpContext, StatusCodes.Status401Unauthorized, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        }
        if (!await authorization.HasPermissionAsync(DefinitionManagePermission, cancellationToken))
        {
            return EndpointSupport.Problem(httpContext, StatusCodes.Status403Forbidden, "AUTH.PERMISSION_DENIED", "Workflow definition management is not permitted.");
        }

        try
        {
            var definition = await workflow.CreatePurchaseOrderDefinitionVersionAsync(
                tenantId,
                request.Version,
                request.Name,
                timeProvider.GetUtcNow(),
                cancellationToken);
            return Results.Created(
                $"/api/workflow/definitions/purchase-order/versions/{definition.Version}",
                new WorkflowDefinitionVersionResponse(definition.Id, definition.Code, definition.Version, definition.Name, definition.CreatedAtUtc));
        }
        catch (WorkflowRuleException ex)
        {
            var status = ex.Code == "WORKFLOW.DEFINITION.VERSION_EXISTS" ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity;
            return EndpointSupport.Problem(httpContext, status, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> ListApprovalInboxAsync(
        int? offset,
        int? limit,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        WorkflowApprovalService workflow,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId))
        {
            return EndpointSupport.Problem(httpContext, StatusCodes.Status401Unauthorized, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        }
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.PurchaseOrderApprove, cancellationToken))
        {
            return EndpointSupport.Problem(httpContext, StatusCodes.Status403Forbidden, "AUTH.PERMISSION_DENIED", "Purchase Order approval is not permitted.");
        }

        var allowedOutlets = (await authorization.GetOutletScopeIdsAsync(cancellationToken)).ToHashSet();
        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var inbox = await workflow.GetInboxAsync(tenantId, userId, cancellationToken);
        var visible = inbox
            .Where(x => allowedOutlets.Contains(x.OutletId))
            .Skip(page.Offset)
            .Take(page.Limit)
            .ToArray();
        return Results.Ok(visible);
    }

    private static async Task<IResult> GetApprovalTaskAsync(
        Guid taskId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        WorkflowApprovalService workflow,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId))
        {
            return EndpointSupport.Problem(httpContext, StatusCodes.Status401Unauthorized, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        }
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.PurchaseOrderApprove, cancellationToken))
        {
            return EndpointSupport.Problem(httpContext, StatusCodes.Status403Forbidden, "AUTH.PERMISSION_DENIED", "Purchase Order approval is not permitted.");
        }

        var detail = await workflow.GetTaskForCandidateAsync(tenantId, userId, taskId, cancellationToken);
        if (detail is null || !await authorization.HasOutletScopeAsync(detail.OutletId, cancellationToken))
        {
            return Results.NotFound();
        }
        return Results.Ok(detail);
    }

    private static async Task<IResult> ApproveTaskAsync(
        Guid taskId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        WorkflowApprovalService workflow,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId))
        {
            return EndpointSupport.Problem(httpContext, StatusCodes.Status401Unauthorized, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        }
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.PurchaseOrderApprove, cancellationToken))
        {
            return EndpointSupport.Problem(httpContext, StatusCodes.Status403Forbidden, "AUTH.PERMISSION_DENIED", "Purchase Order approval is not permitted.");
        }

        var detail = await workflow.GetTaskForCandidateAsync(tenantId, userId, taskId, cancellationToken);
        if (detail is null || !await authorization.HasOutletScopeAsync(detail.OutletId, cancellationToken))
        {
            return Results.NotFound();
        }

        try
        {
            var decision = await workflow.ApproveTaskAsync(
                tenantId,
                taskId,
                userId,
                EndpointSupport.CorrelationId(httpContext),
                timeProvider.GetUtcNow(),
                cancellationToken);
            return Results.Ok(new ApprovalDecisionResponse(
                decision.Id,
                decision.TaskId,
                decision.InstanceId,
                decision.Decision,
                decision.ActorUserId,
                decision.DecidedAtUtc));
        }
        catch (WorkflowRuleException ex)
        {
            var status = ex.Code switch
            {
                "WORKFLOW.TASK.NOT_FOUND" => StatusCodes.Status404NotFound,
                "WORKFLOW.TASK.UNAUTHORIZED" => StatusCodes.Status403Forbidden,
                "WORKFLOW.TASK.ALREADY_DECIDED" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status422UnprocessableEntity
            };
            return EndpointSupport.Problem(httpContext, status, ex.Code, ex.Message);
        }
    }
}

public sealed record CreateWorkflowDefinitionVersionRequest(int Version, string Name);
public sealed record WorkflowDefinitionVersionResponse(Guid Id, string Code, int Version, string Name, DateTimeOffset CreatedAtUtc);
public sealed record ApprovalDecisionResponse(Guid Id, Guid TaskId, Guid InstanceId, string Decision, Guid ActorUserId, DateTimeOffset DecidedAtUtc);
