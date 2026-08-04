// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Wall-clock profile of the retrieval stage on the hot chat path, resolved through the real
/// application DI container. Answers one question the recall reports cannot: how many milliseconds
/// of the pre-LLM serial prologue are spent in <c>KnowledgeRetrievalService.RetrieveAsync</c>, and
/// how that time splits across query embedding, the pgvector KNN pass and the cross-encoder rerank.
/// Same boot-time workarounds and synchronizer neutralization as
/// <see cref="KnowledgeIndexHardGoldenSetDiHostTests"/> — see that file for the rationale.
/// Reporting only, never asserts on timings: the numbers depend on the host CPU.
/// </summary>

using System.Diagnostics;
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
public class KnowledgeIndexLatencyProfileDiHostTests
{
    private const int WarmupRuns = 2;

    private static readonly string[] ProfileQueries =
    {
        "Wie viele Mitarbeiter haben wir?",
        "Lege einen neuen Mitarbeiter Hans Muster an",
        "Zeige mir die Abwesenheiten von nächster Woche",
        "Trage Ferien für Anna vom 3. bis 7. August ein",
        "Welche Verträge gibt es?",
        "Öffne die Einstellungen",
        "Wer arbeitet am Montag in der Frühschicht?",
        "Hallo, wie geht es dir?",
        "Füge die Gruppe Nord dem Dienstplan hinzu",
        "Was kostet ein Liter Milch in Zürich?",
    };

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

    private sealed class LatencyProfileBypassFactory : SignalRTestWebApplicationFactory
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

    private LatencyProfileBypassFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new LatencyProfileBypassFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [Test]
    public async Task RetrievalStage_WallClockProfile_PerQuery()
    {
        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var reranker = scope.ServiceProvider.GetRequiredService<IRerankerProvider>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");
        TestContext.WriteLine($"Processor count: {Environment.ProcessorCount}");
        TestContext.WriteLine(
            $"TopK={KnowledgeIndexConstants.DefaultTopK}, " +
            $"RerankCandidates={KnowledgeIndexConstants.MaxRerankerCandidates}, " +
            $"RerankBatch={KnowledgeIndexConstants.RerankBatchSize}");

        var cold = Stopwatch.StartNew();
        await retrieval.RetrieveAsync(
            ProfileQueries[0], [], isAdmin: true, KnowledgeIndexConstants.DefaultTopK, null, CancellationToken.None);
        cold.Stop();
        TestContext.WriteLine($"COLD first RetrieveAsync (includes ONNX session warmup): {cold.ElapsedMilliseconds} ms");

        for (var i = 0; i < WarmupRuns; i++)
        {
            await retrieval.RetrieveAsync(
                ProfileQueries[i % ProfileQueries.Length], [], isAdmin: true,
                KnowledgeIndexConstants.DefaultTopK, null, CancellationToken.None);
        }

        var totalMs = new List<long>();
        var embedMs = new List<long>();
        var knnMs = new List<long>();
        var rerankMs = new List<long>();

        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine("query | total | embed | knn | rerank(n) [ms]");

        foreach (var query in ProfileQueries)
        {
            var whole = Stopwatch.StartNew();
            await retrieval.RetrieveAsync(
                query, [], isAdmin: true, KnowledgeIndexConstants.DefaultTopK, null, CancellationToken.None);
            whole.Stop();

            var embedWatch = Stopwatch.StartNew();
            var queryVec = await embeddingProvider.EmbedQueryAsync(query, CancellationToken.None);
            embedWatch.Stop();

            var knnWatch = Stopwatch.StartNew();
            var candidates = await repository.FindNearestAsync(
                queryVec, [], adminBypass: true, KnowledgeIndexConstants.MaxRerankerCandidates, CancellationToken.None);
            knnWatch.Stop();

            var rerankWatch = Stopwatch.StartNew();
            await reranker.ScoreAsync(query, candidates.Select(c => c.Text).ToList(), CancellationToken.None);
            rerankWatch.Stop();

            totalMs.Add(whole.ElapsedMilliseconds);
            embedMs.Add(embedWatch.ElapsedMilliseconds);
            knnMs.Add(knnWatch.ElapsedMilliseconds);
            rerankMs.Add(rerankWatch.ElapsedMilliseconds);

            TestContext.WriteLine(
                $"{Truncate(query, 40)} | {whole.ElapsedMilliseconds} | {embedWatch.ElapsedMilliseconds} | " +
                $"{knnWatch.ElapsedMilliseconds} | {rerankWatch.ElapsedMilliseconds} ({candidates.Count})");
        }

        TestContext.WriteLine(string.Empty);
        Report("RetrieveAsync total", totalMs);
        Report("  EmbedQueryAsync", embedMs);
        Report("  FindNearestAsync (pgvector KNN)", knnMs);
        Report("  ScoreAsync (cross-encoder rerank)", rerankMs);

        totalMs.Count.ShouldBe(ProfileQueries.Length);
    }

    private static void Report(string label, List<long> samples)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        TestContext.WriteLine(
            $"{label}: min={sorted[0]} median={sorted[sorted.Count / 2]} " +
            $"max={sorted[^1]} avg={samples.Average():F0} ms");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value.PadRight(max) : value[..max];
}
