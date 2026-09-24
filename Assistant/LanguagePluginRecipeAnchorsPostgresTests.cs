// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves the pack recipe anchors against real PostgreSQL instead of EF InMemory: the AddRecipeAnchors
/// migration created agent_recipes.anchors as a nullable jsonb column; LanguagePluginService.InstallAsync
/// writes recipe-anchors.json into AgentRecipe.Anchors under the manifest spelling of the code and the lists
/// survive the jsonb round trip into a fresh scope; the startup backfill ApplyInstalledRecipeAnchorsAsync
/// restores a language key that a tracked update removed and writes nothing on a second run; and anchors
/// read back from the database let RecipeTriggerMatcher.HasSemanticAnchor accept a Spanish message the
/// core trigger alone rejects, while a Spanish note sentence stays rejected.
/// The install and backfill tests remove the pack's key before they ask the code under test to write it, because the host boot
/// already runs the backfill and would otherwise make each assertion pass without the install or backfill.
/// The packs stay installed afterwards on purpose: this runs on the shared test database, and an uninstall
/// is exactly the step that once removed zh-CN from it.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("Assistant")]
public class LanguagePluginRecipeAnchorsPostgresTests
{
    private const string SpanishCode = "es";
    private const string JapaneseCode = "ja";
    private const string SimplifiedChineseCode = "zh-CN";
    private const string UiLanguage = "de";
    private const int SeedRecipeCount = 26;

    private const string ExternRecipeName = "bulk-add-externs-to-nearest-group";
    private const string SpanishExternMessage =
        "Asigna a todos los trabajadores externos al grupo más cercano.";
    private const string SpanishPackOnlyExternMessage =
        "Asigna a todos los autónomos al grupo más cercano.";
    private const string SpanishNoteMessage = "Lee por favor mis notas pospuestas.";

    private const string AnchorsTableName = "agent_recipes";
    private const string AnchorsColumnName = "anchors";
    private const string PublicSchema = "public";
    private const string JsonbDataType = "jsonb";
    private const string NullableYes = "YES";

    private const string ColumnInfoSql =
        "SELECT data_type, is_nullable FROM information_schema.columns " +
        "WHERE table_schema = @schema AND table_name = @table AND column_name = @column";

