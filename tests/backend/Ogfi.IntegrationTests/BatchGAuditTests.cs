using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Audit;
using Ogfi.Modules.Audit.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class BatchGAuditTests(BatchGAuditFixture fixture) : IClassFixture<BatchGAuditFixture>
{
    [Fact]
    public async Task Material_evidence_is_idempotent_and_append_only()
    {
        var purchaseOrderId = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        var message = fixture.CreateMessage(
            purchaseOrderId,
            sourceEventId: sourceEventId,
            safeEvidenceJson: """{"status":"SUBMITTED","lineCount":1}""");

        await using (var db = fixture.CreateContext(BatchGAuditFixture.TenantA))
        {
            var service = new AuditIngestionService(db, fixture.TimeProvider);
            var first = await service.IngestAsync(message);
            var replay = await service.IngestAsync(message);
            Assert.Equal(first.Id, replay.Id);
            Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.SourceEventId == sourceEventId));

        }

        var mutation = await Assert.ThrowsAsync<PostgresException>(() => fixture.AttemptAuditEventUpdateAsync(sourceEventId));
        Assert.Equal("55000", mutation.SqlState);
    }

    [Fact]
    public async Task Equivalent_concurrent_delivery_returns_one_logical_event()
    {
        var purchaseOrderId = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        var firstMessage = fixture.CreateMessage(purchaseOrderId, sourceEventId, """{"status":"SUBMITTED"}""");
        var secondMessage = firstMessage with { EventId = Guid.NewGuid() };
        var barrier = new ConcurrentSaveBarrier(2);

        await using var firstDb = fixture.CreateContext(BatchGAuditFixture.TenantA, barrier);
        await using var secondDb = fixture.CreateContext(BatchGAuditFixture.TenantA, barrier);
        var results = await Task.WhenAll(
            new AuditIngestionService(firstDb, fixture.TimeProvider).IngestAsync(firstMessage),
            new AuditIngestionService(secondDb, fixture.TimeProvider).IngestAsync(secondMessage));

        Assert.Equal(results[0].Id, results[1].Id);
        await using var verificationDb = fixture.CreateContext(BatchGAuditFixture.TenantA);
        Assert.Equal(1, await verificationDb.AuditEvents.CountAsync(x => x.SourceEventId == sourceEventId));
    }

    [Fact]
    public async Task Conflicting_concurrent_delivery_leaves_one_event_and_returns_stable_conflict()
    {
        var purchaseOrderId = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        var firstMessage = fixture.CreateMessage(
            purchaseOrderId, sourceEventId, """{"status":"SUBMITTED"}""", resourceId: Guid.NewGuid());
        var secondMessage = fixture.CreateMessage(
            purchaseOrderId, sourceEventId, """{"status":"APPROVED"}""", resourceId: Guid.NewGuid());
        var barrier = new ConcurrentSaveBarrier(2);

        await using var firstDb = fixture.CreateContext(BatchGAuditFixture.TenantA, barrier);
        await using var secondDb = fixture.CreateContext(BatchGAuditFixture.TenantA, barrier);
        var outcomes = await Task.WhenAll(
            CaptureAsync(new AuditIngestionService(firstDb, fixture.TimeProvider).IngestAsync(firstMessage)),
            CaptureAsync(new AuditIngestionService(secondDb, fixture.TimeProvider).IngestAsync(secondMessage)));

        Assert.Single(outcomes, x => x.Event is not null);
        var conflict = Assert.Single(outcomes, x => x.Error is not null).Error!;
        Assert.Equal("AUDIT.INGESTION.IDENTITY_CONFLICT", conflict.Code);
        await using var verificationDb = fixture.CreateContext(BatchGAuditFixture.TenantA);
        Assert.Equal(1, await verificationDb.AuditEvents.CountAsync(x => x.SourceEventId == sourceEventId));
    }

    public static TheoryData<string, string> RejectedEvidence => new()
    {
        { """{"authToken":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"accessToken":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"refreshToken":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"sessionCookie":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"passwordHash":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"clientSecret":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"apiKey":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"authorization":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"credential":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"jwt":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"bearerToken":"x"}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { """{"result":{"accessToken":"x"}}""", "AUDIT.EVIDENCE.FIELD_NOT_ALLOWED" },
        { $$"""{"status":"{{new string('x', 2_001)}}"}""", "AUDIT.EVIDENCE.TOO_LARGE" },
        { $$"""{"status":"{{new string('x', 16_384)}}"}""", "AUDIT.EVIDENCE.TOO_LARGE" }
    };

    [Theory]
    [MemberData(nameof(RejectedEvidence))]
    public async Task Safe_evidence_rejects_non_allowlisted_and_oversized_content(string evidence, string expectedCode)
    {
        await using var db = fixture.CreateContext(BatchGAuditFixture.TenantA);
        var message = fixture.CreateMessage(Guid.NewGuid(), Guid.NewGuid(), evidence);
        var exception = await Assert.ThrowsAsync<AuditRuleException>(
            () => new AuditIngestionService(db, fixture.TimeProvider).IngestAsync(message));
        Assert.Equal(expectedCode, exception.Code);
    }

    [Theory]
    [InlineData(AuditActorTypes.Human)]
    [InlineData(AuditActorTypes.System)]
    [InlineData(AuditActorTypes.Integration)]
    public async Task Approved_actor_vocabulary_is_accepted(string actorType)
    {
        await fixture.IngestAsync(fixture.CreateMessage(
            Guid.NewGuid(), Guid.NewGuid(), actorType: actorType));
    }

    [Fact]
    public async Task Support_elevation_actor_is_reserved_until_controlled_action_exists()
    {
        await using var db = fixture.CreateContext(BatchGAuditFixture.TenantA);
        var exception = await Assert.ThrowsAsync<AuditRuleException>(() =>
            new AuditIngestionService(db, fixture.TimeProvider).IngestAsync(
                fixture.CreateMessage(Guid.NewGuid(), Guid.NewGuid(), actorType: AuditActorTypes.SupportElevation)));
        Assert.Equal("AUDIT.INGESTION.SUPPORT_ELEVATION_RESERVED", exception.Code);
    }

    [Theory]
    [InlineData("USER")]
    [InlineData("WORKER")]
    public async Task Legacy_actor_vocabulary_is_rejected(string actorType)
    {
        await using var db = fixture.CreateContext(BatchGAuditFixture.TenantA);
        var exception = await Assert.ThrowsAsync<AuditRuleException>(() =>
            new AuditIngestionService(db, fixture.TimeProvider).IngestAsync(
                fixture.CreateMessage(Guid.NewGuid(), Guid.NewGuid(), actorType: actorType)));
        Assert.Equal("AUDIT.INGESTION.INVALID", exception.Code);
    }

    [Fact]
    public async Task Rs01_trace_rebuild_surfaces_complete_incomplete_and_invalid_evidence()
    {
        var completePo = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        var journal = Guid.NewGuid();
        var workflowInstance = Guid.NewGuid();
        var approvalTask = Guid.NewGuid();
        var approvalDecision = Guid.NewGuid();
        var movement = Guid.NewGuid();
        var financeSource = Guid.NewGuid();
        await fixture.IngestAsync(fixture.CreateMessage(completePo, Guid.NewGuid(), """{"status":"SUBMITTED"}""",
            action: Rs01MaterialStages.PurchaseOrderSubmission));
        await fixture.IngestAsync(fixture.CreateMessage(completePo, Guid.NewGuid(), """{"decision":"APPROVED"}""",
            sourceModule: Rs01MaterialStages.WorkflowOwner, action: Rs01MaterialStages.WorkflowApprovalDecision,
            workflowInstanceId: workflowInstance, approvalTaskId: approvalTask, approvalDecisionId: approvalDecision));
        await fixture.IngestAsync(fixture.CreateMessage(completePo, Guid.NewGuid(), """{"status":"APPROVED"}""",
            action: Rs01MaterialStages.ProcurementApprovalApplication, approvalDecisionId: approvalDecision));
        await fixture.IngestAsync(fixture.CreateMessage(completePo, Guid.NewGuid(), """{"status":"POSTED"}""",
            action: Rs01MaterialStages.GoodsReceiptPosting, goodsReceiptId: receipt));
        await fixture.IngestAsync(fixture.CreateMessage(completePo, Guid.NewGuid(), """{"movementType":"RECEIPT"}""",
            sourceModule: Rs01MaterialStages.InventoryOwner, action: Rs01MaterialStages.InventoryMovementCreation,
            goodsReceiptId: receipt, inventoryMovementId: movement));
        await fixture.IngestAsync(fixture.CreateMessage(completePo, Guid.NewGuid(), """{"postingStatus":"POSTED"}""",
            sourceModule: Rs01MaterialStages.FinanceOwner, action: Rs01MaterialStages.FinanceJournalPosting,
            goodsReceiptId: receipt, financeSourcePostingId: financeSource, journalId: journal));

        await using (var db = fixture.CreateContext(BatchGAuditFixture.TenantA))
        {
            var service = new Rs01TraceProjectionService(db, fixture.TimeProvider);
            var traces = await service.RebuildAsync(BatchGAuditFixture.TenantA, completePo);
            var trace = Assert.Single(traces);
            Assert.Equal(Rs01TraceStates.Complete, trace.State);
            Assert.Equal(receipt, trace.GoodsReceiptId);
            Assert.Equal(journal, trace.JournalId);
            Assert.Equal(1, trace.InventoryMovementCount);

            var query = new AuditQueryService(db);
            Assert.Single(await query.QueryTracesAsync(
                BatchGAuditFixture.TenantA,
                new Rs01TraceQuery(JournalId: journal, Limit: 10)));
            await Assert.ThrowsAsync<AuditRuleException>(() => query.QueryEventsAsync(
                BatchGAuditFixture.TenantA,
                new AuditEventQuery(Limit: 101)));
        }

        await using (var failingDb = fixture.CreateContext(BatchGAuditFixture.TenantA, new FailingSaveInterceptor()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new Rs01TraceProjectionService(failingDb, fixture.TimeProvider)
                    .RebuildAsync(BatchGAuditFixture.TenantA, completePo));
        }
        await using (var verificationDb = fixture.CreateContext(BatchGAuditFixture.TenantA))
        {
            var preserved = Assert.Single(await verificationDb.Rs01TraceProjections
                .Where(x => x.PurchaseOrderId == completePo).ToListAsync());
            Assert.Equal(Rs01TraceStates.Complete, preserved.State);
        }

        var incompletePo = Guid.NewGuid();
        await fixture.IngestAsync(fixture.CreateMessage(
            incompletePo, Guid.NewGuid(), action: Rs01MaterialStages.PurchaseOrderSubmission,
            workflowInstanceId: Guid.NewGuid(), approvalTaskId: Guid.NewGuid(), approvalDecisionId: Guid.NewGuid(),
            goodsReceiptId: Guid.NewGuid(), inventoryMovementId: Guid.NewGuid(),
            financeSourcePostingId: Guid.NewGuid(), journalId: Guid.NewGuid()));
        await using (var db = fixture.CreateContext(BatchGAuditFixture.TenantA))
        {
            var trace = Assert.Single(await new Rs01TraceProjectionService(db, fixture.TimeProvider)
                .RebuildAsync(BatchGAuditFixture.TenantA, incompletePo));
            Assert.Equal(Rs01TraceStates.Incomplete, trace.State);
            Assert.Contains("GOODS_RECEIPT_POSTING", trace.MissingLinksJson);
        }

        var ownershipConflictPo = Guid.NewGuid();
        await fixture.IngestAsync(fixture.CreateMessage(
            ownershipConflictPo, Guid.NewGuid(), action: Rs01MaterialStages.PurchaseOrderSubmission));
        await fixture.IngestAsync(fixture.CreateMessage(
            ownershipConflictPo, Guid.NewGuid(), action: Rs01MaterialStages.WorkflowApprovalDecision,
            sourceModule: Rs01MaterialStages.ProcurementOwner));
        await fixture.IngestAsync(fixture.CreateMessage(
            ownershipConflictPo, Guid.NewGuid(), action: Rs01MaterialStages.WorkflowApprovalDecision,
            sourceModule: Rs01MaterialStages.WorkflowOwner, outcome: AuditOutcomes.Failed,
            errorCode: "WORKFLOW.REJECTED"));
        await using (var db = fixture.CreateContext(BatchGAuditFixture.TenantA))
        {
            var trace = Assert.Single(await new Rs01TraceProjectionService(db, fixture.TimeProvider)
                .RebuildAsync(BatchGAuditFixture.TenantA, ownershipConflictPo));
            Assert.Equal(Rs01TraceStates.Invalid, trace.State);
            Assert.Contains("non-owning module", trace.InvalidReason);
            Assert.Contains("non-successful outcome", trace.InvalidReason);
        }

        await fixture.IngestAsync(fixture.CreateMessage(
            completePo,
            sourceEventId: Guid.NewGuid(),
            goodsReceiptId: receipt,
            sourceModule: Rs01MaterialStages.FinanceOwner,
            financeSourcePostingId: financeSource,
            journalId: Guid.NewGuid(),
            action: Rs01MaterialStages.FinanceJournalPosting));
        await using (var db = fixture.CreateContext(BatchGAuditFixture.TenantA))
        {
            var trace = Assert.Single(await new Rs01TraceProjectionService(db, fixture.TimeProvider)
                .RebuildAsync(BatchGAuditFixture.TenantA, completePo));
            Assert.Equal(Rs01TraceStates.Invalid, trace.State);
            Assert.NotNull(trace.InvalidReason);
        }
    }

    private static async Task<IngestionOutcome> CaptureAsync(Task<AuditEvent> operation)
    {
        try
        {
            return new(await operation, null);
        }
        catch (AuditRuleException exception)
        {
            return new(null, exception);
        }
    }

    [Fact]
    public async Task Audit_tables_are_force_rls_protected_and_model_snapshot_is_current()
    {
        var states = await fixture.GetRlsStatesAsync();
        Assert.Equal(2, states.Count);
        Assert.All(states, state =>
        {
            Assert.True(state.Enabled, $"RLS is not enabled for audit.{state.Table}");
            Assert.True(state.Forced, $"RLS is not forced for audit.{state.Table}");
            Assert.Equal(1, state.PolicyCount);
        });

        var exception = await Assert.ThrowsAsync<PostgresException>(fixture.AttemptCrossTenantInsertAsRuntimeRoleAsync);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);

        await using var db = fixture.CreateContext(BatchGAuditFixture.TenantA);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}

public sealed class BatchGAuditFixture : IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("a7000000-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("b7000000-0000-0000-0000-000000000001");
    private const string RlsTestRole = "ogfi_batch_g_audit_rls_test";
    private readonly string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required for Batch G Audit integration evidence.");

    public TimeProvider TimeProvider { get; } = new FixedTimeProvider(new DateTimeOffset(2026, 8, 18, 6, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await using (var db = CreateContext(TenantA)) await db.Database.MigrateAsync();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $$"""
            DO $$ BEGIN
                CREATE ROLE {{RlsTestRole}} NOLOGIN;
            EXCEPTION WHEN duplicate_object THEN NULL;
            END $$;
            GRANT USAGE ON SCHEMA audit TO {{RlsTestRole}};
            GRANT SELECT, INSERT ON audit.audit_events, audit.rs01_trace_projections TO {{RlsTestRole}};
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public AuditDbContext CreateContext(Guid tenantId, params IInterceptor[] additionalInterceptors)
    {
        var executionContext = new TenantExecutionContextAccessor();
        executionContext.SetCandidateTenant(tenantId);
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(connectionString);
        options.AddInterceptors(new TenantSessionConnectionInterceptor(executionContext));
        options.AddInterceptors(additionalInterceptors);
        return new AuditDbContext(options.Options);
    }

    public async Task IngestAsync(AuditMaterialActionRecordedV1 message)
    {
        await using var db = CreateContext(message.TenantId);
        await new AuditIngestionService(db, TimeProvider).IngestAsync(message);
    }

    public AuditMaterialActionRecordedV1 CreateMessage(
        Guid purchaseOrderId,
        Guid sourceEventId,
        string safeEvidenceJson = "{}",
        Guid? workflowInstanceId = null,
        Guid? approvalTaskId = null,
        Guid? approvalDecisionId = null,
        Guid? goodsReceiptId = null,
        Guid? inventoryMovementId = null,
        Guid? financeSourcePostingId = null,
        Guid? journalId = null,
        string action = "PURCHASE_ORDER.MATERIAL_ACTION",
        string sourceModule = Rs01MaterialStages.ProcurementOwner,
        Guid? resourceId = null,
        string actorType = AuditActorTypes.Integration,
        string outcome = AuditOutcomes.Succeeded,
        string? errorCode = null)
        => new(
            Guid.NewGuid(), TenantA, actorType, null, null, action, sourceModule,
            "PURCHASE_ORDER", resourceId ?? purchaseOrderId, 1, null, null, new DateOnly(2026, 8, 18),
            TimeProvider.GetUtcNow(), outcome, errorCode, $"corr-{Guid.NewGuid():N}"[..32],
            $"cause-{sourceEventId:N}", sourceEventId, safeEvidenceJson, purchaseOrderId,
            workflowInstanceId, approvalTaskId, approvalDecisionId, goodsReceiptId, inventoryMovementId,
            financeSourcePostingId, journalId);

    public async Task AttemptAuditEventUpdateAsync(Guid sourceEventId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "update audit.audit_events set \"Outcome\"='FAILED' where \"SourceEventId\"=@event;", connection);
        command.Parameters.AddWithValue("event", sourceEventId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<AuditRlsState>> GetRlsStatesAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select c.relname,c.relrowsecurity,c.relforcerowsecurity,
              (select count(*) from pg_policies p where p.schemaname='audit' and p.tablename=c.relname and p.policyname='tenant_isolation')
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            where n.nspname='audit' and c.relname in ('audit_events','rs01_trace_projections')
            order by c.relname;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<AuditRlsState>();
        while (await reader.ReadAsync()) result.Add(new AuditRlsState(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetInt64(3)));
        return result;
    }

    public async Task AttemptCrossTenantInsertAsRuntimeRoleAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"SET ROLE {RlsTestRole}; select set_config('app.tenant_id','{TenantA}',false);");
        await using var command = new NpgsqlCommand("""
            insert into audit.audit_events
              ("Id","TenantId","ActorType","Action","SourceModule","ResourceType","ResourceId","OccurredAtUtc","Outcome","CorrelationId","SafeEvidenceJson","CreatedAtUtc")
            values (@id,@tenant,'INTEGRATION','FORGED.WRITE','TEST','PURCHASE_ORDER',@resource,@now,'REJECTED','forged','{}'::jsonb,@now);
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("tenant", TenantB);
        command.Parameters.AddWithValue("resource", Guid.NewGuid());
        command.Parameters.AddWithValue("now", TimeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record AuditRlsState(string Table, bool Enabled, bool Forced, long PolicyCount);

public sealed record IngestionOutcome(AuditEvent? Event, AuditRuleException? Error);

public sealed class ConcurrentSaveBarrier(int participantCount) : SaveChangesInterceptor
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrivals;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref arrivals) == participantCount) completion.TrySetResult();
        await completion.Task.WaitAsync(cancellationToken);
        return result;
    }
}

public sealed class FailingSaveInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<InterceptionResult<int>>(
            new InvalidOperationException("Injected projection save failure."));
}
