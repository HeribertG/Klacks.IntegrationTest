// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Measured model eval for the Holistic Harmonizer (Wizard 3): runs the fixed in-memory eval
/// scenarios against the REAL LLM-backed IPlanProposalProvider for one model and persists the
/// resulting composite score as an EvalRun under the harmonizer-v1 goldset. The scenarios are
/// built entirely in memory — no schedule rows are seeded or deleted; the only database write
/// is the EvalRun row itself, exactly like the turn-eval harness.
///
/// The model under test comes from the HARMONIZER_EVAL_MODEL_ID environment variable
/// (default claude-haiku-45). Provider keys live in the Dev DB (5434) llm_providers; when no
/// usable provider is configured every call fails to parse and the test Assert.Ignores instead
/// of reporting a misleading zero score.
///
/// It is [Explicit] + [Category("Llm")] + [Category("RealDatabase")]: it makes real LLM API
/// calls (6 vision requests per run), costs money, needs network and the Dev DB.
/// </summary>
[TestFixture]
[Explicit("Harmonizer model eval — makes real LLM calls against a configured provider in the Dev DB (5434); costs money, local-only")]
[Category("Llm")]
[Category("RealDatabase")]
public class HarmonizerModelEvalTests
{
    private const string ModelIdEnvironmentVariable = "HARMONIZER_EVAL_MODEL_ID";
    private const string DefaultModelId = "claude-haiku-45";

    private SignalRTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [Test]
    public async Task HarmonizerEval_OnFixedScenarios_PersistsEvalRunAndReportsScore()
    {
        var modelId = Environment.GetEnvironmentVariable(ModelIdEnvironmentVariable) ?? DefaultModelId;

        using var scope = _factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IHarmonizerEvalRunnerService>();

        var result = await runner.RunAsync(modelId, CancellationToken.None);

        TestContext.Out.WriteLine(
            $"Harmonizer eval: goldset={result.Run.Goldset} model={modelId} "
            + $"composite={result.Run.CompositeScore:F4} regression={result.Run.RegressionVsBaseline?.ToString("F4") ?? "<no baseline>"} "
            + $"durationMs={result.Run.DurationMs}");
        TestContext.Out.WriteLine(
            $"Dimensions: parse={result.Dimensions.ParseRate:F4} acceptance={result.Dimensions.BatchAcceptanceRate:F4} "
            + $"improvement={result.Dimensions.NormalizedFitnessImprovement:F4} "
            + $"batches={result.Dimensions.BatchesAccepted}/{result.Dimensions.BatchesProposed} "
            + $"calls={result.Dimensions.LlmCallsParsed}/{result.Dimensions.LlmCallsTotal} "
            + $"scenariosPassed={result.Dimensions.ScenariosWithAcceptedBatch}/{result.Dimensions.ScenariosTotal}");
        foreach (var scenario in result.Scenarios)
        {
            TestContext.Out.WriteLine(
                $"  {scenario.Name}: fitness {scenario.FitnessBefore:F4} -> {scenario.FitnessAfter:F4} "
                + $"batches={scenario.BatchesAccepted}/{scenario.BatchesProposed} "
                + $"calls={scenario.LlmCallsParsed}/{scenario.LlmCallsTotal} "
                + $"lastError={scenario.LastError ?? "<none>"}");
        }

        if (result.Dimensions.LlmCallsParsed == 0)
        {
            var firstError = result.Scenarios.FirstOrDefault(s => s.LastError != null)?.LastError;
            Assert.Ignore($"No usable LLM response for model '{modelId}' — provider likely not configured: {firstError}");
        }

        Assert.That(result.Run.CompositeScore, Is.InRange(0m, 1m));
    }
}
