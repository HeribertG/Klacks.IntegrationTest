// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.IntegrationTest.TestHelpers;

/// <summary>
/// Builds the real AnalyseScenarioService object graph (the entry point and every collaborator, wired through the
/// production AddAnalyseScenarioServices registration) on top of the test's own DataBaseContext, so every collaborator
/// stages on the context the test reads back, exactly as they share the request scope's context in the application.
/// </summary>
internal static class AnalyseScenarioTestServiceFactory
{
    public static AnalyseScenarioService Create(DataBaseContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services.AddAnalyseScenarioServices();
        return (AnalyseScenarioService)services.BuildServiceProvider().GetRequiredService<IAnalyseScenarioService>();
    }
}
