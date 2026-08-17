using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ogfi.Modules.Workflow.Persistence;

public sealed class WorkflowDesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=ogfi;Username=ogfi;Password=ogfi_dev";
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new WorkflowDbContext(options);
    }
}
