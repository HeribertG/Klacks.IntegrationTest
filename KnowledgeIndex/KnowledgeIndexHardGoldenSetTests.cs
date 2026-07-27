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

    private record GoldenItem(string Query, string ExpectedSourceId);

    // No baseline gate on purpose: this set exists to MEASURE the confusable-cluster gap, not to
    // enforce a threshold yet. Once a baseline run has happened, replace this with a real number.
    private const double MinPassRate = 0.0;

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
            if (preRerankCandidates.Any(c => c.SourceId.Equals(item.ExpectedSourceId, StringComparison.OrdinalIgnoreCase)))
            {
                preRerankRecallHits++;
            }

            var result = await service.RetrieveAsync(
                item.Query, [], isAdmin: true, topK: 3, currentRoute: null, CancellationToken.None);

            var found = result.Candidates.Any(c =>
                c.Entry.SourceId.Equals(item.ExpectedSourceId, StringComparison.OrdinalIgnoreCase));

            if (result.Candidates.Count > 0 &&
                result.Candidates[0].Entry.SourceId.Equals(item.ExpectedSourceId, StringComparison.OrdinalIgnoreCase))
            {
                top1Hits++;
            }

            if (!found)
            {
                var top3 = string.Join(", ", result.Candidates.Select(c => c.Entry.SourceId));
                failures.Add($"Query '{item.Query}': expected '{item.ExpectedSourceId}' in top-3, got [{top3}]");
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

        passRate.ShouldBeGreaterThanOrEqualTo(
            MinPassRate,
            $"top-3 recall on the hard golden set: {passRate:P1} ({failures.Count} failures of {golden.Count}).");
    }

    private static List<GoldenItem> LoadGoldenSet()
    {
        var json = File.ReadAllText(GoldenSetPath);
        var raw = JsonSerializer.Deserialize<JsonElement[]>(json)!;
        return raw.Select(e => new GoldenItem(
            e.GetProperty("query").GetString()!,
            e.GetProperty("expectedSourceId").GetString()!)).ToList();
    }
}