    private const string RowVersionSql = "SELECT id, xmin::text FROM agent_recipes";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private SignalRTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [Test]
    public async Task Migration_CreatedAnchorsColumn_AsNullableJsonb()
    {
        await using var connection = new NpgsqlConnection(TestHostDatabase.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(ColumnInfoSql, connection);
        command.Parameters.AddWithValue("schema", PublicSchema);
        command.Parameters.AddWithValue("table", AnchorsTableName);
        command.Parameters.AddWithValue("column", AnchorsColumnName);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue(
            $"{AnchorsTableName}.{AnchorsColumnName} must exist - the AddRecipeAnchors migration was not applied");

        reader.GetString(0).ShouldBe(JsonbDataType);
        reader.GetString(1).ShouldBe(NullableYes);
    }

    [TestCase(SpanishCode)]
    [TestCase(JapaneseCode)]
    [TestCase(SimplifiedChineseCode)]
    public async Task InstallAsync_WritesPackAnchors_ThatRoundTripThroughJsonb(string code)
    {
        var packAnchors = ReadPackAnchors(code);
        packAnchors.Count.ShouldBe(SeedRecipeCount);

        await RemoveAnchorsAsync(code, recipeName: null);
        (await ReadRecipesAsync()).ShouldAllBe(recipe => recipe.Anchors == null || !recipe.Anchors.ContainsKey(code));

        var service = _factory.Services.GetRequiredService<ILanguagePluginService>();
        (await service.InstallAsync(code)).ShouldBeTrue();

        var recipes = await ReadRecipesAsync();
        var seeds = recipes.Where(recipe => recipe.Origin == AgentRecipeOrigins.Seed).ToList();
        seeds.Select(recipe => recipe.Name).ShouldBe(packAnchors.Keys, ignoreOrder: true);
        seeds.ShouldAllBe(recipe => recipe.IsEnabled);

        foreach (var recipe in recipes.Where(recipe => packAnchors.ContainsKey(recipe.Name)))
        {
            recipe.Anchors.ShouldNotBeNull($"recipe '{recipe.Name}' has no anchors after installing '{code}'");
            recipe.Anchors!.Keys
                .Where(key => string.Equals(key, code, StringComparison.OrdinalIgnoreCase))
                .ShouldBe(new[] { code }, $"recipe '{recipe.Name}' must carry the manifest spelling '{code}' only");
            recipe.Anchors[code].ShouldNotBeEmpty($"recipe '{recipe.Name}' has an empty '{code}' list");
            recipe.Anchors[code].ShouldBe(packAnchors[recipe.Name], $"recipe '{recipe.Name}', language '{code}'");
        }

        service.GetInstalledPluginCodes().ShouldContain(code);
    }

    [Test]
    public async Task ApplyInstalledRecipeAnchorsAsync_RestoresARemovedLanguageKey_AndIsIdempotent()
    {
        var service = _factory.Services.GetRequiredService<ILanguagePluginService>();
        await EnsureInstalledAsync(service, SpanishCode);
        var expected = ReadPackAnchors(SpanishCode)[ExternRecipeName];

        await RemoveAnchorsAsync(SpanishCode, ExternRecipeName);
        var blanked = await ReadRecipeAsync(ExternRecipeName);
        (blanked.Anchors?.ContainsKey(SpanishCode) ?? false).ShouldBeFalse(
            "the tracked update must have removed the key before the backfill runs");

        var versionsBeforeBackfill = await ReadRowVersionsAsync();
        await service.ApplyInstalledRecipeAnchorsAsync();

        var restored = await ReadRecipeAsync(ExternRecipeName);
        restored.Anchors.ShouldNotBeNull();
        restored.Anchors!.ShouldContainKey(SpanishCode);
        restored.Anchors[SpanishCode].ShouldBe(expected);

        var versionsAfterBackfill = await ReadRowVersionsAsync();
        ChangedRows(versionsBeforeBackfill, versionsAfterBackfill).ShouldBe(new List<Guid> { restored.Id },
            customMessage: "the backfill must rewrite only the recipe whose key was removed");

        await service.ApplyInstalledRecipeAnchorsAsync();

        var versionsAfterSecondBackfill = await ReadRowVersionsAsync();
        ChangedRows(versionsAfterBackfill, versionsAfterSecondBackfill).ShouldBeEmpty(
            "a second backfill over current anchors must not write any recipe");
    }

    [Test]
    public async Task HasSemanticAnchor_AcceptsASpanishSubject_OnlyThroughDatabaseLoadedAnchors()
    {
        var service = _factory.Services.GetRequiredService<ILanguagePluginService>();
        await EnsureInstalledAsync(service, SpanishCode);

        var recipe = await ReadRecipeAsync(ExternRecipeName);
        var trigger = JsonSerializer.Deserialize<RecipeTrigger>(recipe.TriggerJson, JsonOptions);
        trigger.ShouldNotBeNull();
        var packAnchors = recipe.AllAnchors();
        packAnchors.ShouldContainKey(SpanishCode);

        RecipeTriggerMatcher.HasSemanticAnchor(
                trigger, SpanishExternMessage, language: UiLanguage, packAnchors: packAnchors)
            .ShouldBeTrue();

        RecipeTriggerMatcher.HasSemanticAnchor(
                trigger, SpanishPackOnlyExternMessage, language: UiLanguage, packAnchors: null)
            .ShouldBeFalse("the core de/en/fr/it trigger must not name this Spanish subject on its own");
        RecipeTriggerMatcher.HasSemanticAnchor(
                trigger, SpanishPackOnlyExternMessage, language: UiLanguage, packAnchors: packAnchors)
            .ShouldBeTrue("the Spanish anchors read back from PostgreSQL must name the subject");

        RecipeTriggerMatcher.HasSemanticAnchor(
                trigger, SpanishNoteMessage, language: UiLanguage, packAnchors: packAnchors)
            .ShouldBeFalse("no installed pack anchor may let a note sentence through");
    }

    private static Dictionary<string, List<string>> ReadPackAnchors(string code)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            LanguagePluginConstants.PluginDirectory,
            code,
            LanguagePluginConstants.RecipeAnchorsFileName);
        File.Exists(path).ShouldBeTrue($"'{path}' must be copied to the test output, otherwise the install is a no-op");

        var anchors = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), JsonOptions);
        anchors.ShouldNotBeNull();
        return anchors!;
    }

    private static async Task EnsureInstalledAsync(ILanguagePluginService service, string code)
    {
        if (!service.GetInstalledPluginCodes().Contains(code))
        {
            (await service.InstallAsync(code)).ShouldBeTrue();
        }
    }

    private async Task RemoveAnchorsAsync(string code, string? recipeName)
    {
        using var scope = _factory.Services.CreateScope();
        var recipeRepo = scope.ServiceProvider.GetRequiredService<IAgentRecipeRepository>();
        var recipes = await recipeRepo.GetAllAsync();

        foreach (var recipe in recipes.Where(recipe => recipeName == null || recipe.Name == recipeName))
        {
            if (recipe.Anchors == null || !recipe.Anchors.Remove(code))
            {
                continue;
            }

            await recipeRepo.UpdateAsync(recipe);
        }
    }

    private async Task<List<AgentRecipe>> ReadRecipesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var recipeRepo = scope.ServiceProvider.GetRequiredService<IAgentRecipeRepository>();
        return await recipeRepo.GetAllAsync();
    }

    private async Task<AgentRecipe> ReadRecipeAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var recipeRepo = scope.ServiceProvider.GetRequiredService<IAgentRecipeRepository>();
        var recipe = await recipeRepo.GetByNameAsync(name);
        recipe.ShouldNotBeNull($"recipe '{name}' must exist");
        return recipe!;
    }

    private static async Task<Dictionary<Guid, string>> ReadRowVersionsAsync()
    {
        await using var connection = new NpgsqlConnection(TestHostDatabase.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(RowVersionSql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var versions = new Dictionary<Guid, string>();
        while (await reader.ReadAsync())
        {
            versions[reader.GetGuid(0)] = reader.GetString(1);
        }

        return versions;
    }

    private static List<Guid> ChangedRows(Dictionary<Guid, string> before, Dictionary<Guid, string> after) =>
        after
            .Where(entry => !before.TryGetValue(entry.Key, out var version) || version != entry.Value)
            .Select(entry => entry.Key)
            .ToList();
}
