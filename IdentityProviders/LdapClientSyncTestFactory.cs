// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Interfaces.Authentification;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Klacks.IntegrationTest.IdentityProviders;

/// <summary>
/// WebApplicationFactory that replaces the real ILdapService with a controllable NSubstitute so the
/// client-sync pipeline (ClientSyncService, repositories, unit of work) runs against the real Postgres
/// test database while never touching an actual LDAP server. Background services, the ONNX warm-up
/// and the knowledge index sync are switched off by the hardened base.
/// </summary>
public class LdapClientSyncTestFactory : HardenedTestWebApplicationFactory
{
    public ILdapService FakeLdapService { get; } = Substitute.For<ILdapService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILdapService>();
            services.AddSingleton(FakeLdapService);
        });
    }
}
