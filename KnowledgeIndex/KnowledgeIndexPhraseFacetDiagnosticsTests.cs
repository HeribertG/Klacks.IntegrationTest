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

    // Enough of the head of the list to judge whether the neighbourhood is plausible, without
    // drowning the report: the question is what wins, not the full ordering.
    private const int TopNeighboursShown = 6;

    private const string GermanLanguageTag = "de";

    // The labels the synchronizer writes; the rebuilt text has to reproduce them exactly, otherwise
    // the comparison measures a formatting change instead of the language filter.
    private const string KeywordsSectionLabel = "Keywords: ";
    private const string SynonymsSectionLabel = "Synonyms: ";

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

        // Every kind, not just skills: 8 of the 104 core cases expect a recipe, and restricting the
        // universe to KnowledgeEntryKind.Skill made those unreachable by construction — they showed up
        // as "target absent" and were counted as retrieval failures they never were.
        var skillKeys = hashes.Keys.ToList();
        var skillEntries = await repository.GetByKeysAsync(skillKeys, ct);
        TestContext.WriteLine(
            $"Index entries in the universe: {skillEntries.Count} " +
            $"({skillEntries.Count(e => e.Kind == KnowledgeEntryKind.Skill)} skills, " +
            $"{skillEntries.Count(e => e.Kind == KnowledgeEntryKind.Recipe)} recipes)");

        var phrases = (await phraseRepository.GetAllActiveAsync(ct))
            .Where(p => p.OwnerKind is SkillPhraseOwnerKinds.Skill or SkillPhraseOwnerKinds.Recipe)
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
    /// Separates the two ways a candidate can be lost after the search found it, which the end-to-end
    /// figure cannot distinguish. Either the target arrives at the reranker already badly placed — then
    /// the search is at fault and a deeper candidate pool would help — or it arrives near the top and
    /// the reranker pushes it down, in which case no candidate depth repairs anything and the reranker
    /// itself is the defect. Prints the vector rank, the reranker score and the reranker rank side by
    /// side for the cases the pipeline currently drops, with two passing cases as a control.
    ///
    /// Caveat: uses the semantic leg alone, while production fuses it with the lexical one. The
    /// vector rank here is therefore not the exact rank the reranker sees in production — but the
    /// reranker's own verdict on each candidate is, and that is what this measures.
    /// </summary>
    [Test]
    public async Task Reranker_Isolated_ForCasesLostAfterRetrieval()
    {
        var ct = CancellationToken.None;
        using var scope = _factory.Services.CreateScope();
        var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var reranker = scope.ServiceProvider.GetRequiredService<IRerankerProvider>();

        TestContext.WriteLine($"Active embedding space: {embedding.EmbeddingSpaceId}");
        TestContext.WriteLine($"Cutoff: {KnowledgeIndexConstants.DefaultScoreCutoff}, TopK: {KnowledgeIndexConstants.DefaultTopK}");

        var probes = new (string Target, string Query, string Note)[]
        {
            ("add_client_to_group_by_name", "füge Herrn Baumann der Gruppe Zürich hinzu", "dropped"),
            ("set_user_group_scope", "restrict this user to the eastern region only", "dropped"),
            ("get_system_info", "how much memory is this server using", "dropped"),
            ("close_period", "zapamiętaj, że okres rozliczeniowy lipca trzeba ostatecznie zamknąć", "control, arrives #1"),
            ("confirm_work", "bestätige die geleistete Arbeitszeit von Frau Vogt", "control, arrives #6"),
        };

        foreach (var probe in probes)
        {
            var queryVector = await embedding.EmbedQueryAsync(probe.Query, ct);
            var candidates = await repository.FindNearestAsync(
                queryVector, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, ct);

            var vectorRank = candidates.ToList().FindIndex(c => string.Equals(c.SourceId, probe.Target, StringComparison.Ordinal)) + 1;
            var scores = await reranker.ScoreAsync(probe.Query, candidates.Select(c => c.Text).ToList(), ct);

            var scored = candidates
                .Select((c, i) => (c.SourceId, Score: scores[i], VectorRank: i + 1))
                .OrderByDescending(x => x.Score)
                .ToList();

            var rerankRank = scored.FindIndex(x => string.Equals(x.SourceId, probe.Target, StringComparison.Ordinal)) + 1;
            var targetScore = rerankRank > 0 ? scored[rerankRank - 1].Score : double.NaN;

            TestContext.WriteLine($"--- {probe.Target} ({probe.Note}) | \"{probe.Query}\"");
            if (vectorRank == 0)
            {
                TestContext.WriteLine("    not among the semantic candidates at all");
                continue;
            }

            TestContext.WriteLine(
                $"    vector rank #{vectorRank} -> reranker rank #{rerankRank}, score {targetScore:F6} " +
                $"(cutoff {KnowledgeIndexConstants.DefaultScoreCutoff}, survives: {targetScore >= KnowledgeIndexConstants.DefaultScoreCutoff})");
            TestContext.WriteLine(
                "    reranker top 5: " +
                string.Join(", ", scored.Take(5).Select(x => $"{x.SourceId}({x.Score:F4}, was #{x.VectorRank})")));
        }
    }

    /// <summary>
    /// Answers the one question the model swap leaves open, without paying for the full run. The
    /// candidate stage improved from 160/173 to 168/173 with e5-base, but candidates only matter if
    /// they survive reranking and the score floor. Measuring all 173 cases through the cross-encoder
    /// costs ~2.5 hours; measuring only the cases the swap actually moved costs minutes and answers
    /// the same thing — do the newly found targets reach the model?
    ///
    /// Deliberately narrow: this cannot detect a regression among cases that were already passing.
    /// The full run stays necessary before anything ships, it just does not belong in a working day.
    /// </summary>
    [Test]
    public async Task ChangedCases_ThroughFullPipeline_AfterModelSwap()
    {
        var ct = CancellationToken.None;
        using var scope = _factory.Services.CreateScope();
        var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();

        TestContext.WriteLine($"Active embedding space: {embedding.EmbeddingSpaceId}");
        TestContext.WriteLine($"Cutoff: {KnowledgeIndexConstants.DefaultScoreCutoff}, TopK: {KnowledgeIndexConstants.DefaultTopK}");

        var watched = new HashSet<string>(StringComparer.Ordinal)
        {
            // Were retriever misses with e5-small, are found with e5-base — do they arrive?
            "add_client_to_group_by_name", "close_period", "read_email", "update_client",
            "create_shift", "find_split_shift_candidates", "set_user_group_scope", "confirm_work",
            "get_system_info",
            // Still missing after the swap, or newly missing.
            "start_wizard2", "search_shifts", "set_client_qualification", "add_ai_memory",
            "start_company_rule"
        };

        var cases = LoadGolden(GoldenSetPath).Concat(LoadGolden(ExtendedGoldenSetPath))
            .Where(i => watched.Contains(i.ExpectedSourceId))
            .ToList();

        TestContext.WriteLine($"=== {cases.Count} watched cases through the real pipeline ===");
        var hits = 0;

        foreach (var item in cases)
        {
            var result = await retrieval.RetrieveAsync(
                item.Query, [], isAdmin: true, KnowledgeIndexConstants.DefaultTopK, currentRoute: null, ct);

            var names = result.Candidates.Select(c => c.Entry.SourceId).ToList();
            var position = names.FindIndex(n => string.Equals(n, item.ExpectedSourceId, StringComparison.Ordinal));
            if (position >= 0)
            {
                hits++;
                TestContext.WriteLine($"  HIT  #{position + 1,-2} | {item.ExpectedSourceId} | \"{item.Query}\"");
            }
            else
            {
                TestContext.WriteLine($"  MISS     | {item.ExpectedSourceId} | \"{item.Query}\"");
                TestContext.WriteLine($"           delivered: {string.Join(", ", names.Take(6))}");
            }
        }

        TestContext.WriteLine($"=== {hits}/{cases.Count} watched cases reach the model ===");
        cases.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Measures what the language grouping was actually built for. Today every index text carries the
    /// phrases of all five languages at once — `search_shifts` holds 6 German phrases next to roughly
    /// 25 English, French, Italian and Polish ones — and mean pooling averages them into one vector.
    /// A German query is therefore matched against a point that is four fifths determined by languages
    /// it will never use.
    ///
    /// This rebuilds each entry's text with only the query language plus the language-neutral `mul`
    /// phrases, and re-measures the 79 German cases against it. Unlike the phrase-facet variants, this
    /// does not add competition — it removes what is provably irrelevant for the query at hand, which
    /// is the opposite operation and the reason it is worth measuring separately.
    ///
    /// German only, deliberately: the golden sets label everything else merely as "other", and
    /// inventing a language detector for a diagnostic would measure the detector, not the idea.
    /// </summary>
    [Test]
    public async Task LanguageFilteredIndexText_VersusMixedLanguage_ForGermanCases()
    {
        var ct = CancellationToken.None;
        using var scope = _factory.Services.CreateScope();
        var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var phraseRepository = scope.ServiceProvider.GetRequiredService<ISkillPhraseRepository>();

        TestContext.WriteLine($"Active embedding space: {embedding.EmbeddingSpaceId}");

        var hashes = await repository.GetAllHashesAsync(ct);
        var entries = await repository.GetByKeysAsync(hashes.Keys.ToList(), ct);
        var phrases = (await phraseRepository.GetAllActiveAsync(ct))
            .Where(p => !string.IsNullOrWhiteSpace(p.Phrase))
            .ToList();

        // Language IS NULL is the storage form of "mul" — identical in every language, so it stays.
        var keptByOwner = phrases
            .Where(p => p.Language is null || string.Equals(p.Language, GermanLanguageTag, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.OwnerName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var rebuilt = new List<string>(entries.Count);
        var sourceIds = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            var cut = entry.Text.IndexOf(KeywordsSectionLabel, StringComparison.Ordinal);
            var basis = cut >= 0 ? entry.Text[..cut] : entry.Text;
            keptByOwner.TryGetValue(entry.SourceId, out var kept);

            var keywords = kept?.Where(p => p.Kind == SkillPhraseKinds.Keyword).Select(p => p.Phrase) ?? [];
            var synonyms = kept?.Where(p => p.Kind == SkillPhraseKinds.Synonym).Select(p => p.Phrase) ?? [];
            rebuilt.Add($"{basis}{KeywordsSectionLabel}{string.Join(", ", keywords)}\n{SynonymsSectionLabel}{string.Join(", ", synonyms)}");
            sourceIds.Add(entry.SourceId);
        }

        var before = entries.Sum(e => e.Text.Length);
        var after = rebuilt.Sum(t => t.Length);
        TestContext.WriteLine(
            $"Index text rebuilt for {rebuilt.Count} entries: {before} -> {after} characters " +
            $"({(double)(before - after) / before:P1} removed by dropping foreign-language phrases)");

        var filteredVectors = new List<(string SourceId, float[] Vector)>(rebuilt.Count);
        for (var start = 0; start < rebuilt.Count; start += EmbedChunkSize)
        {
            var chunk = rebuilt.Skip(start).Take(EmbedChunkSize).ToList();
            var embedded = await embedding.EmbedBatchAsync(chunk, ct);
            for (var i = 0; i < embedded.Length; i++)
            {
                filteredVectors.Add((sourceIds[start + i], Normalize(embedded[i])));
            }

            TestContext.WriteLine($"  embedded {filteredVectors.Count}/{rebuilt.Count} rebuilt texts");
        }

        var storedVectors = entries
            .Where(e => e.Embedding.Length > 0)
            .Select(e => (e.SourceId, Vector: Normalize(e.Embedding)))
            .ToList();

        foreach (var (label, path) in new[] { ("CORE", GoldenSetPath), ("EXTENDED", ExtendedGoldenSetPath) })
        {
            var german = LoadGolden(path)
                .Where(i => string.Equals(i.Lang, GermanLanguageTag, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var mixedHits = 0;
            var filteredHits = 0;
            var moves = new List<string>();

            foreach (var item in german)
            {
                var queryVector = Normalize(await embedding.EmbedQueryAsync(item.Query, ct));
                var mixedRank = RankOf(item.ExpectedSourceId, storedVectors.Select(v => (v.SourceId, Dot(queryVector, v.Vector))));
                var filteredRank = RankOf(item.ExpectedSourceId, filteredVectors.Select(v => (v.SourceId, Dot(queryVector, v.Vector))));

                var mixedIn = mixedRank > 0 && mixedRank <= RecallCutoff;
                var filteredIn = filteredRank > 0 && filteredRank <= RecallCutoff;
                if (mixedIn) mixedHits++;
                if (filteredIn) filteredHits++;

                if (mixedIn != filteredIn)
                {
                    var tag = filteredIn ? "RESCUED" : "LOST";
                    moves.Add($"  {tag} | {item.ExpectedSourceId} | \"{item.Query}\" | mixed=#{mixedRank} -> filtered=#{filteredRank}");
                }
            }

            TestContext.WriteLine($"=== {label}: {german.Count} German cases ===");
            TestContext.WriteLine($"  recall@{RecallCutoff} mixed-language index (today): {mixedHits}/{german.Count} = {(double)mixedHits / german.Count:P1}");
            TestContext.WriteLine($"  recall@{RecallCutoff} German + mul only:           {filteredHits}/{german.Count} = {(double)filteredHits / german.Count:P1}");
            TestContext.WriteLine($"  net change: {filteredHits - mixedHits:+#;-#;0} cases");
            foreach (var line in moves) TestContext.WriteLine(line);
        }
    }

    /// <summary>
    /// Shows what the vector search actually returns for the cases it currently fails, which decides
    /// whether the ceiling is the model or the catalogue. If the top of the list is plausible — near
    /// misses from the same subject area — the model understands the query and the target is merely
    /// outranked by siblings. If the same few skills crowd the top of every failing query, or the
    /// entries are unrelated, the problem is not one of degree and a bigger model will not fix it.
    /// Uses the stored vectors and embeds only the queries, so it costs minutes, not hours.
    /// </summary>
    [Test]
    public async Task NearestNeighbours_ForFailingCases_ViaStoredVectors()
    {
        var ct = CancellationToken.None;
        using var scope = _factory.Services.CreateScope();
        var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();

        TestContext.WriteLine($"Active embedding space: {embedding.EmbeddingSpaceId}");

        var hashes = await repository.GetAllHashesAsync(ct);

        // Every kind, not just skills: 8 of the 104 core cases expect a recipe, and restricting the
        // universe to KnowledgeEntryKind.Skill made those unreachable by construction — they showed up
        // as "target absent" and were counted as retrieval failures they never were.
        var skillKeys = hashes.Keys.ToList();
        var entries = await repository.GetByKeysAsync(skillKeys, ct);
        var vectors = entries
            .Where(e => e.Embedding.Length > 0)
            .Select(e => (e.SourceId, Vector: Normalize(e.Embedding)))
            .ToList();

        foreach (var (label, golden) in new[]
                 {
                     ("CORE", LoadGolden(GoldenSetPath)),
                     ("EXTENDED", LoadGolden(ExtendedGoldenSetPath)),
                 })
        {
            TestContext.WriteLine($"=== {label}: neighbourhood of the failing cases ===");
            var failing = 0;

            foreach (var item in golden)
            {
                var queryVector = Normalize(await embedding.EmbedQueryAsync(item.Query, ct));
                var ranked = vectors
                    .Select(v => (v.SourceId, Score: Dot(queryVector, v.Vector)))
                    .OrderByDescending(x => x.Score)
                    .ToList();

                var rank = ranked.FindIndex(x => string.Equals(x.SourceId, item.ExpectedSourceId, StringComparison.Ordinal)) + 1;
                if (rank > 0 && rank <= RecallCutoff)
                {
                    continue;
                }

                failing++;
                var top = string.Join(", ", ranked.Take(TopNeighboursShown).Select((x, i) => $"{i + 1}.{x.SourceId}({x.Score:F3})"));
                TestContext.WriteLine($"  MISS {item.ExpectedSourceId} (rank {(rank == 0 ? "absent" : "#" + rank)}) | \"{item.Query}\"");
                TestContext.WriteLine($"       top: {top}");
            }

            TestContext.WriteLine($"  {failing} failing cases in {label}");
        }
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
        var fusedHits = 0;
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

            // Taking the maximum lets one incidentally matching short phrase override an otherwise
            // solid whole-text score, which is why it loses more than it gains. Rank fusion cannot do
            // that: a second list can only add evidence for a skill, never overwrite the first list's
            // verdict. This is the shape production already uses for dense + lexical, so measuring it
            // here decides whether phrases are worth adding as a third source.
            var rankBySingle = RankMap(singleVectors.Select(s => (s.SourceId, Dot(queryVector, s.Vector))));
            var rankByFacet = RankMap(facetsBySkill.Select(kv => (kv.Key, kv.Value.Max(v => Dot(queryVector, v)))));
            var fusedRank = RankOf(
                item.ExpectedSourceId,
                rankBySingle.Select(kv => (
                    kv.Key,
                    Score: ReciprocalRank(kv.Value) +
                           (rankByFacet.TryGetValue(kv.Key, out var fr) ? ReciprocalRank(fr) : 0f))));

            var singleIn = singleRank > 0 && singleRank <= RecallCutoff;
            var facetIn = facetRank > 0 && facetRank <= RecallCutoff;
            var combinedIn = combinedRank > 0 && combinedRank <= RecallCutoff;
            var fusedIn = fusedRank > 0 && fusedRank <= RecallCutoff;

            if (singleIn) singleHits++;
            if (facetIn) facetHits++;
            if (combinedIn) combinedHits++;
            if (fusedIn) fusedHits++;

            // Reported for the fusion variant, since that is the one under consideration.
            if (!singleIn && fusedIn)
            {
                rescued.Add($"  RESCUED | {item.ExpectedSourceId} | \"{item.Query}\" | single=#{singleRank} -> fused=#{fusedRank}");
            }
            else if (singleIn && !fusedIn)
            {
                lost.Add($"  LOST | {item.ExpectedSourceId} | \"{item.Query}\" | single=#{singleRank} -> fused=#{fusedRank}");
            }
        }

        TestContext.WriteLine($"=== {label} ({golden.Count} cases) ===");
        TestContext.WriteLine($"  recall@{RecallCutoff} single vector (today):   {singleHits}/{golden.Count} = {(double)singleHits / golden.Count:P1}");
        TestContext.WriteLine($"  recall@{RecallCutoff} phrase facets only:     {facetHits}/{golden.Count} = {(double)facetHits / golden.Count:P1}");
        TestContext.WriteLine($"  recall@{RecallCutoff} both (max per skill):   {combinedHits}/{golden.Count} = {(double)combinedHits / golden.Count:P1}");
        TestContext.WriteLine($"  recall@{RecallCutoff} RRF fusion (k={KnowledgeIndexConstants.ReciprocalRankFusionK}):    {fusedHits}/{golden.Count} = {(double)fusedHits / golden.Count:P1}");
        TestContext.WriteLine($"  net change (max):    {combinedHits - singleHits:+#;-#;0} cases");
        TestContext.WriteLine($"  net change (fusion): {fusedHits - singleHits:+#;-#;0} cases");

        foreach (var line in rescued) TestContext.WriteLine(line);
        foreach (var line in lost) TestContext.WriteLine(line);
    }

    /// <summary>
    /// One-based rank per skill for a scored universe, so two rankings can be fused by position
    /// instead of by score — raw scores from a whole-text vector and from a short phrase are not on
    /// a comparable scale, which is precisely why taking their maximum misbehaves.
    /// </summary>
    private static Dictionary<string, int> RankMap(IEnumerable<(string SourceId, float Score)> scored)
    {
        var ordered = scored.OrderByDescending(s => s.Score).ToList();
        var map = new Dictionary<string, int>(ordered.Count, StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            map.TryAdd(ordered[i].SourceId, i + 1);
        }

        return map;
    }

    /// <summary>
    /// Reciprocal rank contribution of one list, using the same constant as production fusion.
    /// </summary>
    private static float ReciprocalRank(int rank) =>
        1f / (KnowledgeIndexConstants.ReciprocalRankFusionK + rank);

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
