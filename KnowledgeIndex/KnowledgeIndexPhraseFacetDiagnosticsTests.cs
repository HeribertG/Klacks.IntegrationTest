// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Decides the single-vector dilution hypothesis before anyone pays for the architecture change it
/// implies. Today every skill is one row in knowledge_index, so one vector has to carry the prose
/// description, the parameter list and up to 30 trigger phrases at once. The 2026-07-29 measurement
/// found 13 golden-set cases whose target is not even among the 25 KNN candidates, and neither the
/// obvious explanations survived: 11 of those skills are richly equipped (update_client has 12
/// keywords and 31 synonyms), and index-text length does not separate hits from misses (969 vs 1015
/// characters on average). The retrieval literature names the remaining candidate — a single vector
/// averages a long, mixed-register document into a point that is close to none of its parts, and a
/// short instruction ("il cognome di questa persona è scritto male") lives in a different region of
/// the embedding space than a tool description.
///
/// This test simulates the fix without building it: it re-ranks every golden-set case a second time
/// against per-phrase vectors, scoring each skill by its best matching facet (MaxP) instead of by its
/// averaged whole. If recall@25 rises, dilution is real and a multi-vector index is worth the
/// migration; if it does not, the ceiling is the embedding model and the architecture work would have
/// been wasted.
///
/// Reporting only, never gates. Reads the shared dev DB on port 5434 and neutralizes the index
/// synchronizer for the same reason as <see cref="KnowledgeIndexHardGoldenSetDiHostTests"/>: an
/// unmatched embedding space would make it re-embed and overwrite every stored row. The bypass
/// factory is deliberately duplicated rather than shared with that fixture — it is the measuring
/// instrument for the recall figures in the handoff and is not worth touching for a diagnostic.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Explicit("Boots the real app host, reads the real DB on port 5434 and embeds ~10k phrases. Reporting only, run manually.")]
[Category("RealDatabase")]
public class KnowledgeIndexPhraseFacetDiagnosticsTests
{
    // The KNN candidate budget is the stage these cases fail at, so the diagnostic measures exactly
    // that width rather than a number of its own.
    private const int RecallCutoff = KnowledgeIndexConstants.MaxRerankerCandidates;

    private const int EmbedChunkSize = 64;

    private const int ProgressEvery = 2000;

    private static readonly string GoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard.json");

    private static readonly string ExtendedGoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard-ext.json");

    private sealed class NoOpUiControlRepository : IUiControlRepository
    {
        public Task<List<UiControl>> GetByPageKeyAsync(string pageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<UiControl>());

        public Task<List<string>> GetDistinctPageKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<string>());

