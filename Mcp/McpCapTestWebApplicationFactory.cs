// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Presentation.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.IntegrationTest.Mcp;

/// <summary>
/// The real Program.cs pipeline (authentication defaults untouched, so AddIdentity's cookie default stays in
/// place exactly as in production) with only the MCP tool-call handler swapped for a recorder and a JWT secret
/// long enough for HS256, since the committed development placeholder is too short to sign with.
/// </summary>
public sealed class McpCapTestWebApplicationFactory : HardenedTestWebApplicationFactory
{
    public const string TestJwtSecret = "INTEGRATION_TEST_mcp_cap_signing_secret_0123456789abcdef";

    private const string JwtSecretSetting = "JwtSettings:Secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(JwtSecretSetting, TestJwtSecret);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<McpCallRecorder>();
            services.RemoveAll<IMcpSkillCallHandler>();
            services.AddScoped<IMcpSkillCallHandler, RecordingMcpSkillCallHandler>();
        });
    }
}
