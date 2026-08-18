using Xunit;
using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Audit.Persistence;

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
    public void Business_modules_do_not_reference_other_business_module_implementations()
    {
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Foundation.Persistence.FoundationDbContext).Assembly,
            "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Catalog.Persistence.CatalogDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Inventory.Persistence.InventoryDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Procurement.Persistence.ProcurementDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Workflow.Persistence.WorkflowDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Finance", "Ogfi.Modules.Audit");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Finance.Persistence.FinanceDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Audit");
        AssertNoBusinessModuleReferences(
            typeof(Ogfi.Modules.Audit.Persistence.AuditDbContext).Assembly,
            "Ogfi.Modules.Foundation", "Ogfi.Modules.Catalog", "Ogfi.Modules.Inventory", "Ogfi.Modules.Procurement", "Ogfi.Modules.Workflow", "Ogfi.Modules.Finance");
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
