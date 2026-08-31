// Copyright (c) Heribert Gasparoli Private. All rights reserved.

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
    private const string GoldsetName = "turn-selection-v1";
    private const string ModelIdEnvironmentVariable = "TURNEVAL_MODEL_ID";
    private const string MaxItemsEnvironmentVariable = "TURNEVAL_MAX_ITEMS";
    private const string MinPassRateEnvironmentVariable = "TURNEVAL_MIN_PASS_RATE";
    private const string DefaultModelId = "deepseek-v4-pro";
    private const string AdminRight = "Admin";
    private const double BaselineTolerance = 0.05;

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
        var modelId = Environment.GetEnvironmentVariable(ModelIdEnvironmentVariable) ?? DefaultModelId;
        int? maxItems = int.TryParse(
            Environment.GetEnvironmentVariable(MaxItemsEnvironmentVariable), out var parsedMaxItems) && parsedMaxItems > 0
            ? parsedMaxItems
            : null;
        var forcedMinPassRate = ReadForcedMinPassRate();

        using var scope = _factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ITurnEvalRunnerService>();
        var goldsetItems = await scope.ServiceProvider.GetRequiredService<ITurnGoldsetLoader>()
            .LoadAsync(GoldsetName, CancellationToken.None);

        // Read the baseline BEFORE the run so the freshly persisted EvalRun is never its own baseline.
        // W0.5/W4: the baseline only gates when it measured the same goldset size — a goldset build-out
        // or a maxItems iteration is a calibration/subset run and must not be compared against a run
        // over different items.
        double? baselineThreshold = null;
        if (!forcedMinPassRate.HasValue)
        {
            var evalRunRepository = scope.ServiceProvider.GetRequiredService<IEvalRunRepository>();
            var baseline = await evalRunRepository.GetLatestAsync(GoldsetName, modelId);
            if (baseline != null && baseline.ItemsTotal > 0 && maxItems == null && baseline.ItemsTotal == goldsetItems.Count)
            {
                var baselinePassRate = (double)baseline.ItemsPassed / baseline.ItemsTotal;
                baselineThreshold = Math.Max(0.0, baselinePassRate - BaselineTolerance);
                TestContext.WriteLine(
                    $"Gate baseline: {baselinePassRate:P1} (latest {GoldsetName}/{modelId} run over {baseline.ItemsTotal} items) -> min pass rate {baselineThreshold:P1}");
            }
            else
            {
                TestContext.WriteLine(
                    $"No comparable baseline EvalRun found (baseline items {baseline?.ItemsTotal.ToString() ?? "n/a"}, goldset items {goldsetItems.Count}, maxItems {maxItems?.ToString() ?? "n/a"}) - this is a calibration/subset run; gate skipped. " +
                    $"Set {MinPassRateEnvironmentVariable} to force a threshold.");
            }
        }

        var result = await runner.RunAsync(
            GoldsetName,
            modelId,
            maxItems,
            userId: Guid.NewGuid().ToString(),
            userRights: [AdminRight]);

        result.Dimensions.ShouldNotBeNull();
        result.Dimensions!.ItemsTotal.ShouldBeGreaterThan(0);

        WriteScorecard(modelId, result);

        var passRate = ComputePassRate(result.Dimensions);
        if (forcedMinPassRate.HasValue)
        {
            passRate.ShouldBeGreaterThanOrEqualTo(
                forcedMinPassRate.Value,
                $"turn-selection pass rate {passRate:P1} below forced gate {forcedMinPassRate:P1} " +
                $"(env {MinPassRateEnvironmentVariable}).");
        }
        else if (baselineThreshold.HasValue)
        {
            passRate.ShouldBeGreaterThanOrEqualTo(
                baselineThreshold.Value,
                $"turn-selection pass rate {passRate:P1} regressed below baseline gate {baselineThreshold:P1} " +
                $"(baseline - {BaselineTolerance:P0}).");
        }
    }

    private static double? ReadForcedMinPassRate()
    {
        var raw = Environment.GetEnvironmentVariable(MinPassRateEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0.0 || parsed > 1.0)
        {
            throw new InvalidOperationException(
                $"{MinPassRateEnvironmentVariable} must be a number between 0.0 and 1.0, got '{raw}'.");
        }

        return parsed;
    }

    private static double ComputePassRate(TurnEvalDimensions dimensions)
    {
        var activeItems = Math.Max(1, dimensions.ItemsTotal - dimensions.ItemsExcluded);
        return (double)dimensions.ItemsPassed / activeItems;
    }

    private static void WriteScorecard(string modelId, TurnEvalRunResult result)
    {
        var dimensions = result.Dimensions!;

        TestContext.WriteLine($"Goldset:                {GoldsetName}");
        TestContext.WriteLine($"Model:                  {modelId} (provider: {result.Run.Provider})");
        TestContext.WriteLine($"Composite:              {result.Run.CompositeScore:F4}");
        TestContext.WriteLine($"Regression vs baseline: {result.Run.RegressionVsBaseline?.ToString("F4") ?? "n/a"}");
        TestContext.WriteLine($"ToolAccuracy:           {Format(dimensions.ToolAccuracy)}");
        TestContext.WriteLine($"SlotAccuracy:           {Format(dimensions.SlotAccuracy)}");
        TestContext.WriteLine($"NoToolAccuracy:         {Format(dimensions.NoToolAccuracy)}");
        TestContext.WriteLine($"NameResolutionAccuracy: {Format(dimensions.NameResolutionAccuracy)}");
        TestContext.WriteLine($"AvgLatencyMs:           {dimensions.AvgLatencyMs:F0}");
        TestContext.WriteLine($"TotalCost:              {dimensions.TotalCost:F4}");
        TestContext.WriteLine(
            $"Items:                  total={dimensions.ItemsTotal}, passed={dimensions.ItemsPassed}, " +
            $"excluded={dimensions.ItemsExcluded}, errored={dimensions.ItemsErrored}");
        TestContext.WriteLine(string.Empty);

        foreach (var item in result.Items.Where(i => !i.Passed))
        {
            TestContext.WriteLine(
                $"MISS {item.ItemId}: expected={item.ExpectedTool ?? "(none)"}, chosen={item.ChosenTool ?? "(none)"}, " +
                $"slotScore={Format(item.SlotScore)}, toolAvailable={item.ExpectedToolAvailable?.ToString() ?? "n/a"}, " +
                $"recipe={item.EngineRecipeWouldTrigger}, excluded={item.Excluded}, errored={item.Errored}, error={item.Error ?? "-"}");
        }
    }

    private static string Format(double? value) => value?.ToString("F4") ?? "n/a";
}
