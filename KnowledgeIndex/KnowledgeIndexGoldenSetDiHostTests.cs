// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Golden-set recall report that resolves the retrieval pipeline through the real application DI
/// container instead of hardcoding the ONNX providers. On hosts where ONNX cannot run (Windows
/// ARM64 / Snapdragon X), <see cref="KnowledgeIndexGoldenSetTests"/> skips entirely, so this is the
/// only way to measure retrieval quality on such a host: it exercises whatever embedding/reranker
/// pair AddKnowledgeIndexServices actually wires up there (the OpenAI embedding fallback plus an
/// embedding-similarity reranker). Reporting only — always green as long as the golden set loads;
/// no MinPassRate gate, since the fallback pipeline is knowingly weaker than the ONNX cross-encoder
/// and is not the CI/production quality bar.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
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
public class KnowledgeIndexGoldenSetDiHostTests
{
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

    // Neutralizes AssistantExtensions.SeedUiControlsAsync (called unconditionally from Program.cs
    // startup) so the host can boot at all: UiControl audit columns are pre-existing on HEAD as
    // "timestamp without time zone" but OnBeforeSaving() stamps Kind=Utc DateTimes, so every
    // WebApplicationFactory<Program> host currently fails to start (confirmed by reproducing the
    // same DbUpdateException with the unmodified RecipeSemanticMatchBreadthTests). Not this
    // module's bug and out of scope to fix in production code; this DI override only affects the
    // in-process test host, never touches Program.cs or the seeder itself.
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

    // Same rationale as NoOpUiControlRepository above: a pending region setup profile on this DB
    // hits the identical Kind=Utc-into-"timestamp without time zone" defect inside
    // RegionSetupService.ApplyAsync, called unconditionally from Program.cs startup. Neutralized
    // for the test host only.
    private sealed class NoOpRegionSetupService : IRegionSetupService
    {
        public Task ApplyAsync() => Task.CompletedTask;
    }

    private sealed class UiControlSeedBypassFactory : SignalRTestWebApplicationFactory
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
            });
        }
    }

    private UiControlSeedBypassFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new UiControlSeedBypassFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [Test]
    public async Task GoldenSet_RecallReport_ViaRealHostDi()
    {
        var golden = LoadGoldenSet();

        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");

        var failures = new List<string>();
        var top1Hits = 0;
        var preRerankRecallHits = 0;
        foreach (var item in golden)
        {
            var queryVec = await embeddingProvider.EmbedQueryAsync(item.Query, CancellationToken.None);
            var preRerankCandidates = await repository.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);
            if (preRerankCandidates.Any(c => c.SourceId.Equals(item.ExpectedSourceId, StringComparison.OrdinalIgnoreCase)))
            {
                preRerankRecallHits++;
            }

            var result = await retrieval.RetrieveAsync(
                item.Query, [], isAdmin: true, topK: 3, currentRoute: null, CancellationToken.None);

            if (result.Candidates.Count > 0 &&
                result.Candidates[0].Entry.SourceId.Equals(item.ExpectedSourceId, StringComparison.OrdinalIgnoreCase))
            {
                top1Hits++;
            }

            var foundTop3 = result.Candidates.Any(c =>
                c.Entry.SourceId.Equals(item.ExpectedSourceId, StringComparison.OrdinalIgnoreCase));
            if (!foundTop3)
            {
                var top3 = string.Join(", ", result.Candidates.Select(c => c.Entry.SourceId));
                failures.Add($"Query '{item.Query}': expected '{item.ExpectedSourceId}' in top-3, got [{top3}]");
            }
        }

        var top3Hits = golden.Count - failures.Count;
        TestContext.WriteLine(
            $"Pre-rerank recall@{KnowledgeIndexConstants.MaxRerankerCandidates} (retriever only): " +
            $"{preRerankRecallHits}/{golden.Count} = {(double)preRerankRecallHits / golden.Count:P1}");
        TestContext.WriteLine($"Top-1 recall (full pipeline): {top1Hits}/{golden.Count} = {(double)top1Hits / golden.Count:P1}");
        TestContext.WriteLine($"Top-3 recall (full pipeline): {top3Hits}/{golden.Count} = {(double)top3Hits / golden.Count:P1}");

        foreach (var failure in failures)
        {
            TestContext.WriteLine(failure);
        }

        golden.Count.ShouldBeGreaterThan(0);
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
