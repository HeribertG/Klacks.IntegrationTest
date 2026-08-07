// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The recipe half of the retrieval measurement. Until 2026-08-06 nothing measured it with an
/// assertion: no golden set contained a recipe target, and RecipeSemanticMatchBreadthTests is
/// report-only, so a recipe regression could not turn anything red.
///
/// Runs the path production runs (RecipeEngineService.FindMatchingRecipeSemanticAsync): the raw user
/// message rather than the history-widened query, no permissions, not admin, top-SemanticMatchTopK,
/// filtered to KnowledgeEntryKind.Recipe. Anything else would measure a pipeline nobody uses.
///
/// The assertion floor is the measured state, not a target. It exists to catch a regression, so it
/// must not be red on the day it is written; raise it when a change earns it.
/// </summary>
namespace Klacks.IntegrationTest.KnowledgeIndex;

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Shouldly;

[TestFixture]
[Category("KnowledgeIndex")]
public class KnowledgeIndexRecipeGoldenSetDiHostTests
{
    // Measured 2026-08-06 on the then 72-case set: top-1 52, recall@3 66. The floors sit a few cases
    // below that so index jitter does not turn the build red while a real regression still does.
    // On 2026-08-07 the set grew to 79 cases (the seven historical terse probes taken over from
    // RecipeSemanticMatchBreadthTests) and the floors were deliberately left untouched: they are
    // absolute hit counts and retrieval runs once per case, so a larger set can only raise the counts.
    // A floor measured on a subset therefore stays valid on a superset. For the same reason the
    // percentages of two differently sized runs are not comparable - only the counts are. Raise the
    // floors when a change earns it - never to make a failing run pass.
    private const int MinimumTop1Hits = 48;
    private const int MinimumTopKHits = 62;

    // Mirrors RecipeEngineService, which keeps them private so the measurement build never depends on
    // that file compiling. Update both together - a stale copy here measures a boundary production
    // no longer applies.
    private const double SemanticHighConfidenceLogThreshold = 0.7;
    private const double SemanticGreyZoneThreshold = 0.4;
    private const double SemanticAmbiguityMargin = 0.05;
    private const int SemanticMatchTopK = 3;

    // The three outcomes of the gate above, as they are written to the report and as the optional
    // "expectedClassification" field of the golden set spells them.
    private const string ClassificationHigh = "high";
    private const string ClassificationGrey = "grey";
    private const string ClassificationNone = "none";

    private const string CaseLinePrefix = "CASE |";
    private const string ClassificationMismatchPrefix = "CLASSIFICATION MISMATCH |";

