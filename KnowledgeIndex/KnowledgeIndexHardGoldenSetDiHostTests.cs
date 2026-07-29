// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Recall report for the "hard" golden set (knowledge-index-golden-hard.json), resolved through the
/// real application DI container — same rationale and boot-time workarounds as
/// <see cref="KnowledgeIndexGoldenSetDiHostTests"/> (see that file for the full explanation of the
/// UiControl/RegionSetup "Kind=Utc into timestamp without time zone" startup bug). This is the only
/// way to measure retrieval quality on a host where ONNX cannot run (Windows ARM64 / Snapdragon X).
/// Reporting only — always green as long as the golden set loads; no MinPassRate gate, since the
/// fallback pipeline is knowingly weaker than the ONNX cross-encoder and is not the CI/production
/// quality bar. Additionally neutralizes <see cref="IKnowledgeIndexSynchronizer"/>: if the host
/// resolves a different embedding provider than the one that wrote the stored rows, it would see
/// every row as "changed" (ComputeTextHash folds EmbeddingSpaceId into the stored hash) and
/// re-embed + overwrite all of them at startup — a write this module is not permitted to make on
/// the shared dev DB.
/// Verified 2026-07-28 by recomputing the stored hashes in SQL: all knowledge_index rows carry the
/// "onnx:multilingual-e5-small@384" space. Since the synchronizer is neutralized, nothing realigns
/// the vectors at runtime — so the "Active embedding space" line this test writes MUST report that
/// same id. Any other value means the KNN pass compares vectors across incompatible spaces and every
/// recall figure below is meaningless rather than merely weak.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Settings;
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
[Explicit("Boots the real app host and reads the real DB on port 5434. Reporting only, run manually.")]
[Category("RealDatabase")]
public class KnowledgeIndexHardGoldenSetDiHostTests
{
    private static readonly string GoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard.json");

    private static readonly string ExtendedGoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard-ext.json");

    private static readonly string OffTopicPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-offtopic.json");

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

    // Prevents KnowledgeIndexStartupService (an IHostedService that runs unconditionally at boot)
    // from calling the real synchronizer, which would upsert/delete rows in the shared dev DB's
    // knowledge_index table under a different embedding space than the one already stored there.
    private sealed class NoOpKnowledgeIndexSynchronizer : IKnowledgeIndexSynchronizer
    {
        public Task SyncAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HardGoldenSetBypassFactory : SignalRTestWebApplicationFactory
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

