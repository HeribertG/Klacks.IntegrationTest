// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live eval of the honesty goldset ('turn-honesty-v1'): questions Klacks holds no data for at all.
/// A correct turn calls no tool AND states no invented fact; every ungrounded claim in the answer
/// counts against the honesty dimension. Persists one eval_runs row like every other turn eval, so
/// the dimension that carries 15 % of the composite finally has runs of its own instead of being
/// loaded but never executed.
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
public class TurnHonestyGoldenSetTests
{
    private const string GoldsetName = "turn-honesty-v1";
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
    public async Task TurnHonestyGoldset_ReplaysAbstainItemsAndReportsHonestyAccuracy()
    {
        var modelId = TurnEvalPassRateGate.ResolveModelId();
        var maxItems = TurnEvalPassRateGate.ResolveMaxItems();

        using var scope = _factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ITurnEvalRunnerService>();
        var goldsetItems = await scope.ServiceProvider.GetRequiredService<ITurnGoldsetLoader>()
            .LoadAsync(GoldsetName, CancellationToken.None);

        goldsetItems.Count.ShouldBeGreaterThan(0);
        goldsetItems.ShouldAllBe(i => i.Honesty != null,
            "every item of the honesty goldset must declare a honesty mode, otherwise it is scored as a plain no-tool turn.");
        goldsetItems.ShouldAllBe(
            i => i.Honesty!.Mode == TurnEvalScorer.HonestyModeMustAbstain,
            $"the only honesty mode the scorer evaluates is '{TurnEvalScorer.HonestyModeMustAbstain}'; any other mode leaves the item unmeasured.");

        var (expectedItemsTotal, isPartial) = TurnEvalPassRateGate.ResolveScope(goldsetItems.Count, maxItems);

        var threshold = await TurnEvalPassRateGate.ResolveThresholdAsync(
            scope.ServiceProvider.GetRequiredService<IEvalRunRepository>(),
            GoldsetName,
            modelId,
            expectedItemsTotal,
            isPartial);

        var result = await runner.RunAsync(
            GoldsetName,
            modelId,
            maxItems,
            userId: Guid.NewGuid().ToString(),
            userRights: [AdminRight]);

        result.Dimensions.ShouldNotBeNull();
        result.Dimensions!.ItemsTotal.ShouldBe(expectedItemsTotal);
        result.Dimensions.HonestyAccuracy.ShouldNotBeNull(
            "the run measured no honesty item at all - the goldset or the scorer mode matching is broken.");

        WriteScorecard(modelId, result);

        var passRate = TurnEvalPassRateGate.ComputePassRate(result.Dimensions);
        passRate.ShouldBeGreaterThanOrEqualTo(
            threshold,
            $"turn-honesty pass rate {passRate:P1} below the gate {threshold:P1}.");
    }

    private static void WriteScorecard(string modelId, TurnEvalRunResult result)
    {
        var dimensions = result.Dimensions!;

        TestContext.WriteLine($"Goldset:                {GoldsetName}");
        TestContext.WriteLine($"Model:                  {modelId} (provider: {result.Run.Provider})");
        TestContext.WriteLine($"ScorerVersion:          {result.Run.ScorerVersion} (partial run: {result.Run.IsPartial})");
        TestContext.WriteLine($"Composite:              {result.Run.CompositeScore:F4}");
        TestContext.WriteLine($"Regression vs baseline: {result.Run.RegressionVsBaseline?.ToString("F4") ?? "n/a"}");
        TestContext.WriteLine($"HonestyAccuracy:        {Format(dimensions.HonestyAccuracy)}");
        TestContext.WriteLine($"NoToolAccuracy:         {Format(dimensions.NoToolAccuracy)}");
        TestContext.WriteLine($"AvgLatencyMs:           {dimensions.AvgLatencyMs:F0} (reported only, not in the composite)");
        TestContext.WriteLine($"TotalCost:              {dimensions.TotalCost:F4}");
        TestContext.WriteLine(
            $"Items:                  total={dimensions.ItemsTotal}, passed={dimensions.ItemsPassed}, " +
            $"excluded={dimensions.ItemsExcluded}, errored={dimensions.ItemsErrored}");
        TestContext.WriteLine(string.Empty);

        foreach (var item in result.Items.Where(i => !i.Passed))
        {
            TestContext.WriteLine(
                $"MISS {item.ItemId}: chosenTool={item.ChosenTool ?? "(none)"}, honest={item.HonestyCorrect?.ToString() ?? "n/a"}, " +
                $"ungrounded=[{string.Join(" | ", item.UngroundedClaims)}], errored={item.Errored}, error={item.Error ?? "-"}");
        }
    }

    private static string Format(double? value) => value?.ToString("F4") ?? "n/a";
}
