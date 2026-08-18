using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.DurableOperations;
using Ogfi.Modules.DurableOperations.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchGDurableOperationsTests(BatchGDurableOperationsFixture fixture)
    : IClassFixture<BatchGDurableOperationsFixture>
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
        Assert.NotNull(cancelled.CancelRequestedAtUtc);

        var invalid = await fixture.CreateOperationAsync(service);
        var backward = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.TransitionAsync(invalid.TenantId, invalid.Id, 1, OperationStatuses.Succeeded));
        Assert.Equal("OPERATIONS.TRANSITION.INVALID", backward.Code);
    }

    [Fact]
    public async Task Replay_identity_reuses_equivalent_operation_and_rejects_conflicting_metadata()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var source = Guid.NewGuid();
        var request = fixture.CreateRequest(source, "corr-equivalent");
        var first = await service.CreateOrReuseReplayOperationAsync(request);
        var replay = await service.CreateOrReuseReplayOperationAsync(request);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(1, await db.Operations.CountAsync(x => x.OriginalSourceEventId == source));

        var conflict = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.CreateOrReuseReplayOperationAsync(request with { CorrelationId = "corr-conflict" }));
        Assert.Equal("OPERATIONS.REPLAY.IDENTITY_CONFLICT", conflict.Code);
    }

    [Fact]
    public async Task Attempts_and_checkpoints_are_monotonic_unique_and_bounded()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var operation = await fixture.CreateOperationAsync(service);
        var first = await service.StartAttemptAsync(operation.TenantId, operation.Id, "worker-a");
        await service.CompleteAttemptAsync(operation.TenantId, first.Id, succeeded: false,
            "OPERATIONS.TEST.TRANSIENT", """{"retryCount":1}""");
        var second = await service.StartAttemptAsync(operation.TenantId, operation.Id, "worker-a");
        var attemptNumbers = await db.OperationAttempts.Where(x => x.OperationId == operation.Id)
            .OrderBy(x => x.AttemptNumber).Select(x => x.AttemptNumber).ToArrayAsync();
        Assert.Equal(new[] { 1, 2 }, attemptNumbers);

        await service.AddCheckpointAsync(operation.TenantId, operation.Id, 1, "LOADED", 25,
            """{"progress":25}""");
        await service.AddCheckpointAsync(operation.TenantId, operation.Id, 2, "VALIDATED", 75,
            """{"progress":75}""");
        var checkpointSequences = await db.OperationCheckpoints.Where(x => x.OperationId == operation.Id)
            .OrderBy(x => x.Sequence).Select(x => x.Sequence).ToArrayAsync();
        Assert.Equal(new[] { 1, 2 }, checkpointSequences);

        var sequence = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.AddCheckpointAsync(operation.TenantId, operation.Id, 4, "SKIPPED", 90));
        Assert.Equal("OPERATIONS.CHECKPOINT.SEQUENCE_INVALID", sequence.Code);
        var progress = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            service.AddCheckpointAsync(operation.TenantId, operation.Id, 3, "INVALID", 101));
        Assert.Equal("OPERATIONS.CHECKPOINT.PROGRESS_INVALID", progress.Code);
        Assert.Equal(OperationAttemptStatuses.Running, second.Status);
    }

    [Fact]
    public async Task Worker_heartbeat_upserts_one_current_projection()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var first = new WorkerHeartbeatUpdate(
            BatchGDurableOperationsFixture.TenantA, "stock-worker", fixture.TimeProvider.GetUtcNow(),
            null, null, Guid.NewGuid(), 5, 2, 1, fixture.TimeProvider.GetUtcNow(), "TRANSIENT");
        var inserted = await service.UpsertHeartbeatAsync(first);
        var updated = await service.UpsertHeartbeatAsync(first with
        {
            PendingCount = 1, RetryPendingCount = 0, LastSucceededAtUtc = fixture.TimeProvider.GetUtcNow()
        });

        Assert.Equal(inserted.Id, updated.Id);
        Assert.Equal(1, await db.WorkerHeartbeats.CountAsync(x => x.WorkerCode == "STOCK-WORKER"));
        Assert.Equal(1, updated.PendingCount);
        Assert.Equal(0, updated.RetryPendingCount);
    }

    [Fact]
    public async Task Failure_projection_normalizes_state_attempts_and_terminal_replayability()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var source = Guid.NewGuid();
        var transient = fixture.CreateFailure(source, ProcessingFailureClassifications.Transient,
            ProcessingFailureStates.RetryPending, replayable: true);
        var first = await service.RecordFailureAsync(transient);
        var second = await service.RecordFailureAsync(transient);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.AttemptCount);
        Assert.True(second.Replayable);
        Assert.Equal(ProcessingFailureStates.RetryPending, second.State);

        foreach (var classification in new[]
                 {
                     ProcessingFailureClassifications.ForgedTenant,
                     ProcessingFailureClassifications.MalformedContract,
                     ProcessingFailureClassifications.Authorization,
                     ProcessingFailureClassifications.SecurityTerminal
                 })
        {
            var terminal = await service.RecordFailureAsync(fixture.CreateFailure(
                Guid.NewGuid(), classification, ProcessingFailureStates.Pending, replayable: true,
                currentOperationId: Guid.NewGuid()));
            Assert.False(terminal.Replayable);
            Assert.Null(terminal.CurrentOperationId);
            Assert.Equal(ProcessingFailureStates.TerminalRejected, terminal.State);
        }
    }

    [Fact]
    public async Task Replay_coordinator_delegates_neutrally_preserves_identity_and_dispatches_once()
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var handler = new RecordingReplayHandler();
        var coordinator = new ReplayCoordinator(fixture.CreateService(db), [handler]);
        var request = new ReplayRequest(
            BatchGDurableOperationsFixture.TenantA, handler.OperationType, handler.OwnerModule,
            ProcessingFailureClassifications.Transient, true, Guid.NewGuid(), "cause-original",
            "corr-original", Guid.NewGuid(), Guid.NewGuid());

        var completed = await coordinator.ReplayAsync(request);
        var reused = await coordinator.ReplayAsync(request);
        Assert.Equal(completed.Id, reused.Id);
        Assert.Equal(OperationStatuses.Succeeded, completed.Status);
        Assert.Equal(1, handler.DispatchCount);
        Assert.NotNull(handler.Command);
        Assert.Equal(request.OriginalSourceEventId, handler.Command.OriginalSourceEventId);
        Assert.Equal(request.OriginalCausationId, handler.Command.OriginalCausationId);
        Assert.Equal(request.CorrelationId, handler.Command.CorrelationId);

        var terminal = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            coordinator.ReplayAsync(request with { FailureClassification = ProcessingFailureClassifications.ForgedTenant }));
        Assert.Equal("OPERATIONS.REPLAY.NOT_ALLOWED", terminal.Code);
        var nonReplayable = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            coordinator.ReplayAsync(request with { Replayable = false }));
        Assert.Equal("OPERATIONS.REPLAY.NOT_ALLOWED", nonReplayable.Code);
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
        { """{"rawPayload":"raw"}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { """{"result":{"token":"raw"}}""", "OPERATIONS.SAFE_DETAIL.FIELD_NOT_ALLOWED" },
        { $$"""{"status":"{{new string('x', 1_001)}}"}""", "OPERATIONS.SAFE_DETAIL.TOO_LARGE" },
        { $$"""{"status":"{{new string('x', 8_192)}}"}""", "OPERATIONS.SAFE_DETAIL.TOO_LARGE" },
        { """{"result":{"result":{"result":{"result":{"result":{"result":{"result":{"result":{"result":{}}}}}}}}}}""", "OPERATIONS.SAFE_DETAIL.INVALID" }
    };

    [Theory]
    [MemberData(nameof(RejectedSafeDetails))]
    public async Task Safe_detail_policy_rejects_raw_sensitive_oversized_and_overdeep_data(
        string detail,
        string expectedCode)
    {
        await using var db = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var service = fixture.CreateService(db);
        var operation = await fixture.CreateOperationAsync(service);
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
        {
            await fixture.CreateOperationAsync(fixture.CreateService(tenantB), BatchGDurableOperationsFixture.TenantB);
        }
        await using var tenantA = fixture.CreateContext(BatchGDurableOperationsFixture.TenantA);
        var visible = await fixture.CreateService(tenantA).QueryOperationsAsync(BatchGDurableOperationsFixture.TenantA, 100);
        Assert.DoesNotContain(visible, x => x.TenantId == BatchGDurableOperationsFixture.TenantB);
        var limit = await Assert.ThrowsAsync<DurableOperationRuleException>(() =>
            fixture.CreateService(tenantA).QueryOperationsAsync(BatchGDurableOperationsFixture.TenantA, 101));
        Assert.Equal("OPERATIONS.QUERY.LIMIT_INVALID", limit.Code);
    }
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

    public DurableOperationsDbContext CreateContext(Guid tenantId)
    {
        var executionContext = new TenantExecutionContextAccessor();
        executionContext.SetCandidateTenant(tenantId);
        var options = new DbContextOptionsBuilder<DurableOperationsDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new TenantSessionConnectionInterceptor(executionContext))
            .Options;
        return new DurableOperationsDbContext(options);
    }

    public DurableOperationService CreateService(DurableOperationsDbContext dbContext)
        => new(dbContext, TimeProvider);

    public CreateReplayOperationRequest CreateRequest(Guid sourceEventId, string? correlationId = null, Guid? tenantId = null)
        => new(tenantId ?? TenantA, "REPLAY_TEST", "TEST_OWNER", sourceEventId,
            $"cause-{sourceEventId:N}", correlationId ?? $"corr-{sourceEventId:N}");

    public Task<Operation> CreateOperationAsync(DurableOperationService service, Guid? tenantId = null)
        => service.CreateOrReuseReplayOperationAsync(CreateRequest(Guid.NewGuid(), tenantId: tenantId));

    public ProcessingFailureUpdate CreateFailure(
        Guid sourceEventId,
        string classification,
        string state,
        bool replayable,
        Guid? currentOperationId = null)
        => new(TenantA, "INVENTORY", "STOCK_CONSEQUENCE", classification, sourceEventId,
            $"cause-{sourceEventId:N}", $"corr-{sourceEventId:N}", "GOODS_RECEIPT", Guid.NewGuid(),
            "OPERATIONS.TEST.FAILURE", """{"reasonCode":"TEST_FAILURE","retryCount":1}""",
            state, replayable, CurrentOperationId: currentOperationId);

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
              ("Id","TenantId","OperationType","OwnerModule","Status","OriginalSourceEventId","CorrelationId","CreatedAtUtc","Replayable","Version")
            values (@id,@tenant,'FORGED','TEST','QUEUED',@source,'forged',@now,true,1);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", TenantB);
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

public sealed class RecordingReplayHandler : IReplayOwnerHandler
{
    public string OwnerModule => "TEST_OWNER";
    public string OperationType => "REPLAY_TEST";
    public int DispatchCount { get; private set; }
    public ReplayDispatchCommand? Command { get; private set; }

    public Task<ReplayDispatchResult> ReplayAsync(
        ReplayDispatchCommand command,
        CancellationToken cancellationToken = default)
    {
        DispatchCount++;
        Command = command;
        return Task.FromResult(new ReplayDispatchResult(
            true, "TEST_RESULT", Guid.NewGuid(), SafeDetailJson: """{"result":"CONTROLLED"}"""));
    }
}

public sealed record OperationsRlsState(string Table, bool Enabled, bool Forced, long PolicyCount);
public sealed record OperationsRuntimeRoleState(bool CanLogin, bool IsSuperuser, bool BypassesRls);
