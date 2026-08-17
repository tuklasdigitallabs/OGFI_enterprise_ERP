namespace Ogfi.BuildingBlocks.Multitenancy;

public static class OgfiClaimTypes
{
    public const string TenantId = "ogfi:tenant_id";
}

public interface ITenantExecutionContextAccessor
{
    Guid? TenantId { get; }
    Guid? UserId { get; }
    Guid? MembershipId { get; }
    bool IsResolved { get; }
    void SetCandidateTenant(Guid tenantId);
    void Resolve(Guid userId, Guid membershipId);
}

public sealed class TenantExecutionContextAccessor : ITenantExecutionContextAccessor
{
    public Guid? TenantId { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? MembershipId { get; private set; }
    public bool IsResolved { get; private set; }

    public void SetCandidateTenant(Guid tenantId)
    {
        TenantId = tenantId;
        UserId = null;
        MembershipId = null;
        IsResolved = false;
    }

    public void Resolve(Guid userId, Guid membershipId)
    {
        if (TenantId is null)
        {
            throw new InvalidOperationException("A tenant candidate must be established before resolving an OGFI membership.");
        }

        UserId = userId;
        MembershipId = membershipId;
        IsResolved = true;
    }
}
