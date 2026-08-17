using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ogfi.Modules.Workflow.Persistence.Migrations;

[DbContext(typeof(WorkflowDbContext))]
[Migration("20260818061100_WorkflowTenantRls")]
public sealed class WorkflowTenantRls : Migration
{
    private static readonly string[] TenantTables =
    [
        "workflow_definition_versions",
        "workflow_instances",
        "workflow_tasks",
        "workflow_task_candidates",
        "approval_decisions",
        "outbox_messages"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables)
        {
            migrationBuilder.Sql($$"""
                ALTER TABLE workflow.{{table}} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE workflow.{{table}} FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS tenant_isolation ON workflow.{{table}};
                CREATE POLICY tenant_isolation ON workflow.{{table}}
                    USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in TenantTables.Reverse())
        {
            migrationBuilder.Sql($$"""
                DROP POLICY IF EXISTS tenant_isolation ON workflow.{{table}};
                ALTER TABLE workflow.{{table}} NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE workflow.{{table}} DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
