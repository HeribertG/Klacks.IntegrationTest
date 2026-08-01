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
/// The stored rows carry a single embedding space, and since the synchronizer is neutralized nothing
/// realigns the vectors at runtime — so the "Active embedding space" line this test writes MUST match
/// it. Any other value means the KNN pass compares vectors across incompatible spaces and every recall
/// figure below is meaningless rather than merely weak. Since 2026-07-30 the expected id is
/// "onnx:multilingual-e5-base@768" (was "-small@384", verified 2026-07-28 by recomputing the stored
/// hashes in SQL).
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
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

    private static readonly string LanguageCoverageSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard-langs.json");

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
    /// Ablation: what would the toolset contain if the cross-encoder were removed and the KNN order
    /// used as-is? The full report measures the reranker against itself and can therefore not answer
    /// whether the reranker helps at all - the 2026-07-30 isolation found it moving targets both up
    /// (#17 to #6) and down (#10 to #16), with no net figure either way.
    /// Runs in a couple of minutes because it never calls the reranker, which is where essentially all
    /// of the full report's runtime goes. Compare its recall@DefaultTopK against the full report's.
    /// </summary>
    [Test]
    public async Task HardGoldenSet_RerankerAblation_VectorOrderOnly()
    {
        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");
        TestContext.WriteLine("=== RERANKER ABLATION (pure KNN order, no cross-encoder) ===");

        var core = LoadGoldenSet();
        var extended = LoadExtendedGoldenSet();

        var coreHits = await MeasureVectorOrderAsync("CORE", core, embeddingProvider, repository);
        var extHits = await MeasureVectorOrderAsync("EXTENDED", extended, embeddingProvider, repository);

        var total = core.Count + extended.Count;
        var hits = coreHits + extHits;
        TestContext.WriteLine(
            $"=== COMBINED ({total} cases) === vector-order recall@{KnowledgeIndexConstants.DefaultTopK}: " +
            $"{hits}/{total} = {(double)hits / total:P1}");

        core.Count.ShouldBeGreaterThan(0);
        extended.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Coverage check across languages the golden set never contained. Klacks ships 25 languages, but
    /// mmarco-mMiniLMv2 declares only 14 training languages - cs, da, el, fi, he, ko, ms, nb, pl, ro,
    /// sv, th and zh-TW were never among them, and the reranker already costs recall in languages it
    /// does know. This set covers the eight untrained ones written in Latin script, where the
    /// translations can be verified; the other scripts need a native speaker before they are worth
    /// measuring.
    /// "none" scores the raw KNN order and takes seconds; "current" and a model name run the
    /// cross-encoder and take hours, which is why this belongs in the nightly run.
    /// </summary>
    // Explicit TestName per case: the default name carries the parameter in parentheses, which the
    // dotnet test filter cannot parse, so single cases could not be selected for a nightly run.
    [TestCase("none", TestName = "LanguageCoverage_VectorOnly")]
    [TestCase("current", TestName = "LanguageCoverage_CurrentReranker")]
    [TestCase("bge-reranker-v2-m3", TestName = "LanguageCoverage_BgeV2M3")]
    public async Task HardGoldenSet_LanguageCoverage(string variant)
    {
        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();

        IRerankerProvider? reranker = null;
        OnnxRerankerProvider? owned = null;
        if (variant == "current")
        {
            reranker = scope.ServiceProvider.GetRequiredService<IRerankerProvider>();
        }
        else if (variant != "none")
        {
            var dir = Path.Combine(
                AppContext.BaseDirectory, KnowledgeIndexConstants.ModelsCacheSubdirectory, variant);
            if (!File.Exists(Path.Combine(dir, KnowledgeIndexConstants.RerankerModelFileName)))
            {
                Assert.Ignore($"No model file in {dir} - download it first.");
            }

            owned = new OnnxRerankerProvider(
                scope.ServiceProvider.GetRequiredService<ModelLoader>(), dir,
                modelUrl: string.Empty, modelSha256: string.Empty,
                tokenizerUrl: string.Empty, tokenizerSha256: string.Empty);
            reranker = owned;
        }

        try
        {
            TestContext.WriteLine($"=== LANGUAGE COVERAGE [{variant}] ===");
            TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");

            var cases = HardGoldenSetItem.Load(LanguageCoverageSetPath);
            var hits = new Dictionary<string, int>();
            var total = new Dictionary<string, int>();
            var started = DateTime.UtcNow;

            foreach (var item in cases)
            {
                total[item.LangCode] = total.GetValueOrDefault(item.LangCode) + 1;

                var queryVec = await embeddingProvider.EmbedQueryAsync(item.Query, CancellationToken.None);
                var candidates = await repository.FindNearestAsync(
                    queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);

                var wrappedEndpoints = candidates
                    .Where(c => c.Kind == KnowledgeEntryKind.Skill && c.ExposedEndpointKey is not null)
                    .Select(c => c.ExposedEndpointKey!)
                    .ToHashSet();

                var filtered = candidates
                    .Where(c => c.Kind == KnowledgeEntryKind.Skill || !wrappedEndpoints.Contains(c.SourceId))
                    .ToList();

                bool found;
                if (reranker is null)
                {
                    var rank = filtered.FindIndex(c => item.Accepts(c.SourceId));
                    found = rank >= 0 && rank < KnowledgeIndexConstants.DefaultTopK;
                }
                else
                {
                    var scores = await reranker.ScoreAsync(
                        item.Query, filtered.Select(f => f.Text).ToList(), CancellationToken.None);
                    found = filtered
                        .Zip(scores, (entry, score) => (Entry: entry, Score: score))
                        .OrderByDescending(p => p.Score)
                        .Take(KnowledgeIndexConstants.DefaultTopK)
                        .Any(p => item.Accepts(p.Entry.SourceId));
                }

                if (found)
                {
                    hits[item.LangCode] = hits.GetValueOrDefault(item.LangCode) + 1;
                }
            }

            foreach (var lang in total.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var h = hits.GetValueOrDefault(lang);
                TestContext.WriteLine(
                    $"  [{variant}] {lang}: {h,3}/{total[lang],-3} = {(double)h / total[lang]:P1}");
            }

            var tot = hits.Values.Sum();
            TestContext.WriteLine(
                $"  [{variant}] GESAMT: {tot}/{cases.Count} = {(double)tot / cases.Count:P1} " +
                $"({(DateTime.UtcNow - started).TotalMinutes:F1} min)");

            cases.Count.ShouldBeGreaterThan(0);
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Benchmarks an alternative cross-encoder against the current one on EXTENDED, where the reranker
    /// demonstrably loses cases. The model files must already sit in Cache/Models/&lt;name&gt;; empty url and
    /// hash tell the loader to use them as they are instead of downloading the configured reranker over
    /// them. Split into separate test cases per model so the first result is available without waiting
    /// for the second - the cross-encoder pass is the entire runtime.
    /// </summary>
    [TestCase("bge-reranker-base")]
    [TestCase("bge-reranker-v2-m3")]
    public async Task HardGoldenSet_AlternativeReranker_Extended(string modelName)
    {
        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var loader = scope.ServiceProvider.GetRequiredService<ModelLoader>();

        var modelDir = Path.Combine(
            AppContext.BaseDirectory, KnowledgeIndexConstants.ModelsCacheSubdirectory, modelName);
        if (!File.Exists(Path.Combine(modelDir, KnowledgeIndexConstants.RerankerModelFileName)))
        {
            Assert.Ignore($"No model file in {modelDir} - download it first.");
        }

        await using var alternative = new OnnxRerankerProvider(
            loader, modelDir, modelUrl: string.Empty, modelSha256: string.Empty,
            tokenizerUrl: string.Empty, tokenizerSha256: string.Empty);

        TestContext.WriteLine($"=== ALTERNATIVE RERANKER: {modelName} (EXTENDED) ===");
        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");

        var extended = LoadExtendedGoldenSet();
        var hits = new Dictionary<string, int>();
        var total = new Dictionary<string, int>();
        var started = DateTime.UtcNow;

        foreach (var item in extended)
        {
            total[item.LangCode] = total.GetValueOrDefault(item.LangCode) + 1;

            var queryVec = await embeddingProvider.EmbedQueryAsync(item.Query, CancellationToken.None);
            var candidates = await repository.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);

            var wrappedEndpoints = candidates
                .Where(c => c.Kind == KnowledgeEntryKind.Skill && c.ExposedEndpointKey is not null)
                .Select(c => c.ExposedEndpointKey!)
                .ToHashSet();

            var filtered = candidates
                .Where(c => c.Kind == KnowledgeEntryKind.Skill || !wrappedEndpoints.Contains(c.SourceId))
                .ToList();

            var scores = await alternative.ScoreAsync(
                item.Query, filtered.Select(f => f.Text).ToList(), CancellationToken.None);

            // No score cutoff: it was calibrated against the current model's distribution and would
            // measure that calibration rather than this model's ordering.
            var ranked = filtered
                .Zip(scores, (entry, score) => (Entry: entry, Score: score))
                .OrderByDescending(p => p.Score)
                .Take(KnowledgeIndexConstants.DefaultTopK)
                .ToList();

            if (ranked.Any(p => item.Accepts(p.Entry.SourceId)))
            {
                hits[item.LangCode] = hits.GetValueOrDefault(item.LangCode) + 1;
            }
        }

        var elapsed = (DateTime.UtcNow - started).TotalMinutes;
        foreach (var lang in total.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var h = hits.GetValueOrDefault(lang);
            TestContext.WriteLine(
                $"  {modelName} {lang,-4}: {h,3}/{total[lang],-3} = {(double)h / total[lang]:P1}");
        }

        var tot = hits.Values.Sum();
        TestContext.WriteLine(
            $"  {modelName} GESAMT: {tot}/{extended.Count} = {(double)tot / extended.Count:P1} " +
            $"(Laufzeit {elapsed:F1} min, ohne Cutoff)");

        extended.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Scores the EXTENDED set both ways - reranked and in raw KNN order - from the same candidate set,
    /// and splits the result by actual language. The full report only distinguishes de from other, which
    /// is too coarse: the ablation showed the reranker losing 6 cases in EXTENDED/other while being
    /// harmless on German, and whether that is one language or all of them decides between tuning a few
    /// skills and replacing the model.
    /// Restricted to EXTENDED because that is where the loss sits, and because the cross-encoder is the
    /// entire cost: 69 cases run in minutes where all 173 take hours.
    /// </summary>
    [Test]
    public async Task HardGoldenSet_RerankerByLanguage_Extended()
    {
        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var reranker = scope.ServiceProvider.GetRequiredService<IRerankerProvider>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");
        TestContext.WriteLine("=== RERANKER BY LANGUAGE (EXTENDED, both orderings, same candidates) ===");

        var extended = LoadExtendedGoldenSet();
        var withReranker = new Dictionary<string, int>();
        var vectorOnly = new Dictionary<string, int>();
        var total = new Dictionary<string, int>();
        var hitsAtTopK = new int[TopKCurve.Length];

        foreach (var item in extended)
        {
            total[item.LangCode] = total.GetValueOrDefault(item.LangCode) + 1;

            var queryVec = await embeddingProvider.EmbedQueryAsync(item.Query, CancellationToken.None);
            var candidates = await repository.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);

            var wrappedEndpoints = candidates
                .Where(c => c.Kind == KnowledgeEntryKind.Skill && c.ExposedEndpointKey is not null)
                .Select(c => c.ExposedEndpointKey!)
                .ToHashSet();

            var filtered = candidates
                .Where(c => c.Kind == KnowledgeEntryKind.Skill || !wrappedEndpoints.Contains(c.SourceId))
                .ToList();

            var vectorRank = filtered.FindIndex(c => item.Accepts(c.SourceId));
            if (vectorRank >= 0 && vectorRank < KnowledgeIndexConstants.DefaultTopK)
            {
                vectorOnly[item.LangCode] = vectorOnly.GetValueOrDefault(item.LangCode) + 1;
            }

            // Mirrors production: cutoff on the raw score, then order, then take.
            var scores = await reranker.ScoreAsync(
                item.Query, filtered.Select(f => f.Text).ToList(), CancellationToken.None);

            var reranked = filtered
                .Zip(scores, (entry, score) => (Entry: entry, Score: score))
                .Where(p => p.Score >= KnowledgeIndexConstants.DefaultScoreCutoff)
                .OrderByDescending(p => p.Score)
                .ToList();

            // Rank once, derive every depth from it. Measuring DefaultTopK alone would need a separate
            // full run per candidate value, and each costs ~19 minutes of cross-encoder time.
            var rerankRank = reranked.FindIndex(p => item.Accepts(p.Entry.SourceId));
            for (var d = 0; d < TopKCurve.Length; d++)
            {
                if (rerankRank >= 0 && rerankRank < TopKCurve[d])
                {
                    hitsAtTopK[d]++;
                }
            }

            if (rerankRank >= 0 && rerankRank < KnowledgeIndexConstants.DefaultTopK)
            {
                withReranker[item.LangCode] = withReranker.GetValueOrDefault(item.LangCode) + 1;
            }
        }

        TestContext.WriteLine($"  --- volle Kette, recall je TopK (DefaultTopK = {KnowledgeIndexConstants.DefaultTopK}) ---");
        for (var d = 0; d < TopKCurve.Length; d++)
        {
            var marker = TopKCurve[d] == KnowledgeIndexConstants.DefaultTopK ? "  <- produktiv" : string.Empty;
            TestContext.WriteLine(
                $"  recall@{TopKCurve[d],-4} {hitsAtTopK[d],3}/{extended.Count} = " +
                $"{(double)hitsAtTopK[d] / extended.Count:P1}{marker}");
        }

        TestContext.WriteLine($"  {"lang",-6} {"n",3}  {"mit Gewichter",14}  {"ohne (Vektor)",14}  delta");
        foreach (var lang in total.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var n = total[lang];
            var w = withReranker.GetValueOrDefault(lang);
            var v = vectorOnly.GetValueOrDefault(lang);
            TestContext.WriteLine(
                $"  {lang,-6} {n,3}  {w,3}/{n,-3} = {(double)w / n,6:P1}  {v,3}/{n,-3} = {(double)v / n,6:P1}  {w - v,+3}");
        }

        var totW = withReranker.Values.Sum();
        var totV = vectorOnly.Values.Sum();
        TestContext.WriteLine(
            $"  GESAMT {extended.Count,3}  {totW,3} = {(double)totW / extended.Count:P1}  " +
            $"{totV,3} = {(double)totV / extended.Count:P1}  netto {totW - totV:+0;-0;0}");

        extended.Count.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Recall of the raw KNN order at every candidate width. Mirrors the production path except for the
    /// cross-encoder: same candidate count, same wrapped-endpoint filter, so the only difference to the
    /// full report is the ordering. Reports the whole curve because the cost of doing so is one extra
    /// comparison per case, and it locates the best DefaultTopK directly instead of by guessing.
    /// </summary>
    private static async Task<int> MeasureVectorOrderAsync(
        string label,
        List<HardGoldenSetItem> golden,
        IEmbeddingProvider embeddingProvider,
        IKnowledgeIndexRepository repository)
    {
        var hitsAtWidth = new int[AblationWidths.Length];
        var candidateHits = 0;
        var rankSum = 0;
        var ranked = 0;
        // Counted separately rather than read out of hitsAtWidth: DefaultTopK is the value this whole
        // exercise is about changing, and it need not stay one of the sampled widths.
        var hitsAtDefaultTopK = 0;
        var perLanguageHits = new Dictionary<string, int>();
        var perLanguageTotal = new Dictionary<string, int>();

        foreach (var item in golden)
        {
            var queryVec = await embeddingProvider.EmbedQueryAsync(item.Query, CancellationToken.None);
            var candidates = await repository.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);

            var wrappedEndpoints = candidates
                .Where(c => c.Kind == KnowledgeEntryKind.Skill && c.ExposedEndpointKey is not null)
                .Select(c => c.ExposedEndpointKey!)
                .ToHashSet();

            var filtered = candidates
                .Where(c => c.Kind == KnowledgeEntryKind.Skill || !wrappedEndpoints.Contains(c.SourceId))
                .ToList();

            var rank = filtered.FindIndex(c => item.Accepts(c.SourceId));

            // Counted before the miss shortcut, so the per-language denominators stay comparable to the
            // full report's - there the language split covers every case, hit or not.
            perLanguageTotal[item.LangCode] = perLanguageTotal.GetValueOrDefault(item.LangCode) + 1;

            if (rank < 0)
            {
                continue;
            }

            candidateHits++;
            rankSum += rank + 1;
            ranked++;

            if (rank < KnowledgeIndexConstants.DefaultTopK)
            {
                hitsAtDefaultTopK++;
                perLanguageHits[item.LangCode] = perLanguageHits.GetValueOrDefault(item.LangCode) + 1;
            }

            for (var w = 0; w < AblationWidths.Length; w++)
            {
                if (rank < AblationWidths[w])
                {
                    hitsAtWidth[w]++;
                }
            }
        }

        TestContext.WriteLine($"--- {label} ({golden.Count} cases), vector order only ---");
        TestContext.WriteLine(
            $"  in candidate set at all: {candidateHits}/{golden.Count} = {(double)candidateHits / golden.Count:P1}");
        if (ranked > 0)
        {
            TestContext.WriteLine($"  mean rank of the target when present: {(double)rankSum / ranked:F1}");
        }

        for (var w = 0; w < AblationWidths.Length; w++)
        {
            var marker = AblationWidths[w] == KnowledgeIndexConstants.DefaultTopK ? "  <- DefaultTopK" : string.Empty;
            TestContext.WriteLine(
                $"  vector-order recall@{AblationWidths[w],2}: {hitsAtWidth[w]}/{golden.Count} = " +
                $"{(double)hitsAtWidth[w] / golden.Count:P1}{marker}");
        }

        // Same split as the full report writes, so the two can be put side by side: the reranker helps
        // on CORE and hurts on EXTENDED, and language is the standing suspect for that sign flip.
        TestContext.WriteLine(
            $"  --- vector-order recall@{KnowledgeIndexConstants.DefaultTopK} per query language ---");
        foreach (var lang in perLanguageTotal.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var langHits = perLanguageHits.GetValueOrDefault(lang);
            TestContext.WriteLine(
                $"    {lang}: {langHits}/{perLanguageTotal[lang]} = " +
                $"{(double)langHits / perLanguageTotal[lang]:P1}");
        }

        return hitsAtDefaultTopK;
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
            perLanguageTotal[item.LangCode] = perLanguageTotal.GetValueOrDefault(item.LangCode) + 1;
            if (toolsetHit)
            {
                perLanguageHits[item.LangCode] = perLanguageHits.GetValueOrDefault(item.LangCode) + 1;
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
    // alongside the alternatives. The steps between 0.001 and 0.0 are not decoration: every target
    // this set currently loses to the cutoff scores between 0.0001 and 0.0009, so the whole decision
    // lives in that interval. Sweeping only 0.001 -> 0.0 forces a choice between keeping the losses
    // and handing an off-topic question the full toolset, with nothing measured in between.
    private static readonly double[] CutoffSweep = [0.05, 0.02, 0.01, 0.005, 0.001, 0.0005, 0.0002, 0.0001, 0.0];

    // Must contain DefaultTopK (the figure comparable to the full report) and end at
    // MaxRerankerCandidates, the widest toolset the candidate pass can ever supply.
    private static readonly int[] AblationWidths = [1, 3, 5, 8, 12, 15, 20, 25];

    // Candidate values for DefaultTopK, measured through the full chain in a single pass. 21 is the
    // hard ceiling: MaxToolsForProviderCeiling (30) minus the 9 alwaysOn skills.
    private static readonly int[] TopKCurve = [12, 16, 20, 21, 25];

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
