namespace Ogfi.ArchitectureTests;

public sealed class ModuleBoundaryTests
{
    [Fact]
    public void Foundation_module_does_not_reference_future_business_modules()
    {
        var references = typeof(Ogfi.Modules.Foundation.Persistence.FoundationDbContext)
            .Assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name)
            .Where(x => x is not null)
            .ToArray();

        Assert.DoesNotContain("Ogfi.Modules.Procurement", references);
        Assert.DoesNotContain("Ogfi.Modules.Inventory", references);
        Assert.DoesNotContain("Ogfi.Modules.Finance", references);
    }
}
