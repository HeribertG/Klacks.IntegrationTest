// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The hardened host with the real knowledge-index synchronizer, the learning mode fixed to Gate, and the three
/// seams that would call a language model or depend on today's corpus replaced: optimizer, routing oracle and
/// goldset replay. Everything else - sharpener, repositories, catalogue refresh, index sync, index verifier - is
/// production wiring on real Postgres.
/// </summary>
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.IntegrationTest.Assistant.Learning;

public sealed class GateModeTestWebApplicationFactory : HardenedTestWebApplicationFactory
{
    protected override bool RunsKnowledgeIndexSync => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<SkillLearningOptionsProvider>();
            services.RemoveAll<ISkillLearningOptionsProvider>();
            services.AddScoped<ISkillLearningOptionsProvider>(provider => new FixedModeSkillLearningOptionsProvider(
                provider.GetRequiredService<SkillLearningOptionsProvider>(), SkillLearningMode.Gate));

            services.RemoveAll<ISkillDescriptionOptimizer>();
            services.AddScoped<ISkillDescriptionOptimizer, NoOpSkillDescriptionOptimizer>();

            services.RemoveAll<ISkillRoutingOracle>();
            services.AddScoped<ISkillRoutingOracle, CleanRoutingOracle>();

            services.AddSingleton<RecordingReplayGate>();
            services.RemoveAll<IGoldsetHoldoutReplayGate>();
            services.AddSingleton<IGoldsetHoldoutReplayGate>(provider => provider.GetRequiredService<RecordingReplayGate>());
        });
    }
}
