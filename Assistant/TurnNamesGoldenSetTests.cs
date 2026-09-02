// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live eval of the turn-names goldset (name resolution accuracy). Seeds the eight test
/// persons through the normal EF path so the database sequence assigns their id numbers
/// (id_number must NEVER be set via SQL or from the outside), then remaps the placeholder
/// id numbers of the goldset (990001-990008) to the actually assigned ones in memory
/// before running. Seeded rows are marked via the Company field and cleaned up ONLY by
/// that marker.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Explicit("Performs real LLM provider calls (costs money), seeds marker-tagged clients into the real DB on port 5434 and writes an EvalRun. Run manually only.")]
[Category("Llm")]
[Category("RealDatabase")]
public class TurnNamesGoldenSetTests
{
    private const string GoldsetName = "turn-names-v1";
    private const string AdminRight = "Admin";
    private const string SeedMarker = "INTEGRATION_TEST_TURNEVAL";

    private static readonly (int Placeholder, string FirstName, string LastName)[] SeedPersons =
    {
        (990001, "Vreni", "Müller"),
        (990002, "Hans-Peter", "Brönnimann"),
        (990003, "Céline", "Fässler"),
        (990004, "José", "Gonçalves"),
        (990005, "François", "Dubois-Marchand"),
        (990006, "Annelies", "Grüter"),
        (990007, "Jürg", "Läderach"),
        (990008, "Mirjam", "Nussbaumer")
    };

    private SignalRTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await CleanupSeededClientsAsync();
        _factory?.Dispose();
    }

    [Test]
    public async Task TurnNamesGoldset_SeedsPersonsRemapsPlaceholdersAndReportsNameResolution()
    {
        var modelId = TurnEvalPassRateGate.ResolveModelId();

        await CleanupSeededClientsAsync();
        var placeholderToIdNumber = await SeedPersonsViaEfAsync();

        using var scope = _factory.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<ITurnGoldsetLoader>();
        var runner = scope.ServiceProvider.GetRequiredService<ITurnEvalRunnerService>();

        var items = await loader.LoadAsync(GoldsetName);
        var remapped = RemapPlaceholders(items, placeholderToIdNumber);

        // Threshold BEFORE the run so the freshly persisted EvalRun is never its own baseline.
        var threshold = await TurnEvalPassRateGate.ResolveThresholdAsync(
            scope.ServiceProvider.GetRequiredService<IEvalRunRepository>(),
            GoldsetName,
            modelId,
            remapped.Count,
            isPartial: false);

        var result = await runner.RunAsync(
            GoldsetName,
            remapped,
            modelId,
            maxItems: null,
            userId: Guid.NewGuid().ToString(),
            userRights: [AdminRight]);

        result.Dimensions.ShouldNotBeNull();

        // The item count stays a precondition: it is what makes the pass rate below comparable.
        result.Dimensions!.ItemsTotal.ShouldBe(SeedPersons.Length);

        WriteScorecard(modelId, result);

        var passRate = TurnEvalPassRateGate.ComputePassRate(result.Dimensions);
        passRate.ShouldBeGreaterThanOrEqualTo(
            threshold,
            $"turn-names pass rate {passRate:P1} below the gate {threshold:P1}.");
    }

    private async Task<Dictionary<int, int>> SeedPersonsViaEfAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();

        foreach (var person in SeedPersons)
        {
            context.Client.Add(new Client
            {
                Id = Guid.NewGuid(),
                FirstName = person.FirstName,
                Name = person.LastName,
                Company = SeedMarker,
                Gender = GenderEnum.Female,
                Type = EntityTypeEnum.Employee,
                CreateTime = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();

        var seeded = await context.Client
            .Where(c => c.Company == SeedMarker)
            .Select(c => new { c.FirstName, c.Name, c.IdNumber })
            .ToListAsync();

        var mapping = new Dictionary<int, int>();
        foreach (var person in SeedPersons)
        {
            var client = seeded.SingleOrDefault(c => c.FirstName == person.FirstName && c.Name == person.LastName);
            client.ShouldNotBeNull($"Seeded person {person.FirstName} {person.LastName} not found after SaveChanges.");
            client!.IdNumber.ShouldBeGreaterThan(0);
            mapping[person.Placeholder] = client.IdNumber;
        }

        return mapping;
    }

    private static List<TurnGoldsetItem> RemapPlaceholders(
        IReadOnlyList<TurnGoldsetItem> items,
        Dictionary<int, int> placeholderToIdNumber)
    {
        foreach (var slot in items.SelectMany(i => i.ExpectedSlots))
        {
            if (slot.Entity != null && placeholderToIdNumber.TryGetValue(slot.Entity.IdNumber, out var actualIdNumber))
            {
                slot.Entity = slot.Entity with { IdNumber = actualIdNumber };
            }
        }

        return items.ToList();
    }

    private async Task CleanupSeededClientsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();

        var stale = await context.Client
            .IgnoreQueryFilters()
            .Where(c => c.Company == SeedMarker)
            .ToListAsync();

        if (stale.Count > 0)
        {
            context.Client.RemoveRange(stale);
            await context.SaveChangesAsync();
        }
    }

    private static void WriteScorecard(string modelId, TurnEvalRunResult result)
    {
        var dimensions = result.Dimensions!;

        TestContext.WriteLine($"Goldset:                {GoldsetName}");
        TestContext.WriteLine($"Model:                  {modelId} (provider: {result.Run.Provider})");
        TestContext.WriteLine($"ScorerVersion:          {result.Run.ScorerVersion} (partial run: {result.Run.IsPartial})");
        TestContext.WriteLine($"Composite:              {result.Run.CompositeScore:F4}");
        TestContext.WriteLine($"ToolAccuracy:           {Format(dimensions.ToolAccuracy)}");
        TestContext.WriteLine($"SlotAccuracy:           {Format(dimensions.SlotAccuracy)}");
        TestContext.WriteLine($"NameResolutionAccuracy: {Format(dimensions.NameResolutionAccuracy)}");
        TestContext.WriteLine($"AvgLatencyMs:           {dimensions.AvgLatencyMs:F0} (reported only, not in the composite)");
        TestContext.WriteLine(
            $"Items:                  total={dimensions.ItemsTotal}, passed={dimensions.ItemsPassed}, " +
            $"excluded={dimensions.ItemsExcluded}, errored={dimensions.ItemsErrored}");

        foreach (var item in result.Items.Where(i => !i.Passed))
        {
            TestContext.WriteLine(
                $"MISS {item.ItemId}: expected={item.ExpectedTool ?? "(none)"}, chosen={item.ChosenTool ?? "(none)"}, " +
                $"slotScore={Format(item.SlotScore)}, nameSlots={item.NameSlotsResolved}/{item.NameSlotsEvaluated}, " +
                $"excluded={item.Excluded}, errored={item.Errored}, error={item.Error ?? "-"}");
        }
    }

    private static string Format(double? value) => value?.ToString("F4") ?? "n/a";
}
