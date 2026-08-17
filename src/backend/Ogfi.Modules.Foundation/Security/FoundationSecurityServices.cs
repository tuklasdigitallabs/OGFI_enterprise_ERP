using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.BuildingBlocks.Time;
using Ogfi.Modules.Foundation.Persistence;

namespace Ogfi.Modules.Foundation.Security;

public sealed record ResolvedMembership(Guid UserId, Guid MembershipId);

public sealed record OutletBusinessContext(
    Guid OutletId,
    string OutletCode,
    string TimeZoneId,
    BusinessDate BusinessDate);

public sealed class MembershipResolver(FoundationDbContext dbContext)
{
    public async Task<ResolvedMembership?> ResolveAsync(Guid tenantId, string externalSubject, CancellationToken cancellationToken)
    {
        var userId = await dbContext.Users
            .Where(x => x.ExternalSubject == externalSubject)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            return null;
        }

        var membership = await dbContext.TenantMemberships
            .Where(x => x.TenantId == tenantId && x.UserId == userId.Value && x.Status == MembershipStatuses.Active)
            .Select(x => new ResolvedMembership(userId.Value, x.Id))
            .SingleOrDefaultAsync(cancellationToken);

        return membership;
    }
}

public sealed class FoundationAuthorizationEvaluator(
    FoundationDbContext dbContext,
    ITenantExecutionContextAccessor executionContext)
{
    public async Task<bool> HasPermissionAsync(string permissionCode, CancellationToken cancellationToken)
    {
        if (!executionContext.IsResolved || executionContext.TenantId is not Guid tenantId || executionContext.MembershipId is not Guid membershipId)
        {
            return false;
        }

        return await dbContext.PermissionGrants.AnyAsync(
            x => x.TenantId == tenantId && x.MembershipId == membershipId && x.PermissionCode == permissionCode,
            cancellationToken);
    }

    public async Task<bool> HasOutletScopeAsync(Guid outletId, CancellationToken cancellationToken)
    {
        if (!executionContext.IsResolved || executionContext.TenantId is not Guid tenantId || executionContext.MembershipId is not Guid membershipId)
        {
            return false;
        }

        return await dbContext.OutletScopeGrants.AnyAsync(
            x => x.TenantId == tenantId && x.MembershipId == membershipId && x.OutletId == outletId,
            cancellationToken);
    }

    public async Task<Guid[]> GetOutletScopeIdsAsync(CancellationToken cancellationToken)
    {
        if (!executionContext.IsResolved || executionContext.TenantId is not Guid tenantId || executionContext.MembershipId is not Guid membershipId)
        {
            return [];
        }

        return await dbContext.OutletScopeGrants
            .Where(x => x.TenantId == tenantId && x.MembershipId == membershipId)
            .Select(x => x.OutletId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
    }
}

public sealed class BusinessTimeResolver(
    FoundationDbContext dbContext,
    ITenantExecutionContextAccessor executionContext,
    TimeProvider timeProvider)
{
    public async Task<OutletBusinessContext?> ResolveAsync(Guid outletId, CancellationToken cancellationToken)
    {
        if (!executionContext.IsResolved || executionContext.TenantId is not Guid tenantId)
        {
            return null;
        }

        var outlet = await dbContext.Outlets
            .Where(x => x.TenantId == tenantId && x.Id == outletId)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.TimeZoneId,
                x.BusinessDayStartMinutes
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (outlet is null)
        {
            return null;
        }

        if (outlet.BusinessDayStartMinutes is < 0 or >= 1440)
        {
            throw new InvalidOperationException($"Outlet {outlet.Id} has an invalid Business Day start minute value.");
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(outlet.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone);
        var localDate = DateOnly.FromDateTime(local.DateTime);
        var localMinutes = (local.Hour * 60) + local.Minute;
        var businessDate = localMinutes < outlet.BusinessDayStartMinutes
            ? localDate.AddDays(-1)
            : localDate;

        return new OutletBusinessContext(
            outlet.Id,
            outlet.Code,
            outlet.TimeZoneId,
            new BusinessDate(businessDate));
    }
}
