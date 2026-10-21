// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;

namespace Klacks.IntegrationTest.TestHelpers;

/// <summary>
/// Builds the AnalyseScenarioService a test exercises on top of the test's own DataBaseContext, so every test
/// constructs the service the same way.
/// </summary>
internal static class AnalyseScenarioTestServiceFactory
{
    public static AnalyseScenarioService Create(DataBaseContext context) => new(context);
}
