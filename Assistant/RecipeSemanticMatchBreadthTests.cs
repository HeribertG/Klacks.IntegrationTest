// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Reporting probe for the breadth of the semantic recipe fallback: runs every
/// turn-selection goldset message through the same matching stages production uses
/// (deterministic trigger, information-question suppressor, semantic retrieval with the
/// grey-zone floor) and reports which messages would end up in a recipe confirmation
/// gate. Read-only apart from the embedding index lookups; no LLM calls.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;
using System.Text.Json;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Explicit("Loads the ONNX embedding index and reads the real DB on port 5434. Reporting only, no LLM calls.")]
[Category("RealDatabase")]
[Category("SlowModelLoad")]
public class RecipeSemanticMatchBreadthTests
{
    private const string GoldsetName = "turn-selection-v1";
    private const double GreyZoneFloor = 0.4;
    private const double HighConfidence = 0.7;
    private const int SemanticTopK = 3;

    private static readonly JsonSerializerOptions TriggerOptions = new() { PropertyNameCaseInsensitive = true };

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
    public async Task SemanticRecipeFallback_BreadthReport_OverGoldsetMessages()
    {
        using var scope = _factory.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<ITurnGoldsetLoader>();
        var recipeRepository = scope.ServiceProvider.GetRequiredService<IAgentRecipeRepository>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();

        var items = await loader.LoadAsync(GoldsetName);
        var recipes = await recipeRepository.GetAllEnabledAsync();

        int deterministic = 0, suppressed = 0, semanticHigh = 0, semanticGrey = 0, none = 0;

        foreach (var item in items)
        {
            var trigger = MatchDeterministic(recipes, item.Message, item.Locale);
            if (trigger != null)
            {
                deterministic++;
                TestContext.WriteLine($"{item.Id}: DETERMINISTIC -> {trigger} | \"{item.Message}\"");
                continue;
            }

            if (MutationIntentDetector.IsInformationQuestion(item.Message))
            {
                suppressed++;
                TestContext.WriteLine($"{item.Id}: SUPPRESSED (information question) | \"{item.Message}\"");
                continue;
            }

            var result = await retrieval.RetrieveAsync(item.Message, [], isAdmin: false, SemanticTopK, currentRoute: null, CancellationToken.None);
            var top = result.Candidates
                .Where(c => c.Entry.Kind == KnowledgeEntryKind.Recipe)
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();

            if (top == null || top.Score < GreyZoneFloor)
            {
                none++;
                TestContext.WriteLine($"{item.Id}: none (top={top?.Score ?? 0:F3}) | \"{item.Message}\"");
            }
            else if (top.Score >= HighConfidence)
            {
                semanticHigh++;
                TestContext.WriteLine($"{item.Id}: SEMANTIC-HIGH {top.Entry.SourceId}={top.Score:F3} | \"{item.Message}\"");
            }
            else
            {
                semanticGrey++;
                TestContext.WriteLine($"{item.Id}: SEMANTIC-GREY {top.Entry.SourceId}={top.Score:F3} | \"{item.Message}\"");
            }
        }

        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine(
            $"SUMMARY: items={items.Count}, deterministic={deterministic}, suppressedQuestions={suppressed}, " +
            $"semanticHigh={semanticHigh}, semanticGrey={semanticGrey}, none={none}");
        TestContext.WriteLine(
            $"Confirmation-gate rate (semantic matches / non-deterministic items): " +
            $"{(double)(semanticHigh + semanticGrey) / Math.Max(1, items.Count - deterministic):P0}");

        items.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task SemanticRecipeFallback_TruePositiveProbe_TersePhrasings()
    {
        var probes = new (string Message, string ExpectedRecipe)[]
        {
            ("Neuen Mitarbeiter, bitte", "onboard-employee"),
            ("Wir haben einen neuen Kollegen", "onboard-employee"),
            ("Frau Müller braucht nächste Woche Ferien", "add-absence-for-employee"),
            ("Ich bräuchte noch eine Gruppe für die Nachtwache", "create-group"),
            ("Der Herr Meier gehört noch ins Team Bern", "add-employee-to-group"),
            ("une nouvelle employée commence lundi", "onboard-employee"),
            ("new hire starting next month", "onboard-employee")
        };

        using var scope = _factory.Services.CreateScope();
        var recipeRepository = scope.ServiceProvider.GetRequiredService<IAgentRecipeRepository>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var recipes = await recipeRepository.GetAllEnabledAsync();

        foreach (var probe in probes)
        {
            var trigger = MatchDeterministic(recipes, probe.Message, null);
            var result = await retrieval.RetrieveAsync(probe.Message, [], isAdmin: false, SemanticTopK, currentRoute: null, CancellationToken.None);
            var top = result.Candidates
                .Where(c => c.Entry.Kind == KnowledgeEntryKind.Recipe)
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();

            TestContext.WriteLine(
                $"expected={probe.ExpectedRecipe,-28} deterministic={trigger ?? "-",-28} " +
                $"semanticTop={top?.Entry.SourceId ?? "-"}={top?.Score ?? 0:F3} | \"{probe.Message}\"");
        }

        probes.Length.ShouldBeGreaterThan(0);
    }

    private static string? MatchDeterministic(List<AgentRecipe> recipes, string message, string? language)
    {
        foreach (var recipe in recipes)
        {
            RecipeTrigger? trigger;
            try
            {
                trigger = JsonSerializer.Deserialize<RecipeTrigger>(recipe.TriggerJson, TriggerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            IReadOnlyCollection<string>? synonyms = null;
            if (language != null && recipe.Synonyms != null && recipe.Synonyms.TryGetValue(language, out var languageSynonyms))
            {
                synonyms = languageSynonyms;
            }

            if (trigger != null && RecipeTriggerMatcher.Matches(trigger, synonyms, message))
            {
                return recipe.Name;
            }
        }

        return null;
    }
}
