using System.Net;
using System.Text.Json;
using Xunit;

namespace Ogfi.IntegrationTests;

public sealed class SecuritySpineTests(SecuritySpineFixture fixture) : IClassFixture<SecuritySpineFixture>
{
    [Fact]
    public async Task Scoped_member_receives_server_derived_tenant_and_business_date()
    {
        using var client = fixture.CreateAuthenticatedClient(SecuritySpineFixture.AliceSubject, SecuritySpineFixture.TenantA);
        using var response = await client.GetAsync($"/api/context/outlets/{SecuritySpineFixture.OutletA1}?tenantId={SecuritySpineFixture.TenantB}");

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(SecuritySpineFixture.TenantA, json.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(SecuritySpineFixture.OutletA1, json.RootElement.GetProperty("outletId").GetGuid());
        Assert.Equal("2026-08-17", json.RootElement.GetProperty("businessDate").GetString());
        Assert.Equal("2026-08-17", response.Headers.GetValues("X-OGFI-Business-Date").Single());
    }

    [Fact]
    public async Task User_without_membership_in_claimed_tenant_is_denied()
    {
        using var client = fixture.CreateAuthenticatedClient(SecuritySpineFixture.AliceSubject, SecuritySpineFixture.TenantB);
        using var response = await client.GetAsync($"/api/context/outlets/{SecuritySpineFixture.OutletB1}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AUTH.TENANT_DENIED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Same_tenant_user_without_permission_is_denied()
    {
        using var client = fixture.CreateAuthenticatedClient(SecuritySpineFixture.BobSubject, SecuritySpineFixture.TenantA);
        using var response = await client.GetAsync($"/api/context/outlets/{SecuritySpineFixture.OutletA1}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AUTH.PERMISSION_DENIED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Same_tenant_user_without_outlet_scope_is_denied()
    {
        using var client = fixture.CreateAuthenticatedClient(SecuritySpineFixture.AliceSubject, SecuritySpineFixture.TenantA);
        using var response = await client.GetAsync($"/api/context/outlets/{SecuritySpineFixture.OutletA2}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AUTH.SCOPE_DENIED", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Cross_tenant_outlet_is_not_disclosed()
    {
        using var client = fixture.CreateAuthenticatedClient(SecuritySpineFixture.AliceSubject, SecuritySpineFixture.TenantA);
        using var response = await client.GetAsync($"/api/context/outlets/{SecuritySpineFixture.OutletB1}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostgreSql_row_level_security_limits_outlets_to_active_tenant_session()
    {
        Assert.Equal(2, await fixture.CountVisibleOutletsAsync(SecuritySpineFixture.TenantA));
        Assert.Equal(1, await fixture.CountVisibleOutletsAsync(SecuritySpineFixture.TenantB));
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }
}
