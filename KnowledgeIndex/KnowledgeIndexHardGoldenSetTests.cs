// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Runtime.InteropServices;
using System.Text.Json;
using Shouldly;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using Klacks.Api.KnowledgeIndex.Infrastructure.Persistence;
using Npgsql;
using NUnit.Framework;

namespace Klacks.IntegrationTest.KnowledgeIndex;

/// <summary>
/// Recall report for the "hard" golden set (knowledge-index-golden-hard.json): queries deliberately
/// aimed at clusters of semantically overlapping skills/recipes (e.g. the company-rule intake
/// lifecycle, single-vs-bulk group membership, the near-identical explain_page_* description
/// template) rather than the well-separated names the original 91-item golden set exercises.
/// Uses the real ONNX embedding + reranker pipeline directly (same pattern as
/// <see cref="KnowledgeIndexGoldenSetTests"/>), so it requires the ONNX models and does not run
/// on Windows ARM64 (Snapdragon X) — see <see cref="KnowledgeIndexHardGoldenSetDiHostTests"/> for
/// the DI-fallback path that does run there.
/// </summary>
[TestFixture]
[Explicit("Requires ONNX models (~200 MB download) and real DB on port 5434. Run manually only.")]
[Category("SlowModelLoad")]
[Category("RealDatabase")]
public class KnowledgeIndexHardGoldenSetTests
{
    private const string ConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

    private const string MinPassRateEnvironmentVariable = "KNOWLEDGEINDEX_HARD_MIN_PASS_RATE";

    private static string EmbeddingCacheDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            KnowledgeIndexConstants.ModelsCacheSubdirectory,
            KnowledgeIndexConstants.EmbeddingModelName);

    private static string RerankerCacheDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            KnowledgeIndexConstants.ModelsCacheSubdirectory,
            KnowledgeIndexConstants.RerankerModelName);

    private static readonly string GoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard.json");

    // A gate of 0.0 was a gate in name only - the assertion could never fail, so a total retrieval
    // collapse read as green. There is still NO recorded top-3 measurement for this set (it writes
    // no eval_runs row and no summary in docs/ or the nightly backups carries one), so this floor is
    // a deliberate provisional target, not "measured value - 5 pp": half the queries of a set built
    // from confusable clusters must land their expectation in the top 3. Replace it with
    // "measured - 5 pp" as soon as a run is on record, and override per run without a code change
    // via KNOWLEDGEINDEX_HARD_MIN_PASS_RATE (0.0-1.0).
    private const double MinPassRate = 0.5;

    [SetUp]
    public void SkipOnUnsupportedOnnxPlatform()
    {
        // Mirrors ServiceCollectionExtensions.IsOnnxRuntimeSupported: on Windows ARM64 the ONNX
        // native runtime is never loaded, knowledge_index stays empty, and every query reports
        // 0% recall regardless of actual retrieval quality. Skip instead of a false failure.
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            Assert.Ignore("ONNX Runtime is unsupported on Windows ARM64 — retrieval is structurally disabled on this host, so recall cannot be measured here. Use KnowledgeIndexHardGoldenSetDiHostTests instead.");
        }
    }

    [Test]
    public async Task HardGoldenSet_QueriesMeetTop3RecallBaseline()
    {
        var golden = LoadGoldenSet();

        var loader = new ModelLoader(new HttpClient());
        await using var embeddingProvider = new OnnxEmbeddingProvider(loader, EmbeddingCacheDir);
        await using var rerankerProvider = new OnnxRerankerProvider(loader, RerankerCacheDir);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        var repo = new KnowledgeIndexRepository(connection);

        var service = new KnowledgeRetrievalService(embeddingProvider, rerankerProvider, repo);

        var failures = new List<string>();
        var top1Hits = 0;
        var preRerankRecallHits = 0;
        foreach (var item in golden)
        {
            var queryVec = await embeddingProvider.EmbedQueryAsync(item.Query, CancellationToken.None);
            var preRerankCandidates = await repo.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);
            if (preRerankCandidates.Any(c => item.Accepts(c.SourceId)))
            {
                preRerankRecallHits++;
            }

            var result = await service.RetrieveAsync(
                item.Query, [], isAdmin: true, topK: 3, currentRoute: null, CancellationToken.None);

            var found = result.Candidates.Any(c =>
                item.Accepts(c.Entry.SourceId));

            if (result.Candidates.Count > 0 &&
                item.Accepts(result.Candidates[0].Entry.SourceId))
            {
                top1Hits++;
            }

            if (!found)
            {
                var top3 = string.Join(", ", result.Candidates.Select(c => c.Entry.SourceId));
                failures.Add($"Query '{item.Query}': expected '{item.ExpectedDisplay}' in top-3, got [{top3}]");
            }
        }

        var passRate = 1.0 - (double)failures.Count / golden.Count;
        var top1Rate = (double)top1Hits / golden.Count;
        var preRerankRecallRate = (double)preRerankRecallHits / golden.Count;

        TestContext.WriteLine(
            $"Pre-rerank recall@{KnowledgeIndexConstants.MaxRerankerCandidates} (retriever only): " +
            $"{preRerankRecallHits}/{golden.Count} = {preRerankRecallRate:P1}");
        TestContext.WriteLine($"Top-1 recall (full pipeline): {top1Hits}/{golden.Count} = {top1Rate:P1}");
        TestContext.WriteLine(
            $"Top-3 recall (full pipeline): {golden.Count - failures.Count}/{golden.Count} = {passRate:P1}");

        if (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                TestContext.WriteLine(failure);
            }
        }

        var gate = ReadMinPassRateGate();
        passRate.ShouldBeGreaterThanOrEqualTo(
            gate,
            $"top-3 recall on the hard golden set: {passRate:P1} ({failures.Count} failures of {golden.Count}).");
    }

    private static double ReadMinPassRateGate()
    {
        var raw = Environment.GetEnvironmentVariable(MinPassRateEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return MinPassRate;
        }

        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0.0 || parsed > 1.0)
        {
            throw new InvalidOperationException(
                $"{MinPassRateEnvironmentVariable} must be a number between 0.0 and 1.0, got '{raw}'.");
        }

        return parsed;
    }

    private static List<HardGoldenSetItem> LoadGoldenSet() => HardGoldenSetItem.Load(GoldenSetPath);
}
