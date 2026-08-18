using Microsoft.EntityFrameworkCore;
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
    public async Task Material_evidence_is_idempotent_bounded_secret_safe_and_append_only()
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

            var unsafeMessage = fixture.CreateMessage(
                Guid.NewGuid(),
                sourceEventId: Guid.NewGuid(),
                safeEvidenceJson: """{"accessToken":"must-not-persist"}""");
            var exception = await Assert.ThrowsAsync<AuditRuleException>(() => service.IngestAsync(unsafeMessage));
            Assert.Equal("AUDIT.EVIDENCE.SECRET_REJECTED", exception.Code);
        }

        var mutation = await Assert.ThrowsAsync<PostgresException>(() => fixture.AttemptAuditEventUpdateAsync(sourceEventId));
        Assert.Equal("55000", mutation.SqlState);
    }

    [Fact]
    public async Task Rs01_trace_rebuild_surfaces_complete_incomplete_and_invalid_evidence()
    {
        var completePo = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        var journal = Guid.NewGuid();
        await fixture.IngestAsync(fixture.CreateMessage(
            completePo,
            sourceEventId: Guid.NewGuid(),
            goodsReceiptId: receipt,
            workflowInstanceId: Guid.NewGuid(),
            approvalTaskId: Guid.NewGuid(),
            approvalDecisionId: Guid.NewGuid(),
            inventoryMovementId: Guid.NewGuid(),
            financeSourcePostingId: Guid.NewGuid(),
            journalId: journal));

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

        var incompletePo = Guid.NewGuid();
        await fixture.IngestAsync(fixture.CreateMessage(incompletePo, sourceEventId: Guid.NewGuid()));
        await using (var db = fixture.CreateContext(BatchGAuditFixture.TenantA))
        {
            var trace = Assert.Single(await new Rs01TraceProjectionService(db, fixture.TimeProvider)
                .RebuildAsync(BatchGAuditFixture.TenantA, incompletePo));
            Assert.Equal(Rs01TraceStates.Incomplete, trace.State);
            Assert.Contains("GOODS_RECEIPT", trace.MissingLinksJson);
        }

        await fixture.IngestAsync(fixture.CreateMessage(
            completePo,
            sourceEventId: Guid.NewGuid(),
            goodsReceiptId: receipt,
            workflowInstanceId: Guid.NewGuid(),
            approvalTaskId: Guid.NewGuid(),
            approvalDecisionId: Guid.NewGuid(),
            inventoryMovementId: Guid.NewGuid(),
            financeSourcePostingId: Guid.NewGuid(),
            journalId: Guid.NewGuid(),
            action: "JOURNAL.CONTRADICTION.RECORDED"));
        await using (var db = fixture.CreateContext(BatchGAuditFixture.TenantA))
        {
            var trace = Assert.Single(await new Rs01TraceProjectionService(db, fixture.TimeProvider)
                .RebuildAsync(BatchGAuditFixture.TenantA, completePo));
            Assert.Equal(Rs01TraceStates.Invalid, trace.State);
            Assert.NotNull(trace.InvalidReason);
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

    public AuditDbContext CreateContext(Guid tenantId)
    {
        var executionContext = new TenantExecutionContextAccessor();
        executionContext.SetCandidateTenant(tenantId);
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(new TenantSessionConnectionInterceptor(executionContext))
            .Options;
        return new AuditDbContext(options);
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
        string action = "PURCHASE_ORDER.MATERIAL_ACTION")
        => new(
            Guid.NewGuid(), TenantA, AuditActorTypes.Worker, null, null, action, "PROCUREMENT",
            "PURCHASE_ORDER", purchaseOrderId, 1, null, null, new DateOnly(2026, 8, 18),
            TimeProvider.GetUtcNow(), AuditOutcomes.Succeeded, null, $"corr-{Guid.NewGuid():N}"[..32],
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
            values (@id,@tenant,'WORKER','FORGED.WRITE','TEST','PURCHASE_ORDER',@resource,@now,'REJECTED','forged','{}'::jsonb,@now);
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
