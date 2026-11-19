// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Interfaces.Authentification;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Klacks.IntegrationTest.IdentityProviders;

/// <summary>
/// WebApplicationFactory that replaces the real ILdapService with a controllable NSubstitute so the
/// client-sync pipeline (ClientSyncService, repositories, unit of work) runs against the real Postgres
/// test database while never touching an actual LDAP server. Background services, the ONNX warm-up
/// and the knowledge index sync are switched off by the hardened base. The JWT secret and the CORS home origin are
/// set so the directory sign-in and approval tests can sign in over HTTP and call the admin endpoints.
/// </summary>
public class LdapClientSyncTestFactory : HardenedTestWebApplicationFactory
{
    private const string JwtSecretKey = "JwtSettings:Secret";
    private const string JwtSecret = "integration-test-directory-login-secret-0123456789abcdef";
    private const string WhitelistedOrigin = "http://localhost:4200";

    public ILdapService FakeLdapService { get; } = Substitute.For<ILdapService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(JwtSecretKey, JwtSecret);
        builder.UseSetting(
            ConfigurationPath.Combine(CorsOriginsOptions.SectionName, nameof(CorsOriginsOptions.Home)),
            WhitelistedOrigin);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILdapService>();
            services.AddSingleton(FakeLdapService);
        });
    }
}
