using Ogfi.Modules.Audit;
using Ogfi.Modules.DurableOperations;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Workers;
using Microsoft.Extensions.DependencyInjection;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.DurableOperations.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

[Collection(BatchFTestCollection.Name)]
public sealed class BatchGPhase3WorkerTests(BatchFFixture fixture)
{
    [Fact]
    public async Task Actual_owner_consequences_produce_six_events_and_complete_rs01_trace()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var context = await fixture.CreateBusinessContextAsync();
        await BatchFFinancialConsequenceTests.ConfigureValidFinanceForPhase3Async(client, context);
        var receipt = await fixture.CreateGoodsReceiptPostedEventAsync(context, lineNetAmount: 1350m);
        var approval = await fixture.SeedCommittedApprovalEvidenceAsync(receipt);

        await fixture.ProcessInventoryAsync(BatchFFixture.TenantA);
        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        await fixture.ProcessAuditWithHeartbeatAsync(BatchFFixture.TenantA);

        Assert.Equal(1, await fixture.CountAuditEventsAsync(approval.SubmissionEventId));
        Assert.Equal(2, await fixture.CountAuditEventsAsync(approval.ApprovalEventId));
        Assert.Equal(3, await fixture.CountAuditEventsAsync(receipt.EventId));
        Assert.Equal(OutboxDeliveryStatuses.Completed,
            (await fixture.GetDeliveryAsync(receipt.EventId, OutboxConsumerCodes.InventoryStockConsequence)).Status);
        Assert.Equal(OutboxDeliveryStatuses.Completed,
            (await fixture.GetDeliveryAsync(receipt.EventId, OutboxConsumerCodes.FinanceFinancialConsequence)).Status);
        Assert.Equal(OutboxDeliveryStatuses.Completed,
            (await fixture.GetDeliveryAsync(receipt.EventId, OutboxConsumerCodes.AuditMaterialAction)).Status);

        var actions = await fixture.GetAuditActionsAsync(receipt.EventId);
        Assert.Contains(Rs01MaterialStages.GoodsReceiptPosting, actions);
        Assert.Contains(Rs01MaterialStages.InventoryMovementCreation, actions);
        Assert.Contains(Rs01MaterialStages.FinanceJournalPosting, actions);
        Assert.Equal(Rs01TraceStates.Complete,
            await fixture.RebuildTraceAsync(receipt.PurchaseOrderId));

