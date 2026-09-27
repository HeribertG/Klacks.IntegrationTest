// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The SignalR test host with the learning mode fixed to AutoApply, for the explicit end-to-end learning test
/// that has to exercise phrase and capability learning - both run only in AutoApply.
/// </summary>
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.IntegrationTest.Assistant.Learning;

public sealed class AutoApplyLearningTestWebApplicationFactory : SignalRTestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<SkillLearningOptionsProvider>();
            services.RemoveAll<ISkillLearningOptionsProvider>();
            services.AddScoped<ISkillLearningOptionsProvider>(provider => new FixedModeSkillLearningOptionsProvider(
                provider.GetRequiredService<SkillLearningOptionsProvider>(), SkillLearningMode.AutoApply));
        });
    }
}
