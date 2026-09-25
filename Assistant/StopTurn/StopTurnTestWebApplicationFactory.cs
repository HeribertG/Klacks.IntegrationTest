// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The hardened test host (real Program.cs, real container, the shared integration database, no background
/// service) with two seams replaced: the provider factory hands out a scripted provider and the skill bridge
/// runs scripted skills. Everything else - the chat service, the recorder, the finalizer, the repositories,
/// the anchor store, the trajectory capture - is the production wiring on real Postgres.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

public sealed class StopTurnTestWebApplicationFactory : HardenedTestWebApplicationFactory
{
    public ScriptedLlmProvider Provider { get; } = new();

    public ScriptedSkillBridge Skills { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILLMProviderFactory>();
            services.AddSingleton<ILLMProviderFactory>(new ScriptedLlmProviderFactory(Provider));
            services.RemoveAll<ILLMSkillBridge>();
            services.AddSingleton<ILLMSkillBridge>(Skills);
        });
    }
}
