// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
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
    private const string DefaultModelId = "gpt-54-mini";
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
        var modelId = Environment.GetEnvironmentVariable(ModelIdEnvironmentVariable) ?? DefaultModelId;
        int? maxItems = int.TryParse(
            Environment.GetEnvironmentVariable(MaxItemsEnvironmentVariable), out var parsedMaxItems) && parsedMaxItems > 0
            ? parsedMaxItems
            : null;

        using var scope = _factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ITurnEvalRunnerService>();

        var result = await runner.RunAsync(
            GoldsetName,
            modelId,
            maxItems,
            userId: Guid.NewGuid().ToString(),
            userRights: [AdminRight]);

        result.Dimensions.ShouldNotBeNull();
        result.Dimensions!.ItemsTotal.ShouldBeGreaterThan(0);

        WriteScorecard(modelId, result);
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