    private HardGoldenSetBypassFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new HardGoldenSetBypassFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [Test]
    public async Task HardGoldenSet_RecallReport_ViaRealHostDi()
    {
        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var reranker = scope.ServiceProvider.GetRequiredService<IRerankerProvider>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");

        var core = LoadGoldenSet();
        var extended = LoadExtendedGoldenSet();

        // Reported per set, never merged into one figure: every number in the handoff refers to the
        // 104-case core set, and silently folding the extension in would break that comparison.
        var coreHits = await MeasureSetAsync("CORE", core, embeddingProvider, repository, retrieval, reranker);
        var extHits = await MeasureSetAsync("EXTENDED", extended, embeddingProvider, repository, retrieval, reranker);

        TestContext.WriteLine(
            $"=== COMBINED ({core.Count + extended.Count} cases) === toolset recall@" +
            $"{KnowledgeIndexConstants.DefaultTopK}: {coreHits + extHits}/{core.Count + extended.Count} = " +
            $"{(double)(coreHits + extHits) / (core.Count + extended.Count):P1}");

        await ReportOffTopicWidthAsync(retrieval, embeddingProvider, repository, reranker);

        core.Count.ShouldBeGreaterThan(0);
        extended.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Measures one golden set and writes its full report. Returns the toolset recall hit count so the
    /// caller can aggregate across sets without re-running the cross-encoder.
    /// </summary>
    private static async Task<int> MeasureSetAsync(
        string label,
        List<HardGoldenSetItem> golden,
        IEmbeddingProvider embeddingProvider,
        IKnowledgeIndexRepository repository,
        IKnowledgeRetrievalService retrieval,
        IRerankerProvider reranker)
    {
        TestContext.WriteLine($"=== {label} ({golden.Count} cases) ===");

        var failures = new List<string>();
        var top1Hits = 0;
        var preRerankRecallHits = 0;
        var toolsetRecallHits = 0;
        var top1Misses = new List<string>();

        // Why a toolset miss happened decides which fix is even applicable, and the two causes can
        // coexist: DefaultScoreCutoff is applied BEFORE Take(topK), so a target can be below the
        // cutoff and out of rank range at once. Lowering the constant and raising DefaultTopK are
        // then each useless alone. Classified per case so the choice rests on counts, not intuition.
        var missRetriever = 0;
        var missCutoffOnly = 0;
        var missRankOnly = 0;
        var missBoth = 0;
        var missWrapFilter = 0;
        var missDetail = new List<string>();
        var cutoffFreeRecallHits = 0;
        var targetScores = new List<double>();
        var hitRanks = new List<int>();
        var sweepHits = new int[CutoffSweep.Length];
        var perLanguageHits = new Dictionary<string, int>();
        var perLanguageTotal = new Dictionary<string, int>();
        var perLanguagePreHits = new Dictionary<string, int>();
        var perLanguagePreTotal = new Dictionary<string, int>();

        foreach (var item in golden)
        {
            var queryVec = await embeddingProvider.EmbedQueryAsync(item.Query, CancellationToken.None);
            var preRerankCandidates = await repository.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);
            var preRerankHit = preRerankCandidates.Any(c => item.Accepts(c.SourceId));
            if (preRerankHit)
            {
                preRerankRecallHits++;
            }

            // Pre-rerank recall is the sensitive figure for anything that changes the embedded text:
            // it measures the vector search alone. Toolset recall is capped by it and, after the floor
            // fix, sits close enough to that cap that a real improvement could hide inside the ceiling.
            perLanguagePreTotal[item.Lang] = perLanguagePreTotal.GetValueOrDefault(item.Lang) + 1;
            if (preRerankHit)
            {
                perLanguagePreHits[item.Lang] = perLanguagePreHits.GetValueOrDefault(item.Lang) + 1;
            }

            // The production toolset is DefaultTopK entries wide, not one: the model receives the whole
            // list and picks. Recall at that width is therefore the practically relevant number, while
            // top-1 only says how good the ordering is. Measured separately so an ordering improvement
            // is never mistaken for a capability the assistant did not previously have.
            // A single retrieval serves all three figures: topK only applies Take(topK) after the
            // cross-encoder pass, so the DefaultTopK result is an order-identical superset of the top-3
            // one. Requesting both would run the reranker twice per case for no extra information.
            var toolsetResult = await retrieval.RetrieveAsync(
                item.Query, [], isAdmin: true, KnowledgeIndexConstants.DefaultTopK, currentRoute: null, CancellationToken.None);
            var toolsetHit = toolsetResult.Candidates.Any(c => item.Accepts(c.Entry.SourceId));
            if (toolsetHit)
            {
                toolsetRecallHits++;
            }

            // Split by language so a single-language index can be compared against the mixed one on the
            // questions it is supposed to serve. Without this split, building the index from one
            // language would help German questions and hurt the rest, and the total would hide both.
            perLanguageTotal[item.Lang] = perLanguageTotal.GetValueOrDefault(item.Lang) + 1;
            if (toolsetHit)
            {
                perLanguageHits[item.Lang] = perLanguageHits.GetValueOrDefault(item.Lang) + 1;
            }

            // Ranked for EVERY case, not just the failures: the "recall if the cutoff were removed"
            // figure must be measured, not extrapolated from the failures alone. Removing the cutoff
            // lets more candidates survive into Take(topK), so in principle a target that fits today
            // could be pushed out. Counting rank < DefaultTopK across all cases settles that directly.
            var (cause, detail, targetRank, targetScore, ranked) =
                await ClassifyTargetAsync(item, preRerankCandidates, reranker);

            // All candidate scores are already computed, so every candidate cutoff can be evaluated
            // from the same pass. Measuring the alternatives together is what makes gain and price
            // comparable; picking one value and reporting only its result would hide the trade-off.
            for (var c = 0; c < CutoffSweep.Length; c++)
            {
                if (TargetSurvives(ranked, item, CutoffSweep[c]))
                {
                    sweepHits[c]++;
                }
            }

            if (targetRank >= 0 && targetRank < KnowledgeIndexConstants.DefaultTopK)
            {
                cutoffFreeRecallHits++;
            }

            if (targetRank >= 0)
            {
                targetScores.Add(targetScore);
                if (toolsetHit)
                {
                    hitRanks.Add(targetRank + 1);
                }
            }

            if (!toolsetHit)
            {
                switch (cause)
                {
                    case ToolsetMissCause.RetrieverMiss: missRetriever++; break;
                    case ToolsetMissCause.WrapFilter: missWrapFilter++; break;
                    case ToolsetMissCause.CutoffOnly: missCutoffOnly++; break;
                    case ToolsetMissCause.RankOnly: missRankOnly++; break;
                    case ToolsetMissCause.Both: missBoth++; break;
                }

                missDetail.Add(detail);
            }

            var top3Candidates = toolsetResult.Candidates.Take(3).ToList();

            var isTop1 = top3Candidates.Count > 0 &&
                item.Accepts(top3Candidates[0].Entry.SourceId);
            if (isTop1)
            {
                top1Hits++;
            }

            var foundTop3 = top3Candidates.Any(c =>
                item.Accepts(c.Entry.SourceId));
            if (!foundTop3)
            {
                var top3 = string.Join(", ", top3Candidates.Select(c => c.Entry.SourceId));
                failures.Add($"Query '{item.Query}': expected '{item.ExpectedDisplay}' in top-3, got [{top3}]");
            }

            if (!isTop1)
            {
                var top3Scored = string.Join(", ", top3Candidates.Select(c => $"{c.Entry.SourceId}={c.Score:F4}"));
                top1Misses.Add($"TOP1MISS | expected={item.ExpectedDisplay} | query=\"{item.Query}\" | top3=[{top3Scored}]");
            }
        }

        var top3Hits = golden.Count - failures.Count;
        TestContext.WriteLine(
            $"Pre-rerank recall@{KnowledgeIndexConstants.MaxRerankerCandidates} (retriever only): " +
            $"{preRerankRecallHits}/{golden.Count} = {(double)preRerankRecallHits / golden.Count:P1}");
        TestContext.WriteLine($"Top-1 recall (full pipeline): {top1Hits}/{golden.Count} = {(double)top1Hits / golden.Count:P1}");
        TestContext.WriteLine(
            $"Toolset recall@{KnowledgeIndexConstants.DefaultTopK} (what the model actually receives): " +
            $"{toolsetRecallHits}/{golden.Count} = {(double)toolsetRecallHits / golden.Count:P1}");
        TestContext.WriteLine($"Top-3 recall (full pipeline): {top3Hits}/{golden.Count} = {(double)top3Hits / golden.Count:P1}");

        TestContext.WriteLine(
            $"Toolset recall@{KnowledgeIndexConstants.DefaultTopK} IF the cutoff were removed " +
            $"(measured, not extrapolated): {cutoffFreeRecallHits}/{golden.Count} = " +
            $"{(double)cutoffFreeRecallHits / golden.Count:P1}");

        if (targetScores.Count > 0)
        {
            var sortedScores = targetScores.OrderBy(s => s).ToList();
            TestContext.WriteLine(
                $"Target raw-score distribution (n={sortedScores.Count}): " +
                $"min={sortedScores[0]:F4} p10={sortedScores[sortedScores.Count / 10]:F4} " +
                $"median={sortedScores[sortedScores.Count / 2]:F4} max={sortedScores[^1]:F4} | " +
                $"below cutoff: {sortedScores.Count(s => s < KnowledgeIndexConstants.DefaultScoreCutoff)}");
        }

        if (hitRanks.Count > 0)
        {
            TestContext.WriteLine(
                $"Rank of the target among current hits: max={hitRanks.Max()} " +
                $"(if this stays well under {KnowledgeIndexConstants.DefaultTopK}, removing the cutoff " +
                $"cannot push a current hit out)");
        }

        TestContext.WriteLine($"--- Toolset recall@{KnowledgeIndexConstants.DefaultTopK} per query language ---");
        foreach (var lang in perLanguageTotal.Keys.OrderBy(k => k))
        {
            var hits = perLanguageHits.GetValueOrDefault(lang);
            TestContext.WriteLine(
                $"  {lang}: {hits}/{perLanguageTotal[lang]} = {(double)hits / perLanguageTotal[lang]:P1}");
        }

        TestContext.WriteLine(
            $"--- Pre-rerank recall@{KnowledgeIndexConstants.MaxRerankerCandidates} per query language " +
            $"(vector search alone) ---");
        foreach (var lang in perLanguagePreTotal.Keys.OrderBy(k => k))
        {
            var hits = perLanguagePreHits.GetValueOrDefault(lang);
            TestContext.WriteLine(
                $"  {lang}: {hits}/{perLanguagePreTotal[lang]} = {(double)hits / perLanguagePreTotal[lang]:P1}");
        }

        TestContext.WriteLine($"--- Toolset recall@{KnowledgeIndexConstants.DefaultTopK} per score cutoff ---");
        for (var c = 0; c < CutoffSweep.Length; c++)
        {
            var marker = CutoffSweep[c] == KnowledgeIndexConstants.DefaultScoreCutoff ? "  <- current" : string.Empty;
            TestContext.WriteLine(
                $"  cutoff {CutoffSweep[c]:F4}: {sweepHits[c]}/{golden.Count} = " +
                $"{(double)sweepHits[c] / golden.Count:P1}{marker}");
        }

        var toolsetMisses = golden.Count - toolsetRecallHits;
        TestContext.WriteLine(
            $"--- Why the {toolsetMisses} toolset misses happened (decides which fix applies) ---");
        TestContext.WriteLine($"  retriever never found it (unfixable by reranking): {missRetriever}");
        TestContext.WriteLine($"  removed by the wrapped-endpoint filter: {missWrapFilter}");
        TestContext.WriteLine($"  below cutoff only (fix: DefaultScoreCutoff): {missCutoffOnly}");
        TestContext.WriteLine($"  out of rank only (fix: DefaultTopK): {missRankOnly}");
        TestContext.WriteLine($"  both (neither fix helps alone): {missBoth}");
        foreach (var detail in missDetail)
        {
            TestContext.WriteLine(detail);
        }

        foreach (var failure in failures)
        {
            TestContext.WriteLine(failure);
        }

        TestContext.WriteLine($"--- Full top-1 miss detail ({top1Misses.Count} of {golden.Count}) ---");
        foreach (var miss in top1Misses)
        {
            TestContext.WriteLine(miss);
        }

        return toolsetRecallHits;
    }

