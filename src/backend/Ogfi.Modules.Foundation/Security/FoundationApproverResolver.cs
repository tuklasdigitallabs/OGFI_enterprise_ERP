using Microsoft.EntityFrameworkCore;
using Ogfi.Modules.Foundation.Persistence;

namespace Ogfi.Modules.Foundation.Security;

public sealed class FoundationApproverResolver(FoundationDbContext dbContext)
{
    public async Task<Guid[]> ResolveUserIdsAsync(
        Guid tenantId,
        string permissionCode,
        Guid outletId,
        CancellationToken cancellationToken)
    {
        return await (
            from membership in dbContext.TenantMemberships.AsNoTracking()
            join permission in dbContext.PermissionGrants.AsNoTracking()
                on new { membership.TenantId, MembershipId = membership.Id }
                equals new { permission.TenantId, permission.MembershipId }
            join scope in dbContext.OutletScopeGrants.AsNoTracking()
                on new { membership.TenantId, MembershipId = membership.Id }
                equals new { scope.TenantId, scope.MembershipId }
            where membership.TenantId == tenantId
                  && membership.Status == MembershipStatuses.Active
                  && permission.PermissionCode == permissionCode
                  && scope.OutletId == outletId
            select membership.UserId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
    }
}
