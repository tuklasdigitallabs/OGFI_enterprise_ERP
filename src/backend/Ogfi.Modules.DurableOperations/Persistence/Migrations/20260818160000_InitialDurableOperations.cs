using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.DurableOperations.Persistence.Migrations;

[DbContext(typeof(DurableOperationsDbContext))]
[Migration("20260818160000_InitialDurableOperations")]
public sealed class InitialDurableOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SCHEMA IF NOT EXISTS operations;

            CREATE TABLE operations.operations (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "ReplayRequestKey" varchar(128) NOT NULL,
                "OperationType" varchar(100) NOT NULL,
                "OwnerModule" varchar(60) NOT NULL,
                "Status" varchar(24) NOT NULL,
                "OriginalSourceEventId" uuid NOT NULL,
                "OriginalCausationId" varchar(128) NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "LegalEntityId" uuid NULL,
                "OutletId" uuid NULL,
                "RequestedByUserId" uuid NULL,
                "RequestedByMembershipId" uuid NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                "StartedAtUtc" timestamptz NULL,
                "CompletedAtUtc" timestamptz NULL,
                "CancelRequestedAtUtc" timestamptz NULL,
                "ResultReferenceType" varchar(100) NULL,
                "ResultReferenceId" uuid NULL,
                "SafeErrorCode" varchar(120) NULL,
                "SafeDetailJson" jsonb NULL,
                "Replayable" boolean NOT NULL,
                "Version" bigint NOT NULL,
                CONSTRAINT "AK_operations_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "CK_operations_status" CHECK ("Status" IN ('QUEUED','RUNNING','SUCCEEDED','FAILED','CANCEL_REQUESTED','CANCELLED')),
                CONSTRAINT "CK_operations_version" CHECK ("Version" > 0),
                CONSTRAINT "CK_operations_safe_detail_size" CHECK ("SafeDetailJson" IS NULL OR octet_length("SafeDetailJson"::text) <= 8192)
            );
            CREATE UNIQUE INDEX "IX_operations_TenantId_ReplayRequestKey"
                ON operations.operations ("TenantId", "ReplayRequestKey");
            CREATE INDEX "IX_operations_TenantId_OriginalSourceEventId"
                ON operations.operations ("TenantId", "OriginalSourceEventId");
            CREATE INDEX "IX_operations_TenantId_Status_CreatedAtUtc"
                ON operations.operations ("TenantId", "Status", "CreatedAtUtc");
            CREATE INDEX "IX_operations_TenantId_CorrelationId"
                ON operations.operations ("TenantId", "CorrelationId");

            CREATE TABLE operations.operation_attempts (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "OperationId" uuid NOT NULL,
                "AttemptNumber" integer NOT NULL,
                "Status" varchar(20) NOT NULL,
                "WorkerCode" varchar(100) NOT NULL,
                "LeaseOwner" varchar(100) NOT NULL,
                "LeaseToken" uuid NOT NULL,
                "LeaseAcquiredAtUtc" timestamptz NOT NULL,
                "LeaseExpiresAtUtc" timestamptz NOT NULL,
                "LastLeaseHeartbeatAtUtc" timestamptz NOT NULL,
                "StartedAtUtc" timestamptz NOT NULL,
                "CompletedAtUtc" timestamptz NULL,
                "SafeErrorCode" varchar(120) NULL,
                "SafeDetailJson" jsonb NOT NULL,
                "OriginalSourceEventId" uuid NOT NULL,
                "OriginalCausationId" varchar(128) NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "Version" bigint NOT NULL,
                CONSTRAINT "AK_operation_attempts_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_operation_attempts_operations_TenantId_OperationId"
                    FOREIGN KEY ("TenantId", "OperationId") REFERENCES operations.operations ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "CK_operation_attempt_number" CHECK ("AttemptNumber" > 0),
                CONSTRAINT "CK_operation_attempt_status" CHECK ("Status" IN ('RUNNING','SUCCEEDED','FAILED','ABANDONED')),
                CONSTRAINT "CK_operation_attempt_version" CHECK ("Version" > 0),
                CONSTRAINT "CK_operation_attempt_lease" CHECK ("LeaseExpiresAtUtc" >= "LeaseAcquiredAtUtc" AND "LastLeaseHeartbeatAtUtc" >= "LeaseAcquiredAtUtc"),
                CONSTRAINT "CK_operation_attempt_safe_detail_size" CHECK (octet_length("SafeDetailJson"::text) <= 8192)
            );
            CREATE UNIQUE INDEX "IX_operation_attempts_TenantId_OperationId_AttemptNumber"
                ON operations.operation_attempts ("TenantId", "OperationId", "AttemptNumber");
            CREATE UNIQUE INDEX "IX_operation_attempts_TenantId_OperationId_running"
                ON operations.operation_attempts ("TenantId", "OperationId") WHERE "Status" = 'RUNNING';

            CREATE TABLE operations.operation_checkpoints (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "OperationId" uuid NOT NULL,
                "Sequence" integer NOT NULL,
                "CheckpointKey" varchar(100) NOT NULL,
                "ProgressPercentage" integer NOT NULL,
                "SafeDetailJson" jsonb NOT NULL,
                "OccurredAtUtc" timestamptz NOT NULL,
                CONSTRAINT "AK_operation_checkpoints_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_operation_checkpoints_operations_TenantId_OperationId"
                    FOREIGN KEY ("TenantId", "OperationId") REFERENCES operations.operations ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "CK_operation_checkpoint_sequence" CHECK ("Sequence" > 0),
                CONSTRAINT "CK_operation_checkpoint_progress" CHECK ("ProgressPercentage" BETWEEN 0 AND 100),
                CONSTRAINT "CK_operation_checkpoint_safe_detail_size" CHECK (octet_length("SafeDetailJson"::text) <= 8192)
            );
            CREATE UNIQUE INDEX "IX_operation_checkpoints_TenantId_OperationId_Sequence"
                ON operations.operation_checkpoints ("TenantId", "OperationId", "Sequence");

            CREATE TABLE operations.worker_heartbeats (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "WorkerCode" varchar(100) NOT NULL,
                "ObservationId" uuid NOT NULL,
                "ObservationSequence" bigint NOT NULL,
                "LastIterationStartedAtUtc" timestamptz NOT NULL,
                "LastSucceededAtUtc" timestamptz NULL,
                "LastFailedAtUtc" timestamptz NULL,
                "CurrentOrLastSourceId" uuid NULL,
                "PendingCount" integer NOT NULL,
                "RetryPendingCount" integer NOT NULL,
                "TerminalFailureCount" integer NOT NULL,
                "OldestPendingAtUtc" timestamptz NULL,
                "LastSafeErrorCode" varchar(120) NULL,
                "UpdatedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "AK_worker_heartbeats_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "CK_worker_heartbeat_counts" CHECK ("PendingCount" >= 0 AND "RetryPendingCount" >= 0 AND "TerminalFailureCount" >= 0),
                CONSTRAINT "CK_worker_heartbeat_observation_sequence" CHECK ("ObservationSequence" > 0)
            );
            CREATE UNIQUE INDEX "IX_worker_heartbeats_TenantId_WorkerCode"
                ON operations.worker_heartbeats ("TenantId", "WorkerCode");
            CREATE INDEX "IX_worker_heartbeats_TenantId_UpdatedAtUtc"
                ON operations.worker_heartbeats ("TenantId", "UpdatedAtUtc");

            CREATE TABLE operations.processing_failure_projections (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "OwnerModule" varchar(60) NOT NULL,
                "ProcessorCode" varchar(100) NOT NULL,
                "FailureClassification" varchar(40) NOT NULL,
                "OriginalSourceEventId" uuid NOT NULL,
                "OriginalCausationId" varchar(128) NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "ResourceType" varchar(100) NOT NULL,
                "ResourceId" uuid NOT NULL,
                "LegalEntityId" uuid NULL,
                "OutletId" uuid NULL,
                "AttemptCount" integer NOT NULL,
                "FirstFailedAtUtc" timestamptz NOT NULL,
                "LastFailedAtUtc" timestamptz NOT NULL,
                "SafeErrorCode" varchar(120) NOT NULL,
                "SafeDetailJson" jsonb NOT NULL,
                "Replayable" boolean NOT NULL,
                "CurrentOperationId" uuid NULL,
                "RecoveryOperationId" uuid NULL,
                "State" varchar(24) NOT NULL,
                "Version" bigint NOT NULL,
                CONSTRAINT "AK_processing_failure_projections_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_processing_failure_projections_operations_TenantId_CurrentOperationId"
                    FOREIGN KEY ("TenantId", "CurrentOperationId") REFERENCES operations.operations ("TenantId", "Id") ON DELETE NO ACTION,
                CONSTRAINT "FK_processing_failure_projections_operations_TenantId_RecoveryOperationId"
                    FOREIGN KEY ("TenantId", "RecoveryOperationId") REFERENCES operations.operations ("TenantId", "Id") ON DELETE NO ACTION,
                CONSTRAINT "CK_processing_failure_attempts" CHECK ("AttemptCount" > 0),
                CONSTRAINT "CK_processing_failure_state" CHECK ("State" IN ('PENDING','RETRY_PENDING','BUSINESS_FAILED','TERMINAL_REJECTED','STALLED','RECOVERED','COMPLETED')),
                CONSTRAINT "CK_processing_failure_classification" CHECK ("FailureClassification" IN ('TRANSIENT','BUSINESS','FORGED_TENANT','MALFORMED_CONTRACT','AUTHORIZATION','SECURITY_TERMINAL')),
                CONSTRAINT "CK_processing_failure_version" CHECK ("Version" > 0),
                CONSTRAINT "CK_processing_failure_safe_detail_size" CHECK (octet_length("SafeDetailJson"::text) <= 8192)
            );
            CREATE UNIQUE INDEX "IX_processing_failure_projections_TenantId_OwnerModule_ProcessorCode_OriginalSourceEventId"
                ON operations.processing_failure_projections ("TenantId", "OwnerModule", "ProcessorCode", "OriginalSourceEventId");
            CREATE INDEX "IX_processing_failure_projections_TenantId_State_LastFailedAtUtc"
                ON operations.processing_failure_projections ("TenantId", "State", "LastFailedAtUtc");
            CREATE INDEX "IX_processing_failure_projections_TenantId_CurrentOperationId"
                ON operations.processing_failure_projections ("TenantId", "CurrentOperationId");
            CREATE INDEX "IX_processing_failure_projections_TenantId_RecoveryOperationId"
                ON operations.processing_failure_projections ("TenantId", "RecoveryOperationId");

            ALTER TABLE operations.operations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE operations.operations FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON operations.operations
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);

            ALTER TABLE operations.operation_attempts ENABLE ROW LEVEL SECURITY;
            ALTER TABLE operations.operation_attempts FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON operations.operation_attempts
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);

            ALTER TABLE operations.operation_checkpoints ENABLE ROW LEVEL SECURITY;
            ALTER TABLE operations.operation_checkpoints FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON operations.operation_checkpoints
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);

            ALTER TABLE operations.worker_heartbeats ENABLE ROW LEVEL SECURITY;
            ALTER TABLE operations.worker_heartbeats FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON operations.worker_heartbeats
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);

            ALTER TABLE operations.processing_failure_projections ENABLE ROW LEVEL SECURITY;
            ALTER TABLE operations.processing_failure_projections FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON operations.processing_failure_projections
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS operations CASCADE;");
    }
}