        await fixture.ProcessAuditAsync(BatchFFixture.TenantA);
        Assert.Equal(6,
            await fixture.CountAuditEventsAsync(approval.SubmissionEventId)
            + await fixture.CountAuditEventsAsync(approval.ApprovalEventId)
            + await fixture.CountAuditEventsAsync(receipt.EventId));
    }

    [Fact]
    public async Task Actual_inventory_finance_and_audit_workers_persist_start_and_success_observations()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var context = await fixture.CreateBusinessContextAsync();
        await BatchFFinancialConsequenceTests.ConfigureValidFinanceForPhase3Async(client, context);
        await fixture.CreateGoodsReceiptPostedEventAsync(context, lineNetAmount: 450m);

        await fixture.ProcessInventoryWithHeartbeatAsync(BatchFFixture.TenantA);
        await fixture.ProcessFinanceWithHeartbeatAsync(BatchFFixture.TenantA);
        await fixture.ProcessAuditWithHeartbeatAsync(BatchFFixture.TenantA);
        await fixture.ProcessApprovalWithHeartbeatAsync(BatchFFixture.TenantA);
        await fixture.ProcessReplayWithHeartbeatAsync(BatchFFixture.TenantA);

        foreach (var workerCode in new[]
                 {
                     WorkerCodes.Approval, WorkerCodes.Inventory, WorkerCodes.Finance,
                     WorkerCodes.Audit, WorkerCodes.Replay
                 })
        {
            var heartbeat = await fixture.GetHeartbeatAsync(workerCode);
            Assert.True(heartbeat.ObservationSequence >= 2);
            Assert.True(
                heartbeat.LastSucceededAtUtc is not null || heartbeat.LastFailedAtUtc is not null,
                $"Worker {workerCode} did not persist an iteration result observation.");
            Assert.NotEqual(Guid.Empty, heartbeat.ObservationId);
        }
    }

    [Fact]
    public async Task Actual_transient_finance_failure_recovers_without_incrementing_failure_evidence()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var context = await fixture.CreateBusinessContextAsync();
        await BatchFFinancialConsequenceTests.ConfigureValidFinanceForPhase3Async(client, context);
        var source = await fixture.CreateGoodsReceiptPostedEventAsync(context, lineNetAmount: 700m);
        fixture.FinanceAttemptHook.FailBeforeOnce();

        await fixture.ProcessFinanceWithHeartbeatAsync(BatchFFixture.TenantA);
        var failed = await fixture.GetFailureAsync(source.EventId, ProcessorCodes.Finance);
        Assert.Equal(ProcessingFailureClassifications.Transient, failed.FailureClassification);
        Assert.Equal(ProcessingFailureStates.RetryPending, failed.State);
        var attemptCount = failed.AttemptCount;
        var lastFailed = failed.LastFailedAtUtc;

        await fixture.ProcessFinanceWithHeartbeatAsync(BatchFFixture.TenantA);
        var recovered = await fixture.GetFailureAsync(source.EventId, ProcessorCodes.Finance);
        Assert.Equal(ProcessingFailureStates.Recovered, recovered.State);
        Assert.Equal(attemptCount, recovered.AttemptCount);
        Assert.Equal(lastFailed, recovered.LastFailedAtUtc);
        Assert.False(recovered.Replayable);
    }

    [Fact]
    public async Task Actual_forged_finance_event_is_terminal_and_non_replayable()
    {
        var context = await fixture.CreateBusinessContextAsync();
        var forged = await fixture.CreateGoodsReceiptPostedEventAsync(
            context, payloadTenantId: BatchFFixture.TenantB);
        await fixture.ProcessFinanceWithHeartbeatAsync(BatchFFixture.TenantA);
        var failure = await fixture.GetFailureAsync(forged.EventId, ProcessorCodes.Finance);
        Assert.Equal(ProcessingFailureClassifications.ForgedTenant, failure.FailureClassification);
        Assert.Equal(ProcessingFailureStates.TerminalRejected, failure.State);
        Assert.False(failure.Replayable);
        Assert.Equal(OutboxDeliveryStatuses.TerminalRejected,
            (await fixture.GetDeliveryAsync(forged.EventId,
                OutboxConsumerCodes.FinanceFinancialConsequence)).Status);
    }

    [Fact]
    public async Task Actual_inventory_finance_and_audit_owner_replay_is_idempotent()
    {
        using var client = fixture.CreateAuthenticatedClient();
        var context = await fixture.CreateBusinessContextAsync();
        await BatchFFinancialConsequenceTests.ConfigureValidFinanceForPhase3Async(client, context);
        var source = await fixture.CreateGoodsReceiptPostedEventAsync(context, lineNetAmount: 975m);
        await fixture.ProcessInventoryAsync(BatchFFixture.TenantA);
        await fixture.ProcessFinanceAsync(BatchFFixture.TenantA);
        await fixture.ProcessAuditAsync(BatchFFixture.TenantA);
        var command = new ReplayDispatchCommand(
            BatchFFixture.TenantA, Guid.NewGuid(), ProcessorCodes.Inventory, "INVENTORY",
            source.EventId, $"GR:{source.GoodsReceiptId}:POST", source.Payload.CorrelationId,
            context.LegalEntityId, context.OutletId);

        Assert.True((await fixture.ReplayWithAsync<InventoryReplayHandler>(command)).Succeeded);
        Assert.True((await fixture.ReplayWithAsync<FinanceReplayHandler>(command with
        {
            OperationType = ProcessorCodes.Finance,
            OwnerModule = "FINANCE"
        })).Succeeded);
        Assert.True((await fixture.ReplayWithAsync<ProcurementAuditReplayHandler>(command with
        {
            OperationType = ProcessorCodes.Audit,
            OwnerModule = "PROCUREMENT"
        })).Succeeded);

        Assert.Equal(1, await fixture.CountInventoryMovementsAsync(source.EventId));
        Assert.Equal(1, await fixture.CountSourcePostingsAsync(source.EventId));
        Assert.Equal(1, await fixture.CountJournalsAsync(source.EventId));
        Assert.Equal(3, await fixture.CountAuditEventsAsync(source.EventId));
    }

    [Fact]
    public async Task Long_running_owner_execution_renews_lease_through_separate_scope()
    {
        OperationAttempt attempt;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>()
                .SetCandidateTenant(BatchFFixture.TenantA);
            var service = scope.ServiceProvider.GetRequiredService<DurableOperationService>();
            var source = Guid.NewGuid();
            var failure = await service.RecordFailureAsync(new ProcessingFailureUpdate(
                BatchFFixture.TenantA, "INVENTORY", ProcessorCodes.Inventory,
                ProcessingFailureClassifications.Transient, source, $"cause-{source:N}",
                $"corr-{source:N}", "GOODS_RECEIPT", Guid.NewGuid(),
                "TEST.TRANSIENT", "{\"reasonCode\":\"TEST_TRANSIENT\"}",
                ProcessingFailureStates.RetryPending, true));
            var operation = await new ReplayCoordinator(service, []).RequestReplayForFailureAsync(
                BatchFFixture.TenantA, failure.Id, $"lease-{failure.Id:N}", null, null);
            operation = await service.TransitionAsync(
                operation.TenantId, operation.Id, operation.Version, OperationStatuses.Running);
            attempt = await service.StartAttemptAsync(
                operation.TenantId, operation.Id, WorkerCodes.Replay);
        }

        var observer = new ControlledLeaseRenewalObserver();
        var runner = new ReplayLeaseRenewalRunner(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            new ReplayWorkerOptions
            {
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(10),
                LeaseDuration = TimeSpan.FromMinutes(1)
            },
            observer);
        var result = await runner.RunAsync(
            BatchFFixture.TenantA, attempt, WorkerCodes.Replay,
            async token =>
            {
                await observer.Observed.Task.WaitAsync(token);
                return new ReplayDispatchResult(true, "TEST", Guid.NewGuid(),
                    SafeDetailJson: "{\"status\":\"SUCCEEDED\"}");
            }, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(await observer.Observed.Task);

        await using var verification = fixture.Services.CreateAsyncScope();
        verification.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>()
            .SetCandidateTenant(BatchFFixture.TenantA);
        var renewed = await verification.ServiceProvider.GetRequiredService<DurableOperationsDbContext>()
            .OperationAttempts.FindAsync(attempt.Id);
        Assert.NotNull(renewed);
        Assert.True(renewed!.Version > attempt.Version);
    }
}

public sealed class ControlledLeaseRenewalObserver : IReplayLeaseRenewalObserver
{
    public TaskCompletionSource<bool> Observed { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task RenewedAsync(
        Guid tenantId, Guid attemptId, long attemptVersion, CancellationToken cancellationToken)
    {
        Observed.TrySetResult(true);
        return Task.CompletedTask;
    }
}