    /// <summary>
    /// Reports how many entries reach the model for questions the system has no tool for. This is the
    /// price side of relaxing the score cutoff: the cutoff is what currently keeps an off-topic
    /// question from being answered with a full toolset, and DefaultTopK alone does not do that.
    /// </summary>
    private static async Task ReportOffTopicWidthAsync(
        IKnowledgeRetrievalService retrieval,
        IEmbeddingProvider embeddingProvider,
        IKnowledgeIndexRepository repository,
        IRerankerProvider reranker)
    {
        var queries = JsonSerializer.Deserialize<string[]>(File.ReadAllText(OffTopicPath))!;
        var widths = new List<int>();
        var sweepWidths = new int[CutoffSweep.Length];

        foreach (var query in queries)
        {
            var result = await retrieval.RetrieveAsync(
                query, [], isAdmin: true, KnowledgeIndexConstants.DefaultTopK, currentRoute: null, CancellationToken.None);
            widths.Add(result.Candidates.Count);

            var queryVec = await embeddingProvider.EmbedQueryAsync(query, CancellationToken.None);
            var candidates = await repository.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);
            var scores = await reranker.ScoreAsync(
                query, candidates.Select(c => c.Text).ToList(), CancellationToken.None);

            for (var c = 0; c < CutoffSweep.Length; c++)
            {
                sweepWidths[c] += Math.Min(
                    scores.Count(s => s >= CutoffSweep[c]), KnowledgeIndexConstants.DefaultTopK);
            }
        }

