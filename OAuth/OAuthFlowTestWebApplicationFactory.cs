// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The real Program.cs pipeline for the OAuth server tests: production authentication wiring, the MCP tool-call handler
/// swapped for a recorder, a JWT secret long enough for HS256, settings that a test can override without touching the
/// shared settings table, and a per-request remote address so the login rate limit stays out of the way.
/// </summary>

using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Presentation.Mcp;
using Klacks.IntegrationTest.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.IntegrationTest.OAuth;

public sealed class OAuthFlowTestWebApplicationFactory : HardenedTestWebApplicationFactory
{
    private const string JwtSecretSetting = "JwtSettings:Secret";
    private const string JwtSecret = "INTEGRATION_TEST_oauth_flow_signing_secret_0123456789abcdef";

    public OAuthSettingOverrides Overrides { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(JwtSecretSetting, JwtSecret);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Overrides);
            services.AddSingleton<McpCallRecorder>();
            services.RemoveAll<IMcpSkillCallHandler>();
            services.AddScoped<IMcpSkillCallHandler, RecordingMcpSkillCallHandler>();
            services.RemoveAll<ISettingsReader>();
            services.AddScoped<ISettingsReader>(provider => new OverridableSettingsReader(
                provider.GetRequiredService<Klacks.Api.Application.Interfaces.ISettingsRepository>(),
                provider.GetRequiredService<OAuthSettingOverrides>()));
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();
        });
    }
}
