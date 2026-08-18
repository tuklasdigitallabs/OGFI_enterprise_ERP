using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.DurableOperations;
using Ogfi.Workers;
using Xunit;

namespace Ogfi.IntegrationTests;

[Collection(BatchGOperationsTestCollection.Name)]
public sealed class BatchGPhase3ServiceTests(BatchGDurableOperationsFixture fixture)
{
    private static OperationalHealthOptions Thresholds => new()
    {
        StaleHeartbeatAge = TimeSpan.FromMinutes(2),
        DegradedPendingAge = TimeSpan.FromMinutes(5),
        UnhealthyPendingAge = TimeSpan.FromMinutes(15),
        DegradedRetryPendingCount = 1,
        UnhealthyRetryPendingCount = 3,
        UnhealthyTerminalFailureCount = 1,
        DegradedDeliveryLag = TimeSpan.FromMinutes(5),
        UnhealthyDeliveryLag = TimeSpan.FromMinutes(15)
    };

    [Fact]
    public async Task Missing_worker_health_is_unknown_and_non_disclosing()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = new OperationalHealthService(db, fixture.TimeProvider, Thresholds);
        var result = await service.GetWorkerHealthAsync(
            BatchGDurableOperationsFixture.TenantA, $"missing-{Guid.NewGuid():N}");
        Assert.Equal(OperationalHealthStatuses.Unknown, result.Status);
        Assert.Null(result.LastObservedAtUtc);
    }

    [Fact]
    public async Task Fresh_empty_worker_health_is_healthy()
    {
        var worker = $"healthy-{Guid.NewGuid():N}";
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        await fixture.CreateService(db).UpsertHeartbeatAsync(
            fixture.CreateHeartbeat(worker, fixture.TimeProvider.GetUtcNow(), 0));
        var result = await new OperationalHealthService(db, fixture.TimeProvider, Thresholds)
            .GetWorkerHealthAsync(BatchGDurableOperationsFixture.TenantA, worker);
        Assert.Equal(OperationalHealthStatuses.Healthy, result.Status);
    }

    [Fact]
    public async Task Retry_count_or_delivery_lag_degrades_worker_health()
    {
        var worker = $"degraded-{Guid.NewGuid():N}";
        var now = fixture.TimeProvider.GetUtcNow();
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        await fixture.CreateService(db).UpsertHeartbeatAsync(
            fixture.CreateHeartbeat(worker, now, 1) with
            {
                RetryPendingCount = 1,
                OldestPendingAtUtc = now.AddMinutes(-6)
            });
        var result = await new OperationalHealthService(db, fixture.TimeProvider, Thresholds)
            .GetWorkerHealthAsync(BatchGDurableOperationsFixture.TenantA, worker);
        Assert.Equal(OperationalHealthStatuses.Degraded, result.Status);
        Assert.Equal(TimeSpan.FromMinutes(6), result.DeliveryLag);
    }

    [Fact]
    public async Task Terminal_failure_or_large_backlog_is_unhealthy()
    {
        var worker = $"unhealthy-{Guid.NewGuid():N}";
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        await fixture.CreateService(db).UpsertHeartbeatAsync(
            fixture.CreateHeartbeat(worker, fixture.TimeProvider.GetUtcNow(), 3) with
            {
                RetryPendingCount = 3,
                TerminalFailureCount = 1
            });
        var result = await new OperationalHealthService(db, fixture.TimeProvider, Thresholds)
            .GetWorkerHealthAsync(BatchGDurableOperationsFixture.TenantA, worker);
        Assert.Equal(OperationalHealthStatuses.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Old_heartbeat_is_stale_even_without_backlog()
    {
        var worker = $"stale-{Guid.NewGuid():N}";
        var now = fixture.TimeProvider.GetUtcNow();
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        await fixture.CreateService(db).UpsertHeartbeatAsync(fixture.CreateHeartbeat(worker, now, 0));
        var future = new FixedTimeProvider(now.AddMinutes(3));
        var result = await new OperationalHealthService(db, future, Thresholds)
            .GetWorkerHealthAsync(BatchGDurableOperationsFixture.TenantA, worker);
        Assert.Equal(OperationalHealthStatuses.Stale, result.Status);
    }

    [Fact]
    public async Task Health_and_failure_queries_are_bounded_and_tenant_scoped()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = new OperationalHealthService(db, fixture.TimeProvider, Thresholds);
        var healthLimit = await Assert.ThrowsAsync<DurableOperationRuleException>(
            () => service.QueryWorkerHealthAsync(BatchGDurableOperationsFixture.TenantA, 101));
        Assert.Equal("OPERATIONS.QUERY.LIMIT_INVALID", healthLimit.Code);
        var failureLimit = await Assert.ThrowsAsync<DurableOperationRuleException>(
            () => service.QueryFailuresAsync(BatchGDurableOperationsFixture.TenantA,
                new ProcessingFailureQuery(Limit: 101)));
        Assert.Equal("OPERATIONS.QUERY.LIMIT_INVALID", failureLimit.Code);

        var recorded = await fixture.CreateService(db).RecordFailureAsync(
            fixture.CreateFailure(Guid.NewGuid()) with { OwnerModule = "FINANCE", ProcessorCode = "FINANCE.TEST" });
        var rows = await service.QueryFailuresAsync(BatchGDurableOperationsFixture.TenantA,
            new ProcessingFailureQuery(OwnerModule: "finance", ProcessorCode: "finance.test", Limit: 10));
        Assert.Contains(rows, x => x.Id == recorded.Id);
    }

    [Fact]
    public async Task Normal_retry_recovery_preserves_failure_occurrence_evidence()
    {
        var source = Guid.NewGuid();
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var failure = await service.RecordFailureAsync(fixture.CreateFailure(source));
        var attempts = failure.AttemptCount;
        var firstFailed = failure.FirstFailedAtUtc;
        var lastFailed = failure.LastFailedAtUtc;
        var recovered = await service.RecoverFailureAfterNormalRetryAsync(
            failure.TenantId, failure.OwnerModule, failure.ProcessorCode, source,
            "{\"status\":\"RECOVERED\"}");
        Assert.NotNull(recovered);
        Assert.Equal(ProcessingFailureStates.Recovered, recovered!.State);
        Assert.False(recovered.Replayable);
        Assert.Equal(attempts, recovered.AttemptCount);
        Assert.Equal(firstFailed, recovered.FirstFailedAtUtc);
        Assert.Equal(lastFailed, recovered.LastFailedAtUtc);
    }

    [Theory]
    [InlineData("INVENTORY.EVENT.TENANT_MISMATCH", ProcessingFailureClassifications.ForgedTenant, ProcessingFailureStates.TerminalRejected, false)]
    [InlineData("FINANCE.EVENT.INVALID", ProcessingFailureClassifications.MalformedContract, ProcessingFailureStates.TerminalRejected, false)]
    [InlineData("AUTH.PERMISSION_DENIED", ProcessingFailureClassifications.Authorization, ProcessingFailureStates.TerminalRejected, false)]
    [InlineData("SECURITY.SIGNATURE_INVALID", ProcessingFailureClassifications.SecurityTerminal, ProcessingFailureStates.TerminalRejected, false)]
    [InlineData("FINANCE.PERIOD.NOT_OPEN", ProcessingFailureClassifications.Business, ProcessingFailureStates.BusinessFailed, true)]
    public void Server_owned_classifier_maps_governed_terminal_and_business_failures(
        string code, string classification, string state, bool replayable)
    {
        Exception exception = code.StartsWith("FINANCE", StringComparison.Ordinal)
            ? new Ogfi.Modules.Finance.FinanceRuleException(code, "safe")
            : code.StartsWith("INVENTORY", StringComparison.Ordinal)
                ? new Ogfi.Modules.Inventory.InventoryRuleException(code, "safe")
                : new DurableOperationRuleException(code, "safe");
        var result = ProcessorFailureClassifier.Classify(exception);
        Assert.Equal(classification, result.Classification);
        Assert.Equal(state, result.State);
        Assert.Equal(replayable, result.Replayable);
    }

    [Fact]
    public void Phase_3_permission_constants_are_exact_and_narrow()
    {
        Assert.Equal("operations.processing.read", OperationsPermissionCodes.ProcessingRead);
        Assert.Equal("operations.processing.replay", OperationsPermissionCodes.ProcessingReplay);
        Assert.Equal("audit.read", Ogfi.Modules.Audit.AuditPermissionCodes.Read);
        Assert.Equal("audit.trace.read", Ogfi.Modules.Audit.AuditPermissionCodes.TraceRead);
    }
}