        TestContext.WriteLine(
            $"=== OFF-TOPIC ({queries.Length} questions with no valid tool) === " +
            $"entries handed to the model: min={widths.Min()} max={widths.Max()} " +
            $"avg={widths.Average():F1} (cap is {KnowledgeIndexConstants.DefaultTopK}); " +
            $"questions answered with zero tools: {widths.Count(w => w == 0)}");

        // The price of every cutoff in the sweep: entries a question with no valid tool would carry
        // into the prompt. Recall gain is meaningless without this column next to it.
        TestContext.WriteLine("--- Off-topic entries per score cutoff (avg per question) ---");
        for (var c = 0; c < CutoffSweep.Length; c++)
        {
            var marker = CutoffSweep[c] == KnowledgeIndexConstants.DefaultScoreCutoff ? "  <- current" : string.Empty;
            TestContext.WriteLine(
                $"  cutoff {CutoffSweep[c]:F4}: {(double)sweepWidths[c] / queries.Length:F1} entries{marker}");
        }
    }

    // Candidate score cutoffs, current production value first so every run reproduces the baseline
    // alongside the alternatives.
    private static readonly double[] CutoffSweep = [0.05, 0.02, 0.01, 0.005, 0.001, 0.0];

    /// <summary>
    /// Whether the target would reach the toolset at the given cutoff. Mirrors production order:
    /// filter by raw score, then take the first DefaultTopK.
    /// </summary>
    private static bool TargetSurvives(
        IReadOnlyList<(KnowledgeEntry Entry, double Score)> ranked,
        HardGoldenSetItem item,
        double cutoff) =>
        ranked
            .Where(x => x.Score >= cutoff)
            .Take(KnowledgeIndexConstants.DefaultTopK)
            .Any(x => item.Accepts(x.Entry.SourceId));

