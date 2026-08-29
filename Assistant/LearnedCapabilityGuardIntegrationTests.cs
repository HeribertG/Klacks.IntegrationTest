// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves the two guards in front of a learned capability against the REAL corpus, which is the only
/// place they can be proved at all.
///
/// (1) THE TRIGGER CORPUS. RecipeDraftValidator has to keep a composed recipe disjoint from every recipe
///     that already exists. The unit suite can only show that against two or three invented triggers;
///     whether a realistic wording collides with the twenty-odd seeded flows is a property of the seed
///     corpus, and the seed corpus lives in agent_recipes. This matters more than its size suggests: a
///     recipe forces its step skill deterministically, ahead of any function calling, so a trigger one
///     word too generic steals real turns silently - the live incident of 2026-07-16.
///
/// (2) THE RISK CATALOGUE. SkillExecutionOracle refuses to compose anything the risk classifier does not
///     place in ReadOnly or Reversible. Whether the skills a real installation actually offers are rated
///     the way the oracle assumes is a property of the live agent_skills rows, not of a substitute.
///
/// The fixture WRITES NOTHING. It reads agent_recipes and agent_skills and runs pure judgement over
/// them, so there is nothing to clean up and nothing the dev application sharing this database could
/// notice. That is deliberate: the dev app and the integration tests share port 5434.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class LearnedCapabilityGuardIntegrationTests
{
    private const string TestSlug = "integration-test-composed-report";

    [Test]
    public async Task ADraftBuiltFromASeededRecipesOwnTriggerWords_IsRejectedByName()
    {
        var recipes = await LoadEnabledRecipesAsync();
        Assume.That(recipes.Count, Is.GreaterThan(0), "No enabled recipes in the database to judge against.");

        var victim = recipes.First(recipe => TriggerStems(recipe).Count >= 2);
        var stems = TriggerStems(victim).Take(2).ToList();

        var verdict = Validator().Validate(Draft(stems), recipes, []);

        verdict.IsAccepted.ShouldBeFalse(
            $"A draft made of '{victim.Name}' own trigger words must not be allowed to exist beside it.");
        verdict.Error.ShouldContain(victim.Name);
    }

    // The complement of the test above: the guard must reject collisions without rejecting everything,
    // otherwise no capability could ever be learned and the rejection above would prove nothing.
    [Test]
    public async Task ADraftWithVocabularyNoSeededRecipeUses_IsAccepted()
    {
        var recipes = await LoadEnabledRecipesAsync();

        var verdict = Validator().Validate(
            Draft(["quartalskennzahl", "auslastungsprofil"]), recipes, []);

        verdict.IsAccepted.ShouldBeTrue(verdict.Error);
        verdict.Name.ShouldBe(SkillLearningDefaults.LearnedRecipeNamePrefix + TestSlug);
    }

    // Every learned recipe carries the question guard, so the plain questions users actually ask must
    // not start it. Checked against the real matcher, which is what the engine runs.
    [TestCase("Welche Kennzahlen zum Auslastungsprofil gibt es?")]
    [TestCase("Wann wird die Quartalskennzahl berechnet?")]
    [TestCase("Wie sieht das Auslastungsprofil aus?")]
    public async Task APlainQuestion_DoesNotStartALearnedCapability(string question)
    {
        var recipes = await LoadEnabledRecipesAsync();

        var verdict = Validator().Validate(
            Draft(["quartalskennzahl", "auslastungsprofil"]), recipes, []);

        verdict.IsAccepted.ShouldBeTrue(verdict.Error);
        Klacks.Api.Domain.Services.Assistant.RecipeTriggerMatcher
            .Matches(verdict.Trigger!, question)
            .ShouldBeFalse();
    }

    // The oracle's premise, stated against the live catalogue rather than against a substitute: the
    // skills a capability may be composed from are exactly those the classifier rates ReadOnly or
    // Reversible, and a real installation must contain some of each side of that line.
    [Test]
    public async Task TheLiveSkillCatalogue_ContainsBothComposableAndRefusedSkills()
    {
        var descriptors = await LoadSkillDescriptorsAsync();
        Assume.That(descriptors.Count, Is.GreaterThan(0), "No enabled skills in the database to judge.");

        var classifier = new SkillRiskClassifier();
        var byClass = descriptors
            .GroupBy(classifier.Classify)
            .ToDictionary(group => group.Key, group => group.Count());

        var composable = byClass.GetValueOrDefault(SkillRiskClass.ReadOnly)
            + byClass.GetValueOrDefault(SkillRiskClass.Reversible);
        var refused = descriptors.Count - composable;

        composable.ShouldBeGreaterThan(
            0, "No skill in the live catalogue may be composed, so no capability could ever be learned.");
        refused.ShouldBeGreaterThan(
            0, "Every skill in the live catalogue is composable, which means the guard is not guarding.");
    }

    private static RecipeDraftValidator Validator() =>
        new(Substitute.For<ISkillRegistry>(), NullLogger<RecipeDraftValidator>.Instance);

    private static LearnedRecipeDraft Draft(IReadOnlyList<string> stems) =>
        new(
            TestSlug,
            "Report the composed figures",
            new Dictionary<string, string>
            {
                ["de"] = "Zusammengesetzte Kennzahlen melden",
                ["en"] = "Report the composed figures",
                ["fr"] = "Signaler les chiffres composés",
                ["it"] = "Segnalare i valori composti"
            },
            new RecipeTrigger
            {
                AllOf = [.. stems.Select(stem => new RecipeCondition { AnyWordStart = [stem] })]
            },
            [new RecipeStep { Kind = RecipeStepKinds.Search, Skill = "list_clients" }]);

    private static List<string> TriggerStems(AgentRecipe recipe)
    {
        var trigger = System.Text.Json.JsonSerializer.Deserialize<RecipeTrigger>(
            recipe.TriggerJson,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return trigger == null
            ? []
            : Klacks.Api.Domain.Services.Assistant.RecipeTriggerWordExtractor.Extract(trigger)
                .Where(stem => stem.Length >= SkillLearningDefaults.MinTriggerStemLength)
                .ToList();
    }

    private static async Task<List<AgentRecipe>> LoadEnabledRecipesAsync()
    {
        await using var context = NewContext();
        return await context.AgentRecipes.AsNoTracking().Where(r => r.IsEnabled).ToListAsync();
    }

    private static async Task<List<SkillDescriptor>> LoadSkillDescriptorsAsync()
    {
        await using var context = NewContext();

        var skills = await context.AgentSkills
            .AsNoTracking()
            .Where(s => s.IsEnabled)
            .ToListAsync();

        return skills
            .Select(skill => new SkillDescriptor(
                skill.Name,
                skill.Description,
                Enum.TryParse<SkillCategory>(skill.Category, ignoreCase: true, out var category)
                    ? category
                    : SkillCategory.Action,
                [],
                [],
                [],
                null))
            .ToList();
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }
}
