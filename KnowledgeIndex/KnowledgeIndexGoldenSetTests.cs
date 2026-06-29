// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Shouldly;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Constants;
using Klacks.Api.Infrastructure.KnowledgeIndex.Application.Services;
using Klacks.Api.Infrastructure.KnowledgeIndex.Infrastructure.Onnx;
using Klacks.Api.Infrastructure.KnowledgeIndex.Infrastructure.Persistence;
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

    private static readonly string GoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden.json");

    private record GoldenItem(string Query, string ExpectedSourceId);

    // Multilingual cross-encoder ranking does not yet match every golden item.
    // The baseline below tolerates known recall gaps; tighten when the index is re-ingested
    // or the reranker is upgraded.
    private const double MinPassRate = 0.85;

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
        foreach (var item in golden)
        {
            var result = await service.RetrieveAsync(
                item.Query, [], isAdmin: true, topK: 3, CancellationToken.None);

            var found = result.Candidates.Any(c =>
                c.Entry.SourceId.Equals(item.ExpectedSourceId, StringComparison.OrdinalIgnoreCase));

            if (!found)
            {
                var top3 = string.Join(", ", result.Candidates.Select(c => c.Entry.SourceId));
                failures.Add($"Query '{item.Query}': expected '{item.ExpectedSourceId}' in top-3, got [{top3}]");
            }
        }

        var passRate = 1.0 - (double)failures.Count / golden.Count;

        if (failures.Count > 0)
        {
            TestContext.WriteLine(
                $"Top-3 recall: {golden.Count - failures.Count}/{golden.Count} = {passRate:P1}");
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
