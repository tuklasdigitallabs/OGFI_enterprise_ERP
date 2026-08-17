using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Persistence;

namespace Ogfi.Modules.Foundation.Security;

public sealed record OutletOrganizationReference(Guid OutletId, Guid LegalEntityId, string OutletCode);

public sealed class FoundationOrganizationReferenceService(
    FoundationDbContext dbContext,
    ITenantExecutionContextAccessor executionContext)
{
    public async Task<OutletOrganizationReference?> GetOutletAsync(Guid outletId, CancellationToken cancellationToken)
    {
        if (!executionContext.IsResolved || executionContext.TenantId is not Guid tenantId)
        {
            return null;
        }

        return await dbContext.Outlets.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == outletId)
            .Select(x => new OutletOrganizationReference(x.Id, x.LegalEntityId, x.Code))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
