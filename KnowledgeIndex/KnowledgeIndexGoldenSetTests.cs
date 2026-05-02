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

    [Test]
    public async Task GoldenSet_AllQueriesReturnExpectedSkillInTop3()
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

        failures.ShouldBeEmpty("all golden set queries should return the expected skill within top-3");
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