    private static readonly string RecipeSetPath = Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "KnowledgeIndex",
        "knowledge-index-golden-recipes.json");

    private sealed class RecipeBypassFactory : SignalRTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                // Same reason as in the skill golden set: the startup synchronizer would rewrite the
                // stored vectors under whatever embedding space this process happens to load.
                services.RemoveAll<IKnowledgeIndexSynchronizer>();
                services.AddScoped<IKnowledgeIndexSynchronizer, NoOpSynchronizer>();
                services.RemoveAll<IRegionSetupService>();
                services.AddScoped<IRegionSetupService, NoOpRegionSetup>();
            });
        }
    }

    private sealed class NoOpSynchronizer : IKnowledgeIndexSynchronizer
    {
        public Task SyncAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpRegionSetup : IRegionSetupService
    {
        public Task ApplyAsync() => Task.CompletedTask;
    }

    // ExpectedClassification is optional and report-only: null means the golden set makes no
    // statement about the gate outcome for that case and nothing is compared.
    private sealed record RecipeCase(
        string Query,
        string ExpectedSourceId,
        string LangCode,
        string Phrasing,
        string? ExpectedClassification);

    private RecipeBypassFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new RecipeBypassFactory();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory?.Dispose();

    [Test]
    public async Task RecipeGoldenSet_RecallAndGateDistribution_ViaRealHostDi()
    {
        using var scope = _factory.Services.CreateScope();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");
        TestContext.WriteLine("=== RECIPE GOLDEN SET (production recipe fallback path) ===");
        // Deliberately spells the case marker apart instead of interpolating the constant: the marker
        // must occur exactly once per case in the output, so a header carrying it would show up in an
        // extraction as one extra case made of placeholders.
        TestContext.WriteLine(
            $"--- per case, one line marked CASE followed by a pipe, fields in fixed order: " +
            $"expected=<sourceId>, [phrasing/langCode], " +
            $"classification=<{ClassificationHigh}/{ClassificationGrey}/{ClassificationNone}>, " +
            $"score=<best candidate>, top1=<true/false>, rank=<0-based position of the expected " +
            $"recipe, -1 = not in the top {SemanticMatchTopK}>, quoted query last ---");

        var cases = LoadCases();
        var top1Hits = 0;
        var topKHits = 0;
        var highConfidence = 0;
        var greyZone = 0;
        var belowGate = 0;
        var ambiguous = 0;
        var empty = 0;
        var perPhrasingHits = new Dictionary<string, int>();
        var perPhrasingTotal = new Dictionary<string, int>();
        var targetScores = new List<double>();
        var misses = new List<string>();

        foreach (var item in cases)
        {
            perPhrasingTotal[item.Phrasing] = perPhrasingTotal.GetValueOrDefault(item.Phrasing) + 1;

            var result = await retrieval.RetrieveAsync(
                item.Query, [], isAdmin: false, SemanticMatchTopK, currentRoute: null,
                CancellationToken.None, KnowledgeEntryKind.Recipe);

            var candidates = result.Candidates;
            if (candidates.Count == 0)
            {
                empty++;
                misses.Add($"EMPTY | expected={item.ExpectedSourceId} | [{item.Phrasing}/{item.LangCode}] \"{item.Query}\"");
                ReportCase(item, ClassificationNone, 0d, isTop1: false, rank: -1);
                continue;
            }

            var isTop1 = Matches(candidates[0].Entry.SourceId, item.ExpectedSourceId);
            if (isTop1)
            {
                top1Hits++;
            }

            var rank = candidates.ToList().FindIndex(c => Matches(c.Entry.SourceId, item.ExpectedSourceId));
            if (rank >= 0)
            {
                topKHits++;
                perPhrasingHits[item.Phrasing] = perPhrasingHits.GetValueOrDefault(item.Phrasing) + 1;
                targetScores.Add(candidates[rank].Score);
            }
            else
            {
                var top = string.Join(", ", candidates.Select(c => $"{c.Entry.SourceId}={c.Score:F4}"));
                misses.Add($"MISS | expected={item.ExpectedSourceId} | [{item.Phrasing}/{item.LangCode}] \"{item.Query}\" | got [{top}]");
            }

            // The gate the engine applies to the BEST candidate, whichever recipe that is. This is
            // what decides between running a recipe, asking a confirmation question, and doing
            // nothing - so it is measured on the top candidate, not on the expected one.
            var best = candidates[0].Score;
            string classification;
            if (best >= SemanticHighConfidenceLogThreshold)
            {
                classification = ClassificationHigh;
                highConfidence++;
            }
            else if (best >= SemanticGreyZoneThreshold)
            {
                classification = ClassificationGrey;
                greyZone++;
            }
            else
            {
                classification = ClassificationNone;
                belowGate++;
            }

            if (candidates.Count > 1 &&
                best - candidates[1].Score < SemanticAmbiguityMargin)
            {
                ambiguous++;
            }

            ReportCase(item, classification, best, isTop1, rank);
        }

        TestContext.WriteLine(
            $"Recipe top-1: {top1Hits}/{cases.Count} = {(double)top1Hits / cases.Count:P1}");
        TestContext.WriteLine(
            $"Recipe recall@{SemanticMatchTopK}: {topKHits}/{cases.Count} = " +
            $"{(double)topKHits / cases.Count:P1}");

        TestContext.WriteLine($"--- recall@{SemanticMatchTopK} per phrasing ---");
        foreach (var phrasing in perPhrasingTotal.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var hits = perPhrasingHits.GetValueOrDefault(phrasing);
            TestContext.WriteLine(
                $"  {phrasing}: {hits}/{perPhrasingTotal[phrasing]} = " +
                $"{(double)hits / perPhrasingTotal[phrasing]:P1}");
        }

        TestContext.WriteLine(
            $"--- gate distribution on the best candidate (high >= " +
            $"{SemanticHighConfidenceLogThreshold}, grey >= " +
            $"{SemanticGreyZoneThreshold}) ---");
        TestContext.WriteLine($"  high confidence: {highConfidence}/{cases.Count}");
        TestContext.WriteLine($"  grey zone (asks a confirmation question): {greyZone}/{cases.Count}");
        TestContext.WriteLine($"  below the gate (no recipe at all): {belowGate}/{cases.Count}");
        TestContext.WriteLine(
            $"  within the ambiguity margin of the runner-up " +
            $"({SemanticAmbiguityMargin}): {ambiguous}/{cases.Count}");
        TestContext.WriteLine($"  retrieval returned nothing: {empty}/{cases.Count}");

        if (targetScores.Count > 0)
        {
            var sorted = targetScores.OrderBy(s => s).ToList();
            TestContext.WriteLine(
                $"--- score of the expected recipe when found (n={sorted.Count}) --- " +
                $"min={sorted[0]:F4} p25={sorted[sorted.Count / 4]:F4} median={sorted[sorted.Count / 2]:F4} " +
                $"p75={sorted[sorted.Count * 3 / 4]:F4} max={sorted[^1]:F4} | " +
                $"below the grey-zone threshold: " +
                $"{sorted.Count(s => s < SemanticGreyZoneThreshold)}");
        }

        TestContext.WriteLine($"--- misses ({misses.Count} of {cases.Count}) ---");
        foreach (var miss in misses)
        {
            TestContext.WriteLine(miss);
        }

        cases.Count.ShouldBeGreaterThan(0);
        top1Hits.ShouldBeGreaterThanOrEqualTo(MinimumTop1Hits);
        topKHits.ShouldBeGreaterThanOrEqualTo(MinimumTopKHits);
    }

    // One machine-readable line per case. The aggregate counters hide a single case sliding across a
    // gate boundary, which is exactly the move that changes whether the engine runs a recipe, asks a
    // confirmation question or stays silent. Fixed field order with the query last, so a split on '|'
    // survives any query text. Classification and score are those of the BEST candidate - the same
    // one the gate counters use - not of the expected recipe.
    private static void ReportCase(RecipeCase item, string classification, double bestScore, bool isTop1, int rank)
    {
        TestContext.WriteLine(
            $"{CaseLinePrefix} expected={item.ExpectedSourceId} | [{item.Phrasing}/{item.LangCode}] | " +
            $"classification={classification} | score={bestScore:F4} | " +
            $"top1={isTop1.ToString().ToLowerInvariant()} | rank={rank} | \"{item.Query}\"");

        // Report only, never an assertion: the score distribution is bimodal, so a per-case
        // classification assertion would go red on index jitter around a threshold instead of on a
        // real regression. The line exists so a shift is visible in the diff of two runs.
        if (item.ExpectedClassification != null &&
            !classification.Equals(item.ExpectedClassification, StringComparison.OrdinalIgnoreCase))
        {
            TestContext.WriteLine(
                $"{ClassificationMismatchPrefix} expected={item.ExpectedClassification} | " +
                $"measured={classification} | score={bestScore:F4} | " +
                $"[{item.Phrasing}/{item.LangCode}] | \"{item.Query}\"");
        }
    }

    private static bool Matches(string sourceId, string expected) =>
        sourceId.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static List<RecipeCase> LoadCases()
    {
        var raw = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(RecipeSetPath))!;
        return raw.Select(e => new RecipeCase(
            e.GetProperty("query").GetString()!,
            e.GetProperty("expectedSourceId").GetString()!,
            e.TryGetProperty("langCode", out var lang) ? lang.GetString()! : "unknown",
            e.TryGetProperty("phrasing", out var phrasing) ? phrasing.GetString()! : "unknown",
            e.TryGetProperty("expectedClassification", out var classification) &&
                classification.ValueKind == JsonValueKind.String
                ? classification.GetString()
                : null))
            .ToList();
    }
}
