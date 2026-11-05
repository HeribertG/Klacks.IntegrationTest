// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The hardened test host with the production authentication wiring (Identity default scheme, JWT bearer, CORS policy,
/// rate limiter, MVC filters). Only configuration is set: a JWT secret long enough for HMAC-SHA256, the CORS origins the
/// tests use, and Auth:RefreshCookie:SameSite=Strict, which differs from appsettings.Development.json (None) so the tests
/// see that the cookie takes SameSite from configuration.
/// </summary>

using Klacks.Api.Application.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Klacks.IntegrationTest.Authentication;

public sealed class RefreshTokenCookieTestWebApplicationFactory : HardenedTestWebApplicationFactory
{
    public const string WhitelistedOrigin = "http://localhost:4200";

    private const string JwtSecretKey = "JwtSettings:Secret";
    private const string JwtSecret = "integration-test-refresh-cookie-secret-0123456789abcdef";
    private const string SameSiteKey = RefreshTokenCookieOptions.SectionName + ":" + nameof(RefreshTokenCookieOptions.SameSite);
    private const string StrictSameSite = "Strict";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(JwtSecretKey, JwtSecret);
        builder.UseSetting(SameSiteKey, StrictSameSite);
        builder.UseSetting(
            ConfigurationPath.Combine(CorsOriginsOptions.SectionName, nameof(CorsOriginsOptions.Home)),
            WhitelistedOrigin);
    }
}