        public Task<List<UiControl>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<UiControl>());

        public Task AddRangeAsync(IEnumerable<UiControl> controls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateAsync(UiControl control, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpsertAsync(UiControl control, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> GetCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class NoOpRegionSetupService : IRegionSetupService
    {
        public Task ApplyAsync() => Task.CompletedTask;
    }

    private sealed class NoOpKnowledgeIndexSynchronizer : IKnowledgeIndexSynchronizer
    {
        public Task SyncAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DiagnosticsBypassFactory : SignalRTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUiControlRepository>();
                services.AddScoped<IUiControlRepository, NoOpUiControlRepository>();
                services.RemoveAll<IRegionSetupService>();
                services.AddScoped<IRegionSetupService, NoOpRegionSetupService>();
                services.RemoveAll<IKnowledgeIndexSynchronizer>();
                services.AddScoped<IKnowledgeIndexSynchronizer, NoOpKnowledgeIndexSynchronizer>();
            });
        }
    }

    private sealed record Facet(string Skill, string Text, string Kind);

    private sealed class GoldenItem
    {
        public string Query { get; set; } = string.Empty;
        public string ExpectedSourceId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
    }

    private DiagnosticsBypassFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new DiagnosticsBypassFactory();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory?.Dispose();

    [Test]
    public async Task PhraseFacets_VersusSingleVector_RecallDiagnostics()
    {
        var ct = CancellationToken.None;
        using var scope = _factory.Services.CreateScope();
        var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var phraseRepository = scope.ServiceProvider.GetRequiredService<ISkillPhraseRepository>();

        // Same guard as the recall fixture: a different space here would compare vectors across
        // incompatible geometries and every figure below would be noise, not a weak result.
        TestContext.WriteLine($"Active embedding space: {embedding.EmbeddingSpaceId}");

        var hashes = await repository.GetAllHashesAsync(ct);
        var skillKeys = hashes.Keys.Where(k => k.Kind == KnowledgeEntryKind.Skill).ToList();
        var skillEntries = await repository.GetByKeysAsync(skillKeys, ct);
        TestContext.WriteLine($"Skills in the index: {skillEntries.Count}");

        var phrases = (await phraseRepository.GetAllActiveAsync(ct))
            .Where(p => p.OwnerKind == SkillPhraseOwnerKinds.Skill)
            .Where(p => !string.IsNullOrWhiteSpace(p.Phrase))
            .DistinctBy(p => (p.OwnerName, p.Kind, p.Phrase))
            .ToList();

        var indexedSkills = skillEntries.Select(e => e.SourceId).ToHashSet(StringComparer.Ordinal);
        var facets = phrases
            .Where(p => indexedSkills.Contains(p.OwnerName))
            .Select(p => new Facet(p.OwnerName, p.Phrase, p.Kind))
            .ToList();

        TestContext.WriteLine(
            $"Phrase facets to embed: {facets.Count} " +
            $"({facets.Count(f => f.Kind == SkillPhraseKinds.Keyword)} keywords, " +
            $"{facets.Count(f => f.Kind == SkillPhraseKinds.Synonym)} synonyms) " +
            $"covering {facets.Select(f => f.Skill).Distinct().Count()} of {skillEntries.Count} skills");

        var facetVectors = await EmbedFacetsAsync(embedding, facets, ct);

        // The whole-text vectors come from the DB rather than being recomputed: variant A must be the
        // production state exactly, not a re-derivation of it.
        var singleVectors = skillEntries
            .Where(e => e.Embedding.Length > 0)
            .Select(e => (e.SourceId, Vector: Normalize(e.Embedding)))
            .ToList();
        TestContext.WriteLine($"Stored skill vectors usable: {singleVectors.Count}");

        var facetsBySkill = facets
            .Select((f, i) => (f.Skill, Vector: facetVectors[i]))
            .GroupBy(x => x.Skill, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Vector).ToList(), StringComparer.Ordinal);

        var core = LoadGolden(GoldenSetPath);
        var extended = LoadGolden(ExtendedGoldenSetPath);

        await MeasureAsync("CORE", core, embedding, singleVectors, facetsBySkill, ct);
        await MeasureAsync("EXTENDED", extended, embedding, singleVectors, facetsBySkill, ct);

        core.Count.ShouldBeGreaterThan(0);
        extended.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Ranks every case twice over the same skill universe and reports both recalls side by side.
    /// </summary>
    /// <param name="label">Golden set name used in the report.</param>
    /// <param name="golden">Cases to measure.</param>
    /// <param name="singleVectors">One stored vector per skill — today's representation.</param>
    /// <param name="facetsBySkill">Per-phrase vectors per skill, scored by their best match.</param>
    private static async Task MeasureAsync(
        string label,
        List<GoldenItem> golden,
        IEmbeddingProvider embedding,
        List<(string SourceId, float[] Vector)> singleVectors,
        Dictionary<string, List<float[]>> facetsBySkill,
        CancellationToken ct)
    {
        var singleHits = 0;
        var facetHits = 0;
        var combinedHits = 0;
        var rescued = new List<string>();
        var lost = new List<string>();

        foreach (var item in golden)
        {
            var queryVector = Normalize(await embedding.EmbedQueryAsync(item.Query, ct));

            var singleRank = RankOf(
                item.ExpectedSourceId,
                singleVectors.Select(s => (s.SourceId, Score: Dot(queryVector, s.Vector))));

            var facetRank = RankOf(
                item.ExpectedSourceId,
                facetsBySkill.Select(kv => (kv.Key, Score: kv.Value.Max(v => Dot(queryVector, v)))));

            // A production index would keep both signals, so the honest third column is the better of
            // the two per skill — not the facet score alone.
            var combinedRank = RankOf(
                item.ExpectedSourceId,
                singleVectors.Select(s => (
                    s.SourceId,
                    Score: Math.Max(
                        Dot(queryVector, s.Vector),
                        facetsBySkill.TryGetValue(s.SourceId, out var fv) ? fv.Max(v => Dot(queryVector, v)) : float.MinValue))));

            var singleIn = singleRank > 0 && singleRank <= RecallCutoff;
            var facetIn = facetRank > 0 && facetRank <= RecallCutoff;
            var combinedIn = combinedRank > 0 && combinedRank <= RecallCutoff;

            if (singleIn) singleHits++;
            if (facetIn) facetHits++;
            if (combinedIn) combinedHits++;

            if (!singleIn && combinedIn)
            {
                rescued.Add($"  RESCUED | {item.ExpectedSourceId} | \"{item.Query}\" | single=#{singleRank} -> combined=#{combinedRank}");
            }
            else if (singleIn && !combinedIn)
            {
                lost.Add($"  LOST | {item.ExpectedSourceId} | \"{item.Query}\" | single=#{singleRank} -> combined=#{combinedRank}");
            }
        }

        TestContext.WriteLine($"=== {label} ({golden.Count} cases) ===");
        TestContext.WriteLine($"  recall@{RecallCutoff} single vector (today):   {singleHits}/{golden.Count} = {(double)singleHits / golden.Count:P1}");
        TestContext.WriteLine($"  recall@{RecallCutoff} phrase facets only:     {facetHits}/{golden.Count} = {(double)facetHits / golden.Count:P1}");
        TestContext.WriteLine($"  recall@{RecallCutoff} both (max per skill):   {combinedHits}/{golden.Count} = {(double)combinedHits / golden.Count:P1}");
        TestContext.WriteLine($"  net change: {combinedHits - singleHits:+#;-#;0} cases");

        foreach (var line in rescued) TestContext.WriteLine(line);
        foreach (var line in lost) TestContext.WriteLine(line);
    }

    /// <summary>
    /// One-based rank of the expected skill in a scored universe; 0 when it is absent.
    /// </summary>
    private static int RankOf(string expected, IEnumerable<(string SourceId, float Score)> scored)
    {
        var ordered = scored.OrderByDescending(s => s.Score).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i].SourceId, expected, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static async Task<List<float[]>> EmbedFacetsAsync(
        IEmbeddingProvider embedding,
        List<Facet> facets,
        CancellationToken ct)
    {
        var vectors = new List<float[]>(facets.Count);

        for (var start = 0; start < facets.Count; start += EmbedChunkSize)
        {
            var chunk = facets.Skip(start).Take(EmbedChunkSize).Select(f => f.Text).ToList();
            var embedded = await embedding.EmbedBatchAsync(chunk, ct);
            vectors.AddRange(embedded.Select(Normalize));

            if (start % ProgressEvery < EmbedChunkSize)
            {
                TestContext.WriteLine($"  embedded {vectors.Count}/{facets.Count} facets");
            }
        }

        return vectors;
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector) sum += (double)v * v;
        var norm = Math.Sqrt(sum);
        if (norm <= 0) return vector;

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = (float)(vector[i] / norm);
        return result;
    }

    private static float Dot(float[] a, float[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        float sum = 0;
        for (var i = 0; i < length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static List<GoldenItem> LoadGolden(string path)
    {
        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<List<GoldenItem>>(json, options) ?? [];
    }
}
