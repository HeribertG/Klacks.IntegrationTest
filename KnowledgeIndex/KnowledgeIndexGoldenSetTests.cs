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

[TestFixture]
[Explicit("Requires ONNX models (~200 MB download) and real DB on port 5434. Run manually only.")]
[Category("SlowModelLoad")]
[Category("RealDatabase")]
public class KnowledgeIndexGoldenSetTests
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

    // W0.5: single source of truth — the Api goldset under Klacks.Api/Application/Skills/Goldsets.
    private static readonly string GoldenSetPath = LocateApiGoldset("knowledge-index-v1.json");

    private record GoldenItem(string Query, string ExpectedSourceId);

    private static string LocateApiGoldset(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Klacks.Api", "Application", "Skills", "Goldsets", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate Klacks.Api/Application/Skills/Goldsets/{fileName} by walking up from the test base directory.");
    }

    // Multilingual cross-encoder ranking does not yet match every golden item.
    // The baseline below tolerates known recall gaps; tighten when the index is re-ingested
    // or the reranker is upgraded.
    private const double MinPassRate = 0.85;

    [SetUp]
    public void SkipOnUnsupportedOnnxPlatform()
    {
        // Mirrors ServiceCollectionExtensions.IsOnnxRuntimeSupported: on Windows ARM64 the ONNX
        // native runtime is never loaded, knowledge_index stays empty, and every query reports
        // 0% recall regardless of actual retrieval quality. Skip instead of a false failure.
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            Assert.Ignore("ONNX Runtime is unsupported on Windows ARM64 — retrieval is structurally disabled on this host, so top-3 recall cannot be measured here.");
        }
    }

    [Test]
    public async Task GoldenSet_QueriesMeetTop3RecallBaseline()
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
            $"top-3 recall regressed below baseline ({MinPassRate:P0}). Current: {passRate:P1} ({failures.Count} failures of {golden.Count}).");
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
