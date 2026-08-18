using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.DurableOperations;
using Ogfi.Modules.DurableOperations.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

[Collection(BatchGOperationsTestCollection.Name)]
public sealed class BatchGDurableOperationsTests(BatchGDurableOperationsFixture fixture)
{
    [Fact]
    public async Task Operation_lifecycle_accepts_only_approved_forward_transitions()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var succeeded = await fixture.CreateOperationAsync(service);
        succeeded = await service.TransitionAsync(succeeded.TenantId, succeeded.Id, 1, OperationStatuses.Running);
        succeeded = await service.TransitionAsync(succeeded.TenantId, succeeded.Id, 2, OperationStatuses.Succeeded,
            "JOURNAL", Guid.NewGuid(), safeDetailJson: """{"status":"POSTED"}""");
        Assert.Equal(OperationStatuses.Succeeded, succeeded.Status);
        Assert.Equal(3, succeeded.Version);
        Assert.NotNull(succeeded.StartedAtUtc);
        Assert.NotNull(succeeded.CompletedAtUtc);
        var terminal = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.TransitionAsync(succeeded.TenantId, succeeded.Id, 3, OperationStatuses.Running));
        Assert.Equal("OPERATIONS.TRANSITION.INVALID", terminal.Code);

        var cancelled = await fixture.CreateOperationAsync(service);
        cancelled = await service.TransitionAsync(cancelled.TenantId, cancelled.Id, 1, OperationStatuses.CancelRequested);
        cancelled = await service.TransitionAsync(cancelled.TenantId, cancelled.Id, 2, OperationStatuses.Cancelled);
        Assert.Equal(OperationStatuses.Cancelled, cancelled.Status);
        var invalid = await fixture.CreateOperationAsync(service);
        var backward = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.TransitionAsync(invalid.TenantId, invalid.Id, 1, OperationStatuses.Succeeded));
        Assert.Equal("OPERATIONS.TRANSITION.INVALID", backward.Code);
    }

    [Fact]
    public async Task Persisted_failure_is_the_only_replay_source_and_foreign_ids_are_non_disclosing()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var update = fixture.CreateFailure(Guid.NewGuid()) with
        {
            OriginalCausationId = "persisted-cause", CorrelationId = "persisted-correlation",
            LegalEntityId = Guid.NewGuid(), OutletId = Guid.NewGuid()
        };
        var failure = await service.RecordFailureAsync(update);

        // Simulate hostile in-memory values. The server-owned request path clears them and re-loads under RLS.
        failure.OwnerModule = "TAMPERED";
        failure.ProcessorCode = "TAMPERED";
        failure.FailureClassification = ProcessingFailureClassifications.ForgedTenant;
        failure.OriginalSourceEventId = Guid.NewGuid();
        failure.OriginalCausationId = "tampered-cause";
        failure.CorrelationId = "tampered-correlation";
        failure.Replayable = false;

        var coordinator = new ReplayCoordinator(service, []);
        var replayKey = $"persisted-{failure.Id:N}";
        var operation = await coordinator.RequestReplayForFailureAsync(
            update.TenantId, failure.Id, replayKey, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(update.OwnerModule, operation.OwnerModule);
        Assert.Equal(update.ProcessorCode, operation.OperationType);
        Assert.Equal(update.OriginalSourceEventId, operation.OriginalSourceEventId);
        Assert.Equal(update.OriginalCausationId, operation.OriginalCausationId);
        Assert.Equal(update.CorrelationId, operation.CorrelationId);
        Assert.Equal(update.LegalEntityId, operation.LegalEntityId);
        Assert.Equal(update.OutletId, operation.OutletId);
        Assert.Equal(OperationStatuses.Queued, operation.Status);

        var foreign = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            coordinator.RequestReplayForFailureAsync(
                BatchGDurableOperationsFixture.TenantB, failure.Id, $"foreign-{Guid.NewGuid():N}", null, null));
        Assert.Equal("OPERATIONS.FAILURE.NOT_FOUND", foreign.Code);
        var missing = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            coordinator.RequestReplayForFailureAsync(
                BatchGDurableOperationsFixture.TenantA, Guid.NewGuid(), $"missing-{Guid.NewGuid():N}", null, null));
        Assert.Equal(foreign.Code, missing.Code);
    }

    [Fact]
    public async Task Replay_request_is_idempotent_and_different_keys_are_exclusive_and_atomic()
    {
        var update = fixture.CreateFailure(Guid.NewGuid());
        Guid failureId;
        await using (var seed = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA))
            failureId = (await fixture.CreateService(seed).RecordFailureAsync(update)).Id;

        await using var firstDb = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        await using var secondDb = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var firstCoordinator = new ReplayCoordinator(fixture.CreateService(firstDb), []);
        var secondCoordinator = new ReplayCoordinator(fixture.CreateService(secondDb), []);
        var firstKey = $"first-{failureId:N}";
        var secondKey = $"second-{failureId:N}";
        var outcomes = await Task.WhenAll(
            CaptureOperationAsync(firstCoordinator.RequestReplayForFailureAsync(
                update.TenantId, failureId, firstKey, null, null)),
            CaptureOperationAsync(secondCoordinator.RequestReplayForFailureAsync(
                update.TenantId, failureId, secondKey, null, null)));
        var retained = Assert.Single(outcomes, x => x.Operation is not null).Operation!;
        Assert.Single(outcomes, x => x.Error?.Code == "OPERATIONS.REPLAY.ACTIVE_EXISTS");

        await using var verification = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        Assert.Equal(1, await verification.Operations.CountAsync(
            x => x.OriginalSourceEventId == update.OriginalSourceEventId));
        var linked = await verification.ProcessingFailures.SingleAsync(x => x.Id == failureId);
        Assert.Equal(retained.Id, linked.CurrentOperationId);
        var coordinator = new ReplayCoordinator(fixture.CreateService(verification), []);
        var same = await coordinator.RequestReplayForFailureAsync(
            update.TenantId, failureId, retained.ReplayRequestKey, null, null);
        Assert.Equal(retained.Id, same.Id);
        await using var sameKeyDbA = fixture.CreateContext(update.TenantId);
        await using var sameKeyDbB = fixture.CreateContext(update.TenantId);
        var sameKeyResults = await Task.WhenAll(
            new ReplayCoordinator(fixture.CreateService(sameKeyDbA), []).RequestReplayForFailureAsync(
                update.TenantId, failureId, retained.ReplayRequestKey, null, null),
            new ReplayCoordinator(fixture.CreateService(sameKeyDbB), []).RequestReplayForFailureAsync(
                update.TenantId, failureId, retained.ReplayRequestKey, null, null));
        Assert.All(sameKeyResults, x => Assert.Equal(retained.Id, x.Id));
        var active = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            coordinator.RequestReplayForFailureAsync(
                update.TenantId, failureId, $"third-{failureId:N}", null, null));
        Assert.Equal("OPERATIONS.REPLAY.ACTIVE_EXISTS", active.Code);

        retained = await fixture.CreateService(verification).TransitionAsync(
            retained.TenantId, retained.Id, retained.Version, OperationStatuses.Running);
        active = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            coordinator.RequestReplayForFailureAsync(
                update.TenantId, failureId, $"running-{failureId:N}", null, null));
        Assert.Equal("OPERATIONS.REPLAY.ACTIVE_EXISTS", active.Code);
    }

    [Fact]
    public async Task Failure_classifications_terminality_and_state_matrix_are_enforced()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var unknown = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()) with { FailureClassification = "UNKNOWN" }));
        Assert.Equal("OPERATIONS.FAILURE.CLASSIFICATION_INVALID", unknown.Code);

        var update = fixture.CreateFailure(Guid.NewGuid()) with { State = ProcessingFailureStates.Pending };
        var failure = await service.RecordFailureAsync(update);
        failure = await service.RecordFailureAsync(update with
        {
            FailureId = failure.Id, State = ProcessingFailureStates.RetryPending
        });
        var pendingRegression = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.RecordFailureAsync(update with { FailureId = failure.Id }));
        Assert.Equal("OPERATIONS.FAILURE.STATE_TRANSITION_INVALID", pendingRegression.Code);

        failure = await service.RecordFailureAsync(update with
        {
            FailureId = failure.Id, FailureClassification = ProcessingFailureClassifications.Authorization,
            State = ProcessingFailureStates.RetryPending, Replayable = true,
            CurrentOperationId = Guid.NewGuid()
        });
        Assert.Equal(ProcessingFailureStates.TerminalRejected, failure.State);
        Assert.False(failure.Replayable);
        Assert.Null(failure.CurrentOperationId);
        var terminalRegression = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.RecordFailureAsync(update with
            {
                FailureId = failure.Id, FailureClassification = ProcessingFailureClassifications.Transient,
                State = ProcessingFailureStates.RetryPending
            }));
        Assert.Equal("OPERATIONS.FAILURE.STATE_TRANSITION_INVALID", terminalRegression.Code);

        var recoveryOccurrence = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()) with
            {
                State = ProcessingFailureStates.Recovered
            }));
        Assert.Equal("OPERATIONS.FAILURE.OCCURRENCE_STATE_INVALID", recoveryOccurrence.Code);
    }

    [Fact]
    public async Task Failure_lineage_and_current_operation_linkage_are_immutable_and_validated()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var update = fixture.CreateFailure(Guid.NewGuid()) with
        {
            LegalEntityId = Guid.NewGuid(), OutletId = Guid.NewGuid()
        };
        var failure = await service.RecordFailureAsync(update);
        var conflicts = new[]
        {
            update with { FailureId = failure.Id, OwnerModule = "FINANCE" },
            update with { FailureId = failure.Id, ProcessorCode = "OTHER_PROCESSOR" },
            update with { FailureId = failure.Id, OriginalSourceEventId = Guid.NewGuid() },
            update with { FailureId = failure.Id, OriginalCausationId = "other-cause" },
            update with { FailureId = failure.Id, CorrelationId = "other-correlation" },
            update with { FailureId = failure.Id, ResourceType = "OTHER_RESOURCE" },
            update with { FailureId = failure.Id, ResourceId = Guid.NewGuid() },
            update with { FailureId = failure.Id, LegalEntityId = Guid.NewGuid() },
            update with { FailureId = failure.Id, OutletId = Guid.NewGuid() }
        };
        foreach (var conflict in conflicts)
        {
            var exception = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
                service.RecordFailureAsync(conflict));
            Assert.Equal("OPERATIONS.FAILURE.IDENTITY_CONFLICT", exception.Code);
        }

        var unrelated = await fixture.CreateOperationAsync(service, ownerModule: "FINANCE");
        var mismatch = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.RecordFailureAsync(update with
            {
                FailureId = failure.Id, CurrentOperationId = unrelated.Id
            }));
        Assert.Equal("OPERATIONS.FAILURE.OPERATION_MISMATCH", mismatch.Code);
    }

    [Fact]
    public async Task Concurrent_failure_inserts_and_updates_preserve_every_attempt()
    {
        var update = fixture.CreateFailure(Guid.NewGuid());
        var insertBarrier = new ConcurrentSaveBarrier(2);
        await using var firstInsert = fixture.CreateContext(update.TenantId, insertBarrier);
        await using var secondInsert = fixture.CreateContext(update.TenantId, insertBarrier);
        var inserted = await Task.WhenAll(
            fixture.CreateService(firstInsert).RecordFailureAsync(update),
            fixture.CreateService(secondInsert).RecordFailureAsync(update));
        Assert.Equal(inserted[0].Id, inserted[1].Id);

        var updateBarrier = new ConcurrentSaveBarrier(2);
        await using var firstUpdate = fixture.CreateContext(update.TenantId, updateBarrier);
        await using var secondUpdate = fixture.CreateContext(update.TenantId, updateBarrier);
        await Task.WhenAll(
            fixture.CreateService(firstUpdate).RecordFailureAsync(update with { FailureId = inserted[0].Id }),
            fixture.CreateService(secondUpdate).RecordFailureAsync(update with { FailureId = inserted[0].Id }));

        await using var verification = fixture.CreateContext(update.TenantId);
        var failure = Assert.Single(await verification.ProcessingFailures
            .Where(x => x.OriginalSourceEventId == update.OriginalSourceEventId).ToListAsync());
        Assert.Equal(4, failure.AttemptCount);
        Assert.Equal(4, failure.Version);
    }

    [Fact]
    public async Task Heartbeat_observations_are_idempotent_monotonic_and_replica_safe()
    {
        var baseline = fixture.TimeProvider.GetUtcNow();
        var worker = $"replica-{Guid.NewGuid():N}";
        var started = fixture.CreateHeartbeat(worker, baseline.AddMinutes(1), pendingCount: 5, sequence: 1);
        await using var verification = fixture.CreateContext(started.TenantId);
        var service = fixture.CreateService(verification);
        await service.UpsertHeartbeatAsync(started);
        var succeeded = started with
        {
            ObservationId = Guid.NewGuid(), ObservationSequence = 2,
            LastSucceededAtUtc = started.LastIterationStartedAtUtc, PendingCount = 1
        };
        var persisted = await service.UpsertHeartbeatAsync(succeeded);
        Assert.Equal(2, persisted.ObservationSequence);
        Assert.Equal(1, persisted.PendingCount);
        var duplicate = await service.UpsertHeartbeatAsync(succeeded);
        Assert.Equal(persisted.Id, duplicate.Id);
        var conflict = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.UpsertHeartbeatAsync(succeeded with { PendingCount = 2 }));
        Assert.Equal("OPERATIONS.HEARTBEAT.OBSERVATION_CONFLICT", conflict.Code);
        conflict = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.UpsertHeartbeatAsync(succeeded with
            {
                ObservationSequence = 3, PendingCount = 0
            }));
        Assert.Equal("OPERATIONS.HEARTBEAT.OBSERVATION_CONFLICT", conflict.Code);
        var stale = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.UpsertHeartbeatAsync(started with { ObservationId = Guid.NewGuid() }));
        Assert.Equal("OPERATIONS.HEARTBEAT.STALE", stale.Code);

        var replicaWorker = $"replica-{Guid.NewGuid():N}";
        var older = fixture.CreateHeartbeat(replicaWorker, baseline.AddMinutes(2), 5, sequence: 1);
        var newer = fixture.CreateHeartbeat(replicaWorker, baseline.AddMinutes(3), 1, sequence: 1);
        await using var replicaA = fixture.CreateContext(older.TenantId);
        await using var replicaB = fixture.CreateContext(older.TenantId);
        await Task.WhenAll(
            CaptureHeartbeatAsync(fixture.CreateService(replicaA).UpsertHeartbeatAsync(older)),
            CaptureHeartbeatAsync(fixture.CreateService(replicaB).UpsertHeartbeatAsync(newer)));
        verification.ChangeTracker.Clear();
        persisted = await verification.WorkerHeartbeats.SingleAsync(
            x => x.WorkerCode == replicaWorker.ToUpperInvariant());
        Assert.Equal(newer.LastIterationStartedAtUtc, persisted.LastIterationStartedAtUtc);
        Assert.Equal(newer.ObservationId, persisted.ObservationId);
    }

    [Fact]
    public async Task Attempt_and_checkpoint_lifecycle_is_database_backed_and_terminal_safe()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var operation = await fixture.CreateOperationAsync(service);
        var queuedAttempt = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.StartAttemptAsync(operation.TenantId, operation.Id, "worker"));
        Assert.Equal("OPERATIONS.ATTEMPT.OPERATION_NOT_RUNNING", queuedAttempt.Code);
        var queuedCheckpoint = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.AddCheckpointAsync(operation.TenantId, operation.Id, 1, "QUEUED", 1));
        Assert.Equal("OPERATIONS.CHECKPOINT.OPERATION_NOT_RUNNING", queuedCheckpoint.Code);

        operation = await service.TransitionAsync(operation.TenantId, operation.Id, 1, OperationStatuses.Running);
        var attempt = await service.StartAttemptAsync(operation.TenantId, operation.Id, "worker-a");
        var active = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.StartAttemptAsync(operation.TenantId, operation.Id, "worker-b"));
        Assert.Equal("OPERATIONS.ATTEMPT.ACTIVE_EXISTS", active.Code);
        attempt = await service.RenewAttemptLeaseAsync(
            operation.TenantId, attempt.Id, attempt.LeaseToken, "worker-a");
        Assert.Equal(2, attempt.Version);
        await service.AddCheckpointAsync(operation.TenantId, operation.Id, 1, "LOADED", 25,
            """{"progress":25}""");
        var regression = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.AddCheckpointAsync(operation.TenantId, operation.Id, 2, "REGRESSION", 24));
        Assert.Equal("OPERATIONS.CHECKPOINT.PROGRESS_REGRESSION", regression.Code);
        await service.CompleteAttemptAsync(
            operation.TenantId, attempt.Id, attempt.LeaseToken, succeeded: true);
        operation = await service.TransitionAsync(operation.TenantId, operation.Id, operation.Version, OperationStatuses.Succeeded);
        var terminalAttempt = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.StartAttemptAsync(operation.TenantId, operation.Id, "worker"));
        Assert.Equal("OPERATIONS.ATTEMPT.OPERATION_NOT_RUNNING", terminalAttempt.Code);
        var terminalCheckpoint = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.AddCheckpointAsync(operation.TenantId, operation.Id, 2, "TERMINAL", 100));
        Assert.Equal("OPERATIONS.CHECKPOINT.OPERATION_NOT_RUNNING", terminalCheckpoint.Code);
        Assert.True(await fixture.HasActiveAttemptPartialUniqueIndexAsync());
    }

    [Fact]
    public async Task Concurrent_attempt_claim_and_completion_allow_one_winner()
    {
        Guid operationId;
        Guid tenantId;
        await using (var seed = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA))
        {
            var service = fixture.CreateService(seed);
            var operation = await fixture.CreateOperationAsync(service);
            operation = await service.TransitionAsync(operation.TenantId, operation.Id, 1, OperationStatuses.Running);
            operationId = operation.Id;
            tenantId = operation.TenantId;
        }

        await using var firstDb = fixture.CreateContext(tenantId);
        await using var secondDb = fixture.CreateContext(tenantId);
        var starts = await Task.WhenAll(
            CaptureAttemptAsync(fixture.CreateService(firstDb).StartAttemptAsync(tenantId, operationId, "replica-a")),
            CaptureAttemptAsync(fixture.CreateService(secondDb).StartAttemptAsync(tenantId, operationId, "replica-b")));
        var attempt = Assert.Single(starts, x => x.Attempt is not null).Attempt!;
        Assert.Single(starts, x => x.Error?.Code == "OPERATIONS.ATTEMPT.ACTIVE_EXISTS");

        var completionBarrier = new ConcurrentSaveBarrier(2);
        await using var firstCompletion = fixture.CreateContext(tenantId, completionBarrier);
        await using var secondCompletion = fixture.CreateContext(tenantId, completionBarrier);
        var completions = await Task.WhenAll(
            CaptureAttemptAsync(fixture.CreateService(firstCompletion).CompleteAttemptAsync(
                tenantId, attempt.Id, attempt.LeaseToken, true)),
            CaptureAttemptAsync(fixture.CreateService(secondCompletion).CompleteAttemptAsync(
                tenantId, attempt.Id, attempt.LeaseToken, true)));
        Assert.Single(completions, x => x.Attempt is not null);
        Assert.Single(completions, x => x.Error?.Code == "OPERATIONS.ATTEMPT.VERSION_CONFLICT");
    }

    [Fact]
    public async Task Replay_request_is_queued_only_and_execution_is_attempt_backed_with_retry()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var failure = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
        var handler = new ControlledReplayHandler((dispatch, _) => dispatch == 1
            ? new ReplayDispatchResult(false, SafeErrorCode: "TRANSIENT", SafeDetailJson: """{"retryCount":1}""", Retryable: true)
            : new ReplayDispatchResult(true, "TEST_RESULT", Guid.NewGuid(), SafeDetailJson: """{"result":"RECOVERED"}"""));
        var coordinator = new ReplayCoordinator(service, [handler]);
        var operation = await coordinator.RequestReplayForFailureAsync(
            failure.TenantId, failure.Id, $"attempt-backed-{failure.Id:N}", null, null);
        Assert.Equal(OperationStatuses.Queued, operation.Status);
        Assert.Equal(0, handler.DispatchCount);
        Assert.Empty(await db.OperationAttempts.Where(x => x.OperationId == operation.Id).ToListAsync());

        operation = await coordinator.ExecuteQueuedReplayOperationAsync(operation.TenantId, operation.Id, "worker-a");
        Assert.Equal(OperationStatuses.Running, operation.Status);
        operation = await coordinator.ExecuteQueuedReplayOperationAsync(operation.TenantId, operation.Id, "worker-b");
        Assert.Equal(OperationStatuses.Succeeded, operation.Status);
        var attempts = await db.OperationAttempts.Where(x => x.OperationId == operation.Id)
            .OrderBy(x => x.AttemptNumber).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, attempts.Select(x => x.AttemptNumber));
        Assert.Equal(new[] { OperationAttemptStatuses.Failed, OperationAttemptStatuses.Succeeded },
            attempts.Select(x => x.Status));
        Assert.All(attempts, x => Assert.Equal(operation.OriginalSourceEventId, x.OriginalSourceEventId));
        var checkpoints = await db.OperationCheckpoints.Where(x => x.OperationId == operation.Id)
            .OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal(4, checkpoints.Count);
        Assert.Equal(checkpoints.OrderBy(x => x.Sequence).Select(x => x.Sequence), checkpoints.Select(x => x.Sequence));
        Assert.Equal(checkpoints.OrderBy(x => x.ProgressPercentage).Select(x => x.ProgressPercentage),
            checkpoints.Select(x => x.ProgressPercentage));
    }

    [Fact]
    public async Task Later_request_is_allowed_after_failure_but_success_recovers_and_blocks_replay()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var failure = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
        var attemptCount = failure.AttemptCount;
        var firstFailedAt = failure.FirstFailedAtUtc;
        var lastFailedAt = failure.LastFailedAtUtc;
        var failing = new ControlledReplayHandler((_, _) =>
            new ReplayDispatchResult(false, SafeErrorCode: "BUSINESS_REJECTED", Retryable: false));
        var firstCoordinator = new ReplayCoordinator(service, [failing]);
        var first = await firstCoordinator.RequestReplayForFailureAsync(
            failure.TenantId, failure.Id, $"failed-{failure.Id:N}", null, null);
        first = await firstCoordinator.ExecuteQueuedReplayOperationAsync(first.TenantId, first.Id, "worker-a");
        Assert.Equal(OperationStatuses.Failed, first.Status);

        var idempotent = new IdempotentReplayHandler();
        var secondCoordinator = new ReplayCoordinator(service, [idempotent]);
        var second = await secondCoordinator.RequestReplayForFailureAsync(
            failure.TenantId, failure.Id, $"authorized-after-failure-{failure.Id:N}", null, null);
        second = await secondCoordinator.ExecuteQueuedReplayOperationAsync(second.TenantId, second.Id, "worker-b");
        Assert.Equal(OperationStatuses.Succeeded, second.Status);
        Assert.Equal(first.OriginalSourceEventId, second.OriginalSourceEventId);
        Assert.Equal(1, idempotent.AuthoritativeEffectCount);
        var recovered = await service.GetFailureAsync(failure.TenantId, failure.Id);
        Assert.Equal(ProcessingFailureStates.Recovered, recovered.State);
        Assert.False(recovered.Replayable);
        Assert.Null(recovered.CurrentOperationId);
        Assert.Equal(second.Id, recovered.RecoveryOperationId);
        Assert.Equal(attemptCount, recovered.AttemptCount);
        Assert.Equal(firstFailedAt, recovered.FirstFailedAtUtc);
        Assert.Equal(lastFailedAt, recovered.LastFailedAtUtc);
        var rejected = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            secondCoordinator.RequestReplayForFailureAsync(
                failure.TenantId, failure.Id, $"after-recovery-{failure.Id:N}", null, null));
        Assert.Equal("OPERATIONS.REPLAY.NOT_ALLOWED", rejected.Code);

        var completed = await service.TransitionFailureStateAsync(
            recovered.TenantId, recovered.Id, recovered.Version,
            ProcessingFailureStates.Completed, recovered.RecoveryOperationId,
            "OPERATIONS.REPLAY.COMPLETED", """{"status":"COMPLETED"}""");
        Assert.Equal(attemptCount, completed.AttemptCount);
        Assert.Equal(firstFailedAt, completed.FirstFailedAtUtc);
        Assert.Equal(lastFailedAt, completed.LastFailedAtUtc);
        Assert.False(completed.Replayable);
        Assert.Null(completed.CurrentOperationId);
        rejected = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            secondCoordinator.RequestReplayForFailureAsync(
                failure.TenantId, failure.Id, $"after-completion-{failure.Id:N}", null, null));
        Assert.Equal("OPERATIONS.REPLAY.NOT_ALLOWED", rejected.Code);
        var terminalCancellation = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            secondCoordinator.RequestCancellationAsync(second.TenantId, second.Id));
        Assert.Equal("OPERATIONS.CANCELLATION.NOT_ALLOWED", terminalCancellation.Code);
    }

    [Fact]
    public async Task Retryable_owner_failure_transitions_operation_only_after_bounded_exhaustion()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var failure = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
        var handler = new ControlledReplayHandler((dispatch, _) => new ReplayDispatchResult(
            false, SafeErrorCode: "TRANSIENT", SafeDetailJson: $$"""{"retryCount":{{dispatch}}}""",
            Retryable: true));
        var coordinator = new ReplayCoordinator(service, [handler]);
        var operation = await coordinator.RequestReplayForFailureAsync(
            failure.TenantId, failure.Id, $"exhausted-{failure.Id:N}", null, null);

        operation = await coordinator.ExecuteQueuedReplayOperationAsync(operation.TenantId, operation.Id, "worker-1");
        Assert.Equal(OperationStatuses.Running, operation.Status);
        operation = await coordinator.ExecuteQueuedReplayOperationAsync(operation.TenantId, operation.Id, "worker-2");
        Assert.Equal(OperationStatuses.Running, operation.Status);
        operation = await coordinator.ExecuteQueuedReplayOperationAsync(operation.TenantId, operation.Id, "worker-3");
        Assert.Equal(OperationStatuses.Failed, operation.Status);
        Assert.Equal(3, await db.OperationAttempts.CountAsync(x => x.OperationId == operation.Id));
        Assert.Equal(3, handler.DispatchCount);
    }

    [Fact]
    public async Task Terminal_and_recovered_failures_reject_replay_and_cancellation_is_explicit()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var terminal = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()) with
        {
            FailureClassification = ProcessingFailureClassifications.ForgedTenant,
            State = ProcessingFailureStates.Pending, Replayable = true
        });
        var coordinator = new ReplayCoordinator(service, []);
        var rejected = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            coordinator.RequestReplayForFailureAsync(
                terminal.TenantId, terminal.Id, $"terminal-{terminal.Id:N}", null, null));
        Assert.Equal("OPERATIONS.REPLAY.NOT_ALLOWED", rejected.Code);

        var replayable = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
        coordinator = new ReplayCoordinator(service, []);
        var operation = await coordinator.RequestReplayForFailureAsync(
            replayable.TenantId, replayable.Id, $"cancelled-{replayable.Id:N}", null, null);
        operation = await coordinator.RequestCancellationAsync(operation.TenantId, operation.Id);
        Assert.Equal(OperationStatuses.CancelRequested, operation.Status);
        operation = await coordinator.ExecuteQueuedReplayOperationAsync(
            operation.TenantId, operation.Id, "worker");
        Assert.Equal(OperationStatuses.Cancelled, operation.Status);
        var afterCancellation = await coordinator.RequestReplayForFailureAsync(
            replayable.TenantId, replayable.Id, $"after-cancel-{replayable.Id:N}", null, null);
        Assert.Equal(OperationStatuses.Queued, afterCancellation.Status);

        var interruptedFailure = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
        var cancelling = new CancellingReplayHandler();
        coordinator = new ReplayCoordinator(service, [cancelling]);
        var interrupted = await coordinator.RequestReplayForFailureAsync(
            interruptedFailure.TenantId, interruptedFailure.Id,
            $"interrupted-{interruptedFailure.Id:N}", null, null);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            coordinator.ExecuteQueuedReplayOperationAsync(interrupted.TenantId, interrupted.Id, "worker"));
        interrupted = await service.GetOperationAsync(interrupted.TenantId, interrupted.Id);
        Assert.Equal(OperationStatuses.Running, interrupted.Status);
        var attempt = Assert.Single(await db.OperationAttempts
            .Where(x => x.OperationId == interrupted.Id).ToListAsync());
        Assert.Equal(OperationAttemptStatuses.Running, attempt.Status);
        Assert.Null(attempt.SafeErrorCode);
    }

    [Fact]
    public async Task Expired_attempt_lease_recovers_crashes_without_rewriting_history()
    {
        var startedAt = fixture.TimeProvider.GetUtcNow();
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);

        var beforeAttemptFailure = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
        var successHandler = new ControlledReplayHandler((_, _) =>
            new ReplayDispatchResult(true, "RESULT", Guid.NewGuid(), SafeDetailJson: """{"result":"RECOVERED"}"""));
        var coordinator = new ReplayCoordinator(service, [successHandler]);
        var beforeAttempt = await coordinator.RequestReplayForFailureAsync(
            beforeAttemptFailure.TenantId, beforeAttemptFailure.Id,
            $"before-attempt-{beforeAttemptFailure.Id:N}", null, null);
        beforeAttempt = await service.TransitionAsync(
            beforeAttempt.TenantId, beforeAttempt.Id, beforeAttempt.Version, OperationStatuses.Running);
        beforeAttempt = await coordinator.ExecuteQueuedReplayOperationAsync(
            beforeAttempt.TenantId, beforeAttempt.Id, "recovery-worker");
        Assert.Equal(OperationStatuses.Succeeded, beforeAttempt.Status);
        Assert.Single(await db.OperationAttempts.Where(x => x.OperationId == beforeAttempt.Id).ToListAsync());

        var leaseFailure = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
        var leased = await coordinator.RequestReplayForFailureAsync(
            leaseFailure.TenantId, leaseFailure.Id, $"lease-{leaseFailure.Id:N}", null, null);
        leased = await service.TransitionAsync(
            leased.TenantId, leased.Id, leased.Version, OperationStatuses.Running);
        var staleAttempt = await service.StartAttemptAsync(leased.TenantId, leased.Id, "crashed-worker");
        var active = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.StartAttemptAsync(leased.TenantId, leased.Id, "early-recovery"));
        Assert.Equal("OPERATIONS.ATTEMPT.ACTIVE_EXISTS", active.Code);

        var future = new FixedTimeProvider(startedAt.Add(DurableOperationService.DefaultAttemptLease).AddSeconds(1));
        await using var futureDb = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var futureService = fixture.CreateService(futureDb, future);
        var leaseLost = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            futureService.CompleteAttemptAsync(
                leased.TenantId, staleAttempt.Id, staleAttempt.LeaseToken, succeeded: true));
        Assert.Equal("OPERATIONS.ATTEMPT.LEASE_LOST", leaseLost.Code);
        var recoveryHandler = new ControlledReplayHandler((_, _) =>
            new ReplayDispatchResult(true, "RESULT", Guid.NewGuid(), SafeDetailJson: """{"result":"RECOVERED"}"""));
        var recoveredOperation = await new ReplayCoordinator(futureService, [recoveryHandler])
            .ExecuteQueuedReplayOperationAsync(leased.TenantId, leased.Id, "late-recovery");
        Assert.Equal(OperationStatuses.Succeeded, recoveredOperation.Status);
        var attempts = await futureDb.OperationAttempts.Where(x => x.OperationId == leased.Id)
            .OrderBy(x => x.AttemptNumber).ToListAsync();
        Assert.Equal(new[] { OperationAttemptStatuses.Abandoned, OperationAttemptStatuses.Succeeded },
            attempts.Select(x => x.Status));
        Assert.Equal("OPERATIONS.ATTEMPT.STALE_LEASE", attempts[0].SafeErrorCode);
        Assert.Equal(staleAttempt.OriginalSourceEventId, attempts[1].OriginalSourceEventId);
        Assert.Equal(staleAttempt.OriginalCausationId, attempts[1].OriginalCausationId);
        Assert.Equal(staleAttempt.CorrelationId, attempts[1].CorrelationId);
    }

    [Fact]
    public async Task Crash_after_owner_effect_recovers_without_duplicate_authoritative_effect()
    {
        var startedAt = fixture.TimeProvider.GetUtcNow();
        var handler = new InterruptAfterEffectReplayHandler();
        Guid tenantId;
        Guid operationId;
        await using (var firstDb = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA))
        {
            var firstService = fixture.CreateService(firstDb);
            var failure = await firstService.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
            var firstCoordinator = new ReplayCoordinator(firstService, [handler]);
            var operation = await firstCoordinator.RequestReplayForFailureAsync(
                failure.TenantId, failure.Id, $"effect-crash-{failure.Id:N}", null, null);
            tenantId = operation.TenantId;
            operationId = operation.Id;
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                firstCoordinator.ExecuteQueuedReplayOperationAsync(tenantId, operationId, "worker-before-crash"));
        }

        var future = new FixedTimeProvider(startedAt.Add(DurableOperationService.DefaultAttemptLease).AddSeconds(1));
        await using var recoveryDb = fixture.CreateContext(tenantId);
        var recoveryService = fixture.CreateService(recoveryDb, future);
        var recoveryCoordinator = new ReplayCoordinator(recoveryService, [handler]);
        var recovered = await recoveryCoordinator.ExecuteQueuedReplayOperationAsync(
            tenantId, operationId, "worker-after-crash");
        Assert.Equal(OperationStatuses.Succeeded, recovered.Status);
        Assert.Equal(1, handler.AuthoritativeEffectCount);
        Assert.Equal(2, handler.DispatchCount);
        var attempts = await recoveryDb.OperationAttempts.Where(x => x.OperationId == operationId)
            .OrderBy(x => x.AttemptNumber).ToListAsync();
        Assert.Equal(new[] { OperationAttemptStatuses.Abandoned, OperationAttemptStatuses.Succeeded },
            attempts.Select(x => x.Status));
    }

    [Fact]
    public async Task Failure_identity_includes_owner_module()
    {
        var source = Guid.NewGuid();
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var inventory = await service.RecordFailureAsync(fixture.CreateFailure(source));
        var finance = await service.RecordFailureAsync(fixture.CreateFailure(source) with
        {
            OwnerModule = "FINANCE"
        });
        Assert.NotEqual(inventory.Id, finance.Id);
        Assert.Equal(2, await db.ProcessingFailures.CountAsync(
            x => x.OriginalSourceEventId == source && x.ProcessorCode == "STOCK_CONSEQUENCE"));
    }

    [Fact]
    public async Task Cancellation_and_worker_completion_race_has_one_terminal_winner()
    {
        Guid tenantId;
        Guid operationId;
        Guid attemptId;
        Guid leaseToken;
        await using (var seed = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA))
        {
            var service = fixture.CreateService(seed);
            var failure = await service.RecordFailureAsync(fixture.CreateFailure(Guid.NewGuid()));
            var coordinator = new ReplayCoordinator(service, []);
            var operation = await coordinator.RequestReplayForFailureAsync(
                failure.TenantId, failure.Id, $"cancellation-race-{failure.Id:N}", null, null);
            operation = await service.TransitionAsync(
                operation.TenantId, operation.Id, operation.Version, OperationStatuses.Running);
            var attempt = await service.StartAttemptAsync(operation.TenantId, operation.Id, "race-worker");
            tenantId = operation.TenantId;
            operationId = operation.Id;
            attemptId = attempt.Id;
            leaseToken = attempt.LeaseToken;
        }

        var barrier = new ConcurrentSaveBarrier(2);
        await using var completionDb = fixture.CreateContext(tenantId, barrier);
        await using var cancellationDb = fixture.CreateContext(tenantId, barrier);
        var outcomes = await Task.WhenAll(
            CaptureOperationAsync(fixture.CreateService(completionDb).CompleteReplaySuccessAsync(
                tenantId, operationId, attemptId, leaseToken, "RESULT", Guid.NewGuid(),
                """{"result":"RECOVERED"}""")),
            CaptureOperationAsync(fixture.CreateService(cancellationDb).RequestCancellationAsync(
                tenantId, operationId)));
        Assert.Single(outcomes, x => x.Operation is not null);
        Assert.Single(outcomes, x => x.Error is not null);

        await using var verification = fixture.CreateContext(tenantId);
        var finalService = fixture.CreateService(verification);
        var operationState = await finalService.GetOperationAsync(tenantId, operationId);
        if (operationState.Status == OperationStatuses.CancelRequested)
            operationState = await finalService.ObserveRequestedCancellationAsync(tenantId, operationId);
        Assert.Contains(operationState.Status, new[]
        {
            OperationStatuses.Succeeded, OperationStatuses.Cancelled
        });
        var failureState = await verification.ProcessingFailures.SingleAsync(x =>
            x.CurrentOperationId == operationId || x.RecoveryOperationId == operationId);
        if (operationState.Status == OperationStatuses.Succeeded)
        {
            Assert.Equal(ProcessingFailureStates.Recovered, failureState.State);
            Assert.False(failureState.Replayable);
        }
        else
        {
            Assert.Equal(ProcessingFailureStates.RetryPending, failureState.State);
            Assert.True(failureState.Replayable);
        }
    }

    public static TheoryData<string, string> RejectedSafeDetails => new()
    {
        { """{"request":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"event":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"exception":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"connection":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"cookie":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"token":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"password":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"credential":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"stackTrace":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { $$"""{"status":"{{new string('x', 1_001)}}"}""", "OPERATIONS.SAFE_DETAIL.TOO_LARGE" },
        { $$"""{"status":"{{new string('x', 8_192)}}"}""", "OPERATIONS.SAFE_DETAIL.TOO_LARGE" },
        { """{"result":{"result":{"result":{"result":{"result":{"result":{"result":{"result":{"result":{}}}}}}}}}}""", "OPERATIONS.SAFE_DETAIL.INVALID" }
    };

    [Theory]
    [MemberData(nameof(RejectedSafeDetails))]
    public async Task Safe_detail_policy_rejects_raw_sensitive_oversized_and_overdeep_data(
        string detail, string expectedCode)
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var operation = await fixture.CreateOperationAsync(service);
        operation = await service.TransitionAsync(operation.TenantId, operation.Id, 1, OperationStatuses.Running);
        var exception = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.AddCheckpointAsync(operation.TenantId, operation.Id, 1, "SAFE", 1, detail));
        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task Operations_tables_are_force_rls_protected_and_tenant_reads_are_isolated()
    {
        var role = await fixture.GetRuntimeRoleStateAsync();
        Assert.False(role.CanLogin);
        Assert.False(role.IsSuperuser);
        Assert.False(role.BypassesRls);
        var states = await fixture.GetRlsStatesAsync();
        Assert.Equal(5, states.Count);
        Assert.All(states, state =>
        {
            Assert.True(state.Enabled, $"RLS is not enabled for operations.{state.Table}");
            Assert.True(state.Forced, $"RLS is not forced for operations.{state.Table}");
            Assert.Equal(1, state.PolicyCount);
        });
        var crossTenant = await Assert.ThrowsAsync<PostgresException>(fixture.AttemptCrossTenantInsertAsRuntimeRoleAsync);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, crossTenant.SqlState);
        await using (var tenantB = fixture.CreateContext(BatchGDurableOperationsFixture.TenantB))
            await fixture.CreateOperationAsync(fixture.CreateService(tenantB), BatchGDurableOperationsFixture.TenantB);
        await using var tenantA = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var visible = await fixture.CreateService(tenantA).QueryOperationsAsync(BatchGDurableOperationsFixture.TenantA, 100);
        Assert.DoesNotContain(visible, x => x.TenantId == BatchGDurableOperationsFixture.TenantB);
        var limit = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            fixture.CreateService(tenantA).QueryOperationsAsync(BatchGDurableOperationsFixture.TenantA, 101));
        Assert.Equal("OPERATIONS.QUERY.LIMIT_INVALID", limit.Code);
    }

    private static async Task<HeartbeatOutcome> CaptureHeartbeatAsync(Task<WorkerHeartbeat> operation)
    {
        try { return new(await operation, null); }
        catch (DurableOperationRuleException exception) { return new(null, exception); }
    }

    private static async Task<AttemptOutcome> CaptureAttemptAsync(Task<OperationAttempt> operation)
    {
        try { return new(await operation, null); }
        catch (DurableOperationRuleException exception) { return new(null, exception); }
    }

    private static async Task<OperationOutcome> CaptureOperationAsync(Task<Operation> operation)
    {
        try { return new(await operation, null); }
        catch (DurableOperationRuleException exception) { return new(null, exception); }
    }
}

[CollectionDefinition(Name)]
public sealed class BatchGOperationsTestCollection : ICollectionFixture<BatchGDurableOperationsFixture>
{
    public const string Name = "Batch G operations shared database fixture";
}

public sealed class BatchGDurableOperationsFixture : IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("a7000000-0000-0000-0000-000000000002");
    public static readonly Guid TenantB = Guid.Parse("b7000000-0000-0000-0000-000000000002");
    private const string RlsTestRole = "ogfi_batch_g_operations_rls_test";
    private readonly string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required for Durable Operations integration evidence.");

    public TimeProvider TimeProvider { get; } = new FixedTimeProvider(
        new DateTimeOffset(2026, 8, 18, 8, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await using (var db = CreateContext(TenantA)) await db.Database.MigrateAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $$"""
            DO $$ BEGIN
                CREATE ROLE {{RlsTestRole}} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
            EXCEPTION WHEN duplicate_object THEN NULL;
            END $$;
            GRANT USAGE ON SCHEMA operations TO {{RlsTestRole}};
            GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA operations TO {{RlsTestRole}};
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public DurableOperationsDbContext CreateContext(Guid tenantId, params IInterceptor[] interceptors)
    {
        var executionContext = new TenantExecutionContextAccessor();
        executionContext.SetCandidateTenant(tenantId);
        var options = new DbContextOptionsBuilder<DurableOperationsDbContext>().UseNpgsql(connectionString);
        options.AddInterceptors(new TenantSessionConnectionInterceptor(executionContext));
        options.AddInterceptors(interceptors);
        return new DurableOperationsDbContext(options.Options);
    }

    public DurableOperationService CreateService(
        DurableOperationsDbContext dbContext, TimeProvider? timeProvider = null)
        => new(dbContext, timeProvider ?? TimeProvider);

    public CreateReplayOperationRequest CreateRequest(
        Guid sourceEventId, string? replayRequestKey = null, Guid? tenantId = null,
        string ownerModule = "INVENTORY", string operationType = "STOCK_CONSEQUENCE")
        => new(tenantId ?? TenantA, replayRequestKey ?? $"request-{Guid.NewGuid():N}", operationType,
            ownerModule, sourceEventId, $"cause-{sourceEventId:N}", $"corr-{sourceEventId:N}");

    public Task<Operation> CreateOperationAsync(
        DurableOperationService service, Guid? tenantId = null, string ownerModule = "INVENTORY",
        string operationType = "STOCK_CONSEQUENCE")
        => service.CreateOrReuseReplayOperationAsync(
            CreateRequest(Guid.NewGuid(), tenantId: tenantId, ownerModule: ownerModule, operationType: operationType));

    public ProcessingFailureUpdate CreateFailure(Guid sourceEventId)
        => new(TenantA, "INVENTORY", "STOCK_CONSEQUENCE", ProcessingFailureClassifications.Transient,
            sourceEventId, $"cause-{sourceEventId:N}", $"corr-{sourceEventId:N}", "GOODS_RECEIPT",
            Guid.NewGuid(), "OPERATIONS.TEST.FAILURE", """{"reasonCode":"TEST_FAILURE","retryCount":1}""",
            ProcessingFailureStates.RetryPending, Replayable: true);

    public WorkerHeartbeatUpdate CreateHeartbeat(
        string workerCode, DateTimeOffset observedAt, int pendingCount,
        long sequence = 1, Guid? observationId = null)
        => new(TenantA, workerCode, observationId ?? Guid.NewGuid(), sequence,
            observedAt, observedAt, null, Guid.NewGuid(), pendingCount, 0, 0,
            pendingCount > 0 ? observedAt : null, null);

    public async Task<bool> HasActiveAttemptPartialUniqueIndexAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select count(*) = 1 from pg_indexes
            where schemaname='operations' and tablename='operation_attempts'
              and indexdef like '%UNIQUE%' and indexdef like '%WHERE%'
              and indexdef like '%Status%' and indexdef like '%RUNNING%';
            """, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<OperationsRlsState>> GetRlsStatesAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select c.relname,c.relrowsecurity,c.relforcerowsecurity,
              (select count(*) from pg_policies p where p.schemaname='operations' and p.tablename=c.relname and p.policyname='tenant_isolation')
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            where n.nspname='operations' and c.relname in
              ('operations','operation_attempts','operation_checkpoints','worker_heartbeats','processing_failure_projections')
            order by c.relname;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<OperationsRlsState>();
        while (await reader.ReadAsync())
            result.Add(new OperationsRlsState(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt64(3)));
        return result;
    }

    public async Task<OperationsRuntimeRoleState> GetRuntimeRoleStateAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select rolcanlogin, rolsuper, rolbypassrls from pg_roles where rolname=@role;", connection);
        command.Parameters.AddWithValue("role", RlsTestRole);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Durable Operations RLS test role was not created.");
        return new OperationsRuntimeRoleState(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }

    public async Task AttemptCrossTenantInsertAsRuntimeRoleAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"SET ROLE {RlsTestRole}; select set_config('app.tenant_id','{TenantA}',false);");
        await using var command = new NpgsqlCommand("""
            insert into operations.operations
              ("Id","TenantId","ReplayRequestKey","OperationType","OwnerModule","Status","OriginalSourceEventId",
               "CorrelationId","CreatedAtUtc","Replayable","Version")
            values (@id,@tenant,@request,'FORGED','TEST','QUEUED',@source,'forged',@now,true,1);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", TenantB);
        command.Parameters.AddWithValue("request", $"forged-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("source", Guid.NewGuid());
        command.Parameters.AddWithValue("now", TimeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class ControlledReplayHandler(
    Func<int, ReplayDispatchCommand, ReplayDispatchResult> behavior) : IReplayOwnerHandler
{
    public string OwnerModule => "INVENTORY";
    public string OperationType => "STOCK_CONSEQUENCE";
    public int DispatchCount { get; private set; }
    public List<ReplayDispatchCommand> Commands { get; } = [];

    public Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
    {
        DispatchCount++;
        Commands.Add(command);
        return Task.FromResult(behavior(DispatchCount, command));
    }
}

public sealed class IdempotentReplayHandler : IReplayOwnerHandler
{
    private readonly Dictionary<Guid, Guid> effects = [];
    public string OwnerModule => "INVENTORY";
    public string OperationType => "STOCK_CONSEQUENCE";
    public int AuthoritativeEffectCount => effects.Count;

    public Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
    {
        if (!effects.TryGetValue(command.OriginalSourceEventId, out var resultId))
        {
            resultId = Guid.NewGuid();
            effects.Add(command.OriginalSourceEventId, resultId);
        }
        return Task.FromResult(new ReplayDispatchResult(
            true, "CONTROLLED_EFFECT", resultId, SafeDetailJson: """{"result":"IDEMPOTENT"}"""));
    }
}

public sealed class CancellingReplayHandler : IReplayOwnerHandler
{
    public string OwnerModule => "INVENTORY";
    public string OperationType => "STOCK_CONSEQUENCE";
    public Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
        => Task.FromException<ReplayDispatchResult>(new OperationCanceledException("Controlled cancellation."));
}

public sealed class InterruptAfterEffectReplayHandler : IReplayOwnerHandler
{
    private readonly Dictionary<Guid, Guid> effects = [];
    public string OwnerModule => "INVENTORY";
    public string OperationType => "STOCK_CONSEQUENCE";
    public int DispatchCount { get; private set; }
    public int AuthoritativeEffectCount => effects.Count;

    public Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command, CancellationToken cancellationToken = default)
    {
        DispatchCount++;
        if (!effects.TryGetValue(command.OriginalSourceEventId, out var resultId))
        {
            resultId = Guid.NewGuid();
            effects.Add(command.OriginalSourceEventId, resultId);
        }
        if (DispatchCount == 1)
            return Task.FromException<ReplayDispatchResult>(
                new OperationCanceledException("Host interrupted after authoritative effect."));
        return Task.FromResult(new ReplayDispatchResult(
            true, "CONTROLLED_EFFECT", resultId,
            SafeDetailJson: """{"result":"IDEMPOTENT"}"""));
    }
}

public sealed record HeartbeatOutcome(WorkerHeartbeat? Heartbeat, DurableOperationRuleException? Error);
public sealed record AttemptOutcome(OperationAttempt? Attempt, DurableOperationRuleException? Error);
public sealed record OperationOutcome(Operation? Operation, DurableOperationRuleException? Error);
public sealed record OperationsRlsState(string Table, bool Enabled, bool Forced, long PolicyCount);
public sealed record OperationsRuntimeRoleState(bool CanLogin, bool IsSuperuser, bool BypassesRls);