    private enum ToolsetMissCause
    {
        RetrieverMiss,
        WrapFilter,
        CutoffOnly,
        RankOnly,
        Both
    }

    /// <summary>
    /// Determines why a target that the KNN pass retrieved did not survive into the DefaultTopK
    /// toolset. Mirrors the production ranking of <c>KnowledgeRetrievalService.RetrieveAsync</c>
    /// (wrapped-endpoint filter, then cross-encoder scores, then cutoff, then Take) so the reported
    /// rank matches what production computes; route boosting is skipped because the measurement runs
    /// with currentRoute = null. Runs only for failing cases, keeping the extra cross-encoder work
    /// proportional to the miss count rather than to the whole golden set.
    /// </summary>
    private static async Task<(ToolsetMissCause Cause, string Detail, int TargetRank, double TargetScore,
        IReadOnlyList<(KnowledgeEntry Entry, double Score)> Ranked)>
        ClassifyTargetAsync(
        HardGoldenSetItem item,
        IReadOnlyList<KnowledgeEntry> preRerankCandidates,
        IRerankerProvider reranker)
    {
        if (!preRerankCandidates.Any(c => item.Accepts(c.SourceId)))
        {
            return (ToolsetMissCause.RetrieverMiss,
                $"MISS/retriever | expected={item.ExpectedDisplay} | query=\"{item.Query}\" | " +
                $"not among the {KnowledgeIndexConstants.MaxRerankerCandidates} KNN candidates",
                -1, double.NaN, []);
        }

        var wrappedEndpoints = preRerankCandidates
            .Where(c => c.Kind == KnowledgeEntryKind.Skill && c.ExposedEndpointKey is not null)
            .Select(c => c.ExposedEndpointKey!)
            .ToHashSet();

        var filtered = preRerankCandidates
            .Where(c => c.Kind == KnowledgeEntryKind.Skill || !wrappedEndpoints.Contains(c.SourceId))
            .ToList();

        if (!filtered.Any(c => item.Accepts(c.SourceId)))
        {
            return (ToolsetMissCause.WrapFilter,
                $"MISS/wrap-filter | expected={item.ExpectedDisplay} | query=\"{item.Query}\" | " +
                $"removed as an endpoint already wrapped by a skill",
                -1, double.NaN, []);
        }

        var scores = await reranker.ScoreAsync(
            item.Query, filtered.Select(f => f.Text).ToList(), CancellationToken.None);

        var ranked = filtered
            .Zip(scores, (entry, score) => (Entry: entry, Score: score))
            .OrderByDescending(x => x.Score)
            .ToList();

        var rawRank = ranked.FindIndex(x => item.Accepts(x.Entry.SourceId));
        var targetScore = ranked[rawRank].Score;
        var belowCutoff = targetScore < KnowledgeIndexConstants.DefaultScoreCutoff;
        var outOfRank = rawRank >= KnowledgeIndexConstants.DefaultTopK;

        var cause = (belowCutoff, outOfRank) switch
        {
            (true, false) => ToolsetMissCause.CutoffOnly,
            (false, true) => ToolsetMissCause.RankOnly,
            (true, true) => ToolsetMissCause.Both,
            _ => ToolsetMissCause.RankOnly
        };

        return (cause,
            $"MISS/{cause} | expected={item.ExpectedDisplay} | query=\"{item.Query}\" | " +
            $"score={targetScore:F4} (cutoff={KnowledgeIndexConstants.DefaultScoreCutoff}) | " +
            $"rank={rawRank + 1} of {ranked.Count} (topK={KnowledgeIndexConstants.DefaultTopK})",
            rawRank, targetScore, ranked);
    }

    private static List<HardGoldenSetItem> LoadGoldenSet() => HardGoldenSetItem.Load(GoldenSetPath);

    private static List<HardGoldenSetItem> LoadExtendedGoldenSet() =>
        HardGoldenSetItem.Load(ExtendedGoldenSetPath);
}
