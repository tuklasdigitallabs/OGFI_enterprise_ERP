using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Audit.Persistence.Migrations;

[DbContext(typeof(AuditDbContext))]
[Migration("20260818150000_InitialAudit")]
public sealed class InitialAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SCHEMA IF NOT EXISTS audit;

            CREATE TABLE audit.audit_events (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "ActorType" varchar(20) NOT NULL,
                "ActorUserId" uuid NULL,
                "ActorMembershipId" uuid NULL,
                "Action" varchar(120) NOT NULL,
                "SourceModule" varchar(80) NOT NULL,
                "ResourceType" varchar(100) NOT NULL,
                "ResourceId" uuid NOT NULL,
                "ResourceRevision" bigint NULL,
                "LegalEntityId" uuid NULL,
                "OutletId" uuid NULL,
                "BusinessDate" date NULL,
                "OccurredAtUtc" timestamptz NOT NULL,
                "Outcome" varchar(20) NOT NULL,
                "ErrorCode" varchar(120) NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "CausationId" varchar(128) NULL,
                "SourceEventId" uuid NULL,
                "SafeEvidenceJson" jsonb NOT NULL,
                "PurchaseOrderId" uuid NULL,
                "WorkflowInstanceId" uuid NULL,
                "ApprovalTaskId" uuid NULL,
                "ApprovalDecisionId" uuid NULL,
                "GoodsReceiptId" uuid NULL,
                "InventoryMovementId" uuid NULL,
                "FinanceSourcePostingId" uuid NULL,
                "JournalId" uuid NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                CONSTRAINT "AK_audit_events_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "CK_audit_event_revision" CHECK ("ResourceRevision" IS NULL OR "ResourceRevision" > 0),
                CONSTRAINT "CK_audit_event_safe_evidence_size" CHECK (octet_length("SafeEvidenceJson"::text) <= 16384)
            );
            CREATE UNIQUE INDEX "IX_audit_events_TenantId_SourceModule_SourceEventId_Action"
                ON audit.audit_events ("TenantId", "SourceModule", "SourceEventId", "Action")
                WHERE "SourceEventId" IS NOT NULL;
            CREATE INDEX "IX_audit_events_TenantId_OccurredAtUtc"
                ON audit.audit_events ("TenantId", "OccurredAtUtc");
            CREATE INDEX "IX_audit_events_TenantId_ResourceType_ResourceId_OccurredAtUtc"
                ON audit.audit_events ("TenantId", "ResourceType", "ResourceId", "OccurredAtUtc");
            CREATE INDEX "IX_audit_events_TenantId_CorrelationId_OccurredAtUtc"
                ON audit.audit_events ("TenantId", "CorrelationId", "OccurredAtUtc");
            CREATE INDEX "IX_audit_events_TenantId_PurchaseOrderId_OccurredAtUtc"
                ON audit.audit_events ("TenantId", "PurchaseOrderId", "OccurredAtUtc");
            CREATE INDEX "IX_audit_events_TenantId_GoodsReceiptId_OccurredAtUtc"
                ON audit.audit_events ("TenantId", "GoodsReceiptId", "OccurredAtUtc");
            CREATE INDEX "IX_audit_events_TenantId_JournalId_OccurredAtUtc"
                ON audit.audit_events ("TenantId", "JournalId", "OccurredAtUtc");

            CREATE TABLE audit.rs01_trace_projections (
                "Id" uuid PRIMARY KEY,
                "TenantId" uuid NOT NULL,
                "PurchaseOrderId" uuid NOT NULL,
                "WorkflowInstanceId" uuid NULL,
                "ApprovalTaskId" uuid NULL,
                "ApprovalDecisionId" uuid NULL,
                "GoodsReceiptId" uuid NULL,
                "InventoryMovementIdsJson" jsonb NOT NULL,
                "InventoryMovementCount" integer NOT NULL,
                "FinanceSourcePostingId" uuid NULL,
                "JournalId" uuid NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "State" varchar(20) NOT NULL,
                "MissingLinksJson" jsonb NOT NULL,
                "InvalidReason" varchar(600) NULL,
                "EvidenceEventCount" integer NOT NULL,
                "FirstEventAtUtc" timestamptz NOT NULL,
                "LastEventAtUtc" timestamptz NOT NULL,
                "RebuiltAtUtc" timestamptz NOT NULL,
                CONSTRAINT "AK_rs01_trace_projections_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "CK_audit_trace_event_count" CHECK ("EvidenceEventCount" > 0),
                CONSTRAINT "CK_audit_trace_movement_count" CHECK ("InventoryMovementCount" >= 0)
            );
            CREATE UNIQUE INDEX "IX_rs01_trace_projections_TenantId_PurchaseOrderId_GoodsReceiptId"
                ON audit.rs01_trace_projections ("TenantId", "PurchaseOrderId", "GoodsReceiptId")
                WHERE "GoodsReceiptId" IS NOT NULL;
            CREATE UNIQUE INDEX "IX_rs01_trace_projections_TenantId_PurchaseOrderId"
                ON audit.rs01_trace_projections ("TenantId", "PurchaseOrderId")
                WHERE "GoodsReceiptId" IS NULL;
            CREATE INDEX "IX_rs01_trace_projections_TenantId_GoodsReceiptId"
                ON audit.rs01_trace_projections ("TenantId", "GoodsReceiptId");
            CREATE INDEX "IX_rs01_trace_projections_TenantId_JournalId"
                ON audit.rs01_trace_projections ("TenantId", "JournalId");
            CREATE INDEX "IX_rs01_trace_projections_TenantId_CorrelationId"
                ON audit.rs01_trace_projections ("TenantId", "CorrelationId");
            CREATE INDEX "IX_rs01_trace_projections_TenantId_State_LastEventAtUtc"
                ON audit.rs01_trace_projections ("TenantId", "State", "LastEventAtUtc");

            CREATE OR REPLACE FUNCTION audit.prevent_audit_event_mutation() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'Audit Event is append-only' USING ERRCODE = '55000';
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER trg_audit_event_append_only
                BEFORE UPDATE OR DELETE ON audit.audit_events
                FOR EACH ROW EXECUTE FUNCTION audit.prevent_audit_event_mutation();

            ALTER TABLE audit.audit_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE audit.audit_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON audit.audit_events
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);

            ALTER TABLE audit.rs01_trace_projections ENABLE ROW LEVEL SECURITY;
            ALTER TABLE audit.rs01_trace_projections FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON audit.rs01_trace_projections
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS audit CASCADE;");
    }
}
