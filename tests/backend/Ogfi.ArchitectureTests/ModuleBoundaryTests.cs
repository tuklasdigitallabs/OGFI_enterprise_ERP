using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Ogfi.Modules.Audit.Persistence;
using Ogfi.Modules.DurableOperations.Persistence;

namespace Ogfi.ArchitectureTests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Audit_model_snapshot_matches_runtime_model()
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .Options;
        using var dbContext = new AuditDbContext(options);
        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Durable_operations_model_snapshot_matches_runtime_model()
    {
        var options = new DbContextOptionsBuilder<DurableOperationsDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .Options;
        using var dbContext = new DurableOperationsDbContext(options);
        var snapshot = dbContext.GetService<IMigrationsAssembly>().ModelSnapshot
            ?? throw new InvalidOperationException("Durable Operations model snapshot is missing.");
        var currentModel = dbContext.GetService<IDesignTimeModel>().Model;
        var snapshotModel = dbContext.GetService<IModelRuntimeInitializer>()
            .Initialize(snapshot.Model, designTime: true);
        var differences = dbContext.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(), currentModel.GetRelationalModel());
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Select(x => x switch
        {
            CreateIndexOperation index => $"CreateIndex {index.Name} on {index.Table} ({string.Join(",", index.Columns)})",
            DropIndexOperation index => $"DropIndex {index.Name} on {index.Table}",
            _ => x.ToString()
        })));
    }

    [Fact]
    public void Business_modules_and_durable_operations_have_no_cross_implementation_references()
    {
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Foundation.Persistence.FoundationDbContext).Assembly,
            "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit", "Ogfi.Modules.DurableOperations");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Catalog.Persistence.CatalogDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit", "Ogfi.Modules.DurableOperations");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Inventory.Persistence.InventoryDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit", "Ogfi.Modules.DurableOperations");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Procurement.Persistence.ProcurementDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit", "Ogfi.Modules.DurableOperations");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Workflow.Persistence.WorkflowDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit", "Ogfi.Modules.DurableOperations");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Finance.Persistence.FinanceDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Audit", "Ogfi.Modules.DurableOperations");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Audit.Persistence.AuditDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.DurableOperations");
        AssertNoBusinessModuleReferences(
            typeof(DurableOperationsDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit");
    }

    private static void AssertNoBusinessModuleReferences(System.Reflection.Assembly assembly, params string[] forbidden)
    {
        var references = assembly.GetReferencedAssemblies().Select(x => x.Name).Where(x => x is not null).ToArray();
        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, references);
        }
    }
}
