// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Live eval of the turn-selection goldset. Replays every item against the configured model,
/// persists one eval_runs row and gates the pass rate against the best comparable earlier run,
/// with an absolute floor underneath so a missing baseline can never make the gate silently green.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Explicit("Performs real LLM provider calls (costs money) and writes an EvalRun to the real DB on port 5434. Run manually only.")]
[Category("Llm")]
[Category("RealDatabase")]
public class TurnSelectionGoldenSetTests
{
    private const string DefaultGoldsetName = "turn-selection-v1";
    private const string AdminRight = "Admin";

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
    public async Task TurnSelectionGoldset_ReplaysAllItemsAndReportsScorecard()
    {
        var modelId = TurnEvalPassRateGate.ResolveModelId();
        var goldsetName = TurnEvalPassRateGate.ResolveGoldset(DefaultGoldsetName);
        var maxItems = TurnEvalPassRateGate.ResolveMaxItems();

        using var scope = _factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ITurnEvalRunnerService>();
        var goldsetItems = await scope.ServiceProvider.GetRequiredService<ITurnGoldsetLoader>()
            .LoadAsync(goldsetName, CancellationToken.None);

        var (expectedItemsTotal, isPartial) = TurnEvalPassRateGate.ResolveScope(goldsetItems.Count, maxItems);

        // Read the threshold BEFORE the run so the freshly persisted EvalRun is never its own baseline.
        var threshold = await TurnEvalPassRateGate.ResolveThresholdAsync(
            scope.ServiceProvider.GetRequiredService<IEvalRunRepository>(),
            goldsetName,
            modelId,
            expectedItemsTotal,
            isPartial);

        var result = await runner.RunAsync(
            goldsetName,
            modelId,
            maxItems,
            userId: Guid.NewGuid().ToString(),
            userRights: [AdminRight]);

        result.Dimensions.ShouldNotBeNull();
        result.Dimensions!.ItemsTotal.ShouldBe(expectedItemsTotal);
        result.Run.IsPartial.ShouldBe(isPartial);

        WriteScorecard(goldsetName, modelId, result);

        var passRate = TurnEvalPassRateGate.ComputePassRate(result.Dimensions);
        passRate.ShouldBeGreaterThanOrEqualTo(
            threshold,
            $"turn-selection pass rate {passRate:P1} below the gate {threshold:P1}.");
    }

    private static void WriteScorecard(string goldsetName, string modelId, TurnEvalRunResult result)
    {
        var dimensions = result.Dimensions!;

        TestContext.WriteLine($"Goldset:                {goldsetName}");
        TestContext.WriteLine($"Model:                  {modelId} (provider: {result.Run.Provider})");
        TestContext.WriteLine($"ScorerVersion:          {result.Run.ScorerVersion} (partial run: {result.Run.IsPartial})");
        TestContext.WriteLine($"Composite:              {result.Run.CompositeScore:F4}");
        TestContext.WriteLine($"Regression vs baseline: {result.Run.RegressionVsBaseline?.ToString("F4") ?? "n/a"}");
        TestContext.WriteLine($"ToolAccuracy:           {Format(dimensions.ToolAccuracy)}");
        TestContext.WriteLine($"SlotAccuracy:           {Format(dimensions.SlotAccuracy)}");
        TestContext.WriteLine($"NoToolAccuracy:         {Format(dimensions.NoToolAccuracy)}");
        TestContext.WriteLine($"RecipeAccuracy:         {Format(dimensions.RecipeAccuracy)}");
        TestContext.WriteLine($"HonestyAccuracy:        {Format(dimensions.HonestyAccuracy)}");
        TestContext.WriteLine($"NameResolutionAccuracy: {Format(dimensions.NameResolutionAccuracy)}");
        TestContext.WriteLine($"AvgLatencyMs:           {dimensions.AvgLatencyMs:F0} (reported only, not in the composite)");
        TestContext.WriteLine($"TotalCost:              {dimensions.TotalCost:F4}");
        TestContext.WriteLine(
            $"Items:                  total={dimensions.ItemsTotal}, passed={dimensions.ItemsPassed}, " +
            $"excluded={dimensions.ItemsExcluded}, errored={dimensions.ItemsErrored}");
        TestContext.WriteLine(string.Empty);

        foreach (var item in result.Items.Where(i => !i.Passed))
        {
            TestContext.WriteLine(
                $"MISS {item.ItemId}: expected={item.ExpectedTool ?? item.ExpectedRecipe ?? "(none)"}, chosen={item.ChosenTool ?? "(none)"}, " +
                $"slotScore={Format(item.SlotScore)}, toolAvailable={item.ExpectedToolAvailable?.ToString() ?? "n/a"}, " +
                $"recipe={item.EngineRecipeWouldTrigger}, excluded={item.Excluded}, errored={item.Errored}, error={item.Error ?? "-"}");
        }
    }

    private static string Format(double? value) => value?.ToString("F4") ?? "n/a";
}
