namespace Ogfi.Modules.Foundation.Security;

public static class FoundationPermissionCodes
{
    public const string ContextRead = "foundation.context.read";
}

public static class MembershipStatuses
{
    public const string Active = "ACTIVE";
}

public sealed class Tenant
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
}

public sealed class ErpUser
{
    public Guid Id { get; set; }
    public required string ExternalSubject { get; set; }
    public required string DisplayName { get; set; }
}

public sealed class LegalEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
}

public sealed class Outlet
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string TimeZoneId { get; set; }
    public int BusinessDayStartMinutes { get; set; }
}

public sealed class TenantMembership
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public required string Status { get; set; }
}

public sealed class PermissionGrant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid MembershipId { get; set; }
    public required string PermissionCode { get; set; }
}

public sealed class OutletScopeGrant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid OutletId { get; set; }
}
