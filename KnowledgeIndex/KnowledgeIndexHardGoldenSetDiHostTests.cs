// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Recall report for the "hard" golden set (knowledge-index-golden-hard.json), resolved through the
/// real application DI container — same rationale and boot-time workarounds as
/// <see cref="KnowledgeIndexGoldenSetDiHostTests"/> (see that file for the full explanation of the
/// UiControl/RegionSetup "Kind=Utc into timestamp without time zone" startup bug). This is the only
/// way to measure retrieval quality on a host where ONNX cannot run (Windows ARM64 / Snapdragon X).
/// Reporting only — always green as long as the golden set loads; no MinPassRate gate, since the
/// fallback pipeline is knowingly weaker than the ONNX cross-encoder and is not the CI/production
/// quality bar. Additionally neutralizes <see cref="IKnowledgeIndexSynchronizer"/>: booting this
/// host resolves an embedding provider that differs from whatever originally embedded the 354
/// existing knowledge_index rows on the shared dev DB (ComputeTextHash folds EmbeddingSpaceId into
/// the stored hash), so an un-neutralized synchronizer would see every row as "changed" and
/// re-embed + overwrite all of them at startup — a write this module is not permitted to make.
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
public class KnowledgeIndexHardGoldenSetDiHostTests
{
    private static readonly string GoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard.json");

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
        var golden = LoadGoldenSet();

        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");

        var failures = new List<string>();
        var top1Hits = 0;
        var preRerankRecallHits = 0;
        var top1Misses = new List<string>();

        foreach (var item in golden)
        {
            var queryVec = await embeddingProvider.EmbedQueryAsync(item.Query, CancellationToken.None);
            var preRerankCandidates = await repository.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);
            if (preRerankCandidates.Any(c => item.Accepts(c.SourceId)))
            {
                preRerankRecallHits++;
            }

            var result = await retrieval.RetrieveAsync(
                item.Query, [], isAdmin: true, topK: 3, currentRoute: null, CancellationToken.None);

            var isTop1 = result.Candidates.Count > 0 &&
                item.Accepts(result.Candidates[0].Entry.SourceId);
            if (isTop1)
            {
                top1Hits++;
            }

            var foundTop3 = result.Candidates.Any(c =>
                item.Accepts(c.Entry.SourceId));
            if (!foundTop3)
            {
                var top3 = string.Join(", ", result.Candidates.Select(c => c.Entry.SourceId));
                failures.Add($"Query '{item.Query}': expected '{item.ExpectedDisplay}' in top-3, got [{top3}]");
            }

            if (!isTop1)
            {
                var top3Scored = string.Join(", ", result.Candidates.Select(c => $"{c.Entry.SourceId}={c.Score:F4}"));
                top1Misses.Add($"TOP1MISS | expected={item.ExpectedDisplay} | query=\"{item.Query}\" | top3=[{top3Scored}]");
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

        TestContext.WriteLine($"--- Full top-1 miss detail ({top1Misses.Count} of {golden.Count}) ---");
        foreach (var miss in top1Misses)
        {
            TestContext.WriteLine(miss);
        }

        golden.Count.ShouldBeGreaterThan(0);
    }

    private static List<HardGoldenSetItem> LoadGoldenSet() => HardGoldenSetItem.Load(GoldenSetPath);
}
