using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Workflow.Persistence.Migrations;

[DbContext(typeof(WorkflowDbContext))]
[Migration("20260818061000_InitialWorkflowApproval")]
public sealed class InitialWorkflowApproval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE SCHEMA IF NOT EXISTS workflow;

            CREATE TABLE workflow.workflow_definition_versions (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "Code" varchar(100) NOT NULL,
                "Version" integer NOT NULL,
                "Name" varchar(200) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_workflow_definition_versions" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_workflow_definition_versions_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "CK_workflow_definition_version" CHECK ("Version" > 0)
            );
            CREATE UNIQUE INDEX "IX_workflow_definition_versions_TenantId_Code_Version"
                ON workflow.workflow_definition_versions ("TenantId", "Code", "Version");

            CREATE TABLE workflow.workflow_instances (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "DefinitionVersionId" uuid NOT NULL,
                "SubjectType" varchar(60) NOT NULL,
                "SubjectId" uuid NOT NULL,
                "ApprovalRound" integer NOT NULL,
                "SubjectVersion" bigint NOT NULL,
                "RequesterUserId" uuid NOT NULL,
                "LegalEntityId" uuid NOT NULL,
                "OutletId" uuid NOT NULL,
                "BusinessDate" date NOT NULL,
                "PurchaseOrderTotal" numeric(19,4) NOT NULL,
                "Currency" varchar(3) NOT NULL,
                "Status" varchar(20) NOT NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "StartedAtUtc" timestamp with time zone NOT NULL,
                "CompletedAtUtc" timestamp with time zone NULL,
                CONSTRAINT "PK_workflow_instances" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_workflow_instances_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "CK_workflow_instance_round" CHECK ("ApprovalRound" > 0),
                CONSTRAINT "CK_workflow_instance_subject_version" CHECK ("SubjectVersion" > 0),
                CONSTRAINT "CK_workflow_instance_total" CHECK ("PurchaseOrderTotal" >= 0),
                CONSTRAINT "FK_workflow_instances_definition"
                    FOREIGN KEY ("TenantId", "DefinitionVersionId")
                    REFERENCES workflow.workflow_definition_versions ("TenantId", "Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_workflow_instances_subject_round"
                ON workflow.workflow_instances ("TenantId", "SubjectType", "SubjectId", "ApprovalRound");

            CREATE TABLE workflow.workflow_tasks (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "InstanceId" uuid NOT NULL,
                "StepKey" varchar(80) NOT NULL,
                "Status" varchar(20) NOT NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "CompletedAtUtc" timestamp with time zone NULL,
                CONSTRAINT "PK_workflow_tasks" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_workflow_tasks_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_workflow_tasks_instance"
                    FOREIGN KEY ("TenantId", "InstanceId")
                    REFERENCES workflow.workflow_instances ("TenantId", "Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_workflow_tasks_instance_step"
                ON workflow.workflow_tasks ("TenantId", "InstanceId", "StepKey");

            CREATE TABLE workflow.workflow_task_candidates (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "TaskId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                CONSTRAINT "PK_workflow_task_candidates" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_workflow_task_candidates_task"
                    FOREIGN KEY ("TenantId", "TaskId")
                    REFERENCES workflow.workflow_tasks ("TenantId", "Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_workflow_task_candidates_task_user"
                ON workflow.workflow_task_candidates ("TenantId", "TaskId", "UserId");

            CREATE TABLE workflow.approval_decisions (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "InstanceId" uuid NOT NULL,
                "TaskId" uuid NOT NULL,
                "Decision" varchar(20) NOT NULL,
                "ActorUserId" uuid NOT NULL,
                "DecidedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_approval_decisions" PRIMARY KEY ("Id"),
                CONSTRAINT "FK_approval_decisions_task"
                    FOREIGN KEY ("TenantId", "TaskId")
                    REFERENCES workflow.workflow_tasks ("TenantId", "Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_approval_decisions_instance"
                    FOREIGN KEY ("TenantId", "InstanceId")
                    REFERENCES workflow.workflow_instances ("TenantId", "Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_approval_decisions_task"
                ON workflow.approval_decisions ("TenantId", "TaskId");

            CREATE TABLE workflow.outbox_messages (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "Type" varchar(200) NOT NULL,
                "SchemaVersion" integer NOT NULL,
                "OccurredAtUtc" timestamp with time zone NOT NULL,
                "CorrelationId" varchar(64) NOT NULL,
                "CausationId" varchar(128) NULL,
                "Payload" jsonb NOT NULL,
                "ProcessedAtUtc" timestamp with time zone NULL,
                "AttemptCount" integer NOT NULL,
                "LastError" text NULL,
                CONSTRAINT "PK_workflow_outbox_messages" PRIMARY KEY ("Id")
            );
            CREATE INDEX "IX_workflow_outbox_messages_processed_occurred"
                ON workflow.outbox_messages ("ProcessedAtUtc", "OccurredAtUtc");
            CREATE UNIQUE INDEX "IX_workflow_outbox_messages_causation"
                ON workflow.outbox_messages ("TenantId", "Type", "CausationId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP SCHEMA IF EXISTS workflow CASCADE;");
    }
}
