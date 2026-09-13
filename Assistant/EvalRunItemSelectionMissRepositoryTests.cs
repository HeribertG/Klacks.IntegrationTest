// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that the selection-miss query really is a query. Its whole point is that the caller's
/// conditions - the goldset item ids it may learn from and the presence of a chosen tool - narrow the
/// window in SQL rather than the result in memory; a filter applied after the limit leaves the window
/// occupied by rows nobody can spend. The in-memory provider the unit fixture uses is LINQ to Objects and
/// would translate anything, so only a real provider can show that the id set becomes a predicate and
/// that the null handling of the chosen-tool test survives SQL's three-valued logic.
/// Runs against the shared integration database and only ever touches rows of its own eval run, whose
/// goldset name starts with INTEGRATION_TEST_.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class EvalRunItemSelectionMissRepositoryTests
{
    private const string TestPrefix = "INTEGRATION_TEST_EVALITEM_";
    private const string Goldset = TestPrefix + "goldset";
    private const string TrainItem = TestPrefix + "train";
    private const string SecondTrainItem = TestPrefix + "train2";
    private const string HoldoutItem = TestPrefix + "holdout";
    private const string ExpectedTool = "add_client_note";
    private const string ChosenTool = "search_employees";
    private const int Limit = 50;

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private EvalRunItemRepository _repository = null!;
    private Guid _runId;

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _repository = new EvalRunItemRepository(_context);
        _runId = Guid.NewGuid();

        _context.EvalRuns.Add(new EvalRun
        {
            Id = _runId,
            Goldset = Goldset,
            Model = "deepseek-v4-pro",
            Provider = "deepseek",
            ItemsTotal = 3,
            ItemsPassed = 0
        });

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task TheItemIdSet_BecomesAPredicateAndNotAnAfterthought()
    {
        await _repository.AddRangeAsync(
        [
            Miss(TrainItem),
            Miss(HoldoutItem)
        ]);

        var misses = await _repository.ListUnconsumedSelectionMissesAsync(_runId, [TrainItem], Limit);

        misses.Count.ShouldBe(1);
        misses[0].ItemId.ShouldBe(TrainItem);
    }

    [Test]
    public async Task SeveralItemIds_AreAllTranslated()
    {
        await _repository.AddRangeAsync(
        [
            Miss(TrainItem),
            Miss(SecondTrainItem),
            Miss(HoldoutItem)
        ]);

        var misses = await _repository.ListUnconsumedSelectionMissesAsync(
            _runId, [TrainItem, SecondTrainItem], Limit);

        misses.Select(m => m.ItemId).Order().ShouldBe([TrainItem, SecondTrainItem]);
    }

    // A NULL comparison in SQL is not false, it is unknown - a row without a chosen tool has to be
    // excluded by the predicate itself, not by luck.
    [Test]
    public async Task AMissWithoutAChosenTool_IsExcludedByTheQuery()
    {
        var withoutTool = Miss(SecondTrainItem);
        withoutTool.ChosenTool = null;
        var emptyTool = Miss(HoldoutItem);
        emptyTool.ChosenTool = string.Empty;

        await _repository.AddRangeAsync([Miss(TrainItem), withoutTool, emptyTool]);

        var misses = await _repository.ListUnconsumedSelectionMissesAsync(
            _runId, [TrainItem, SecondTrainItem, HoldoutItem], Limit);

        misses.Count.ShouldBe(1);
        misses[0].ItemId.ShouldBe(TrainItem);
    }

    [Test]
    public async Task AnEmptyIdSet_ReturnsNothing()
    {
        await _repository.AddRangeAsync([Miss(TrainItem)]);

        (await _repository.ListUnconsumedSelectionMissesAsync(_runId, [], Limit)).ShouldBeEmpty();
    }

    [Test]
    public async Task AWatermarkedMiss_IsNeverOfferedAgain()
    {
        await _repository.AddRangeAsync([Miss(TrainItem)]);
        var first = await _repository.ListUnconsumedSelectionMissesAsync(_runId, [TrainItem], Limit);

        await _repository.MarkConsumedAsync([first[0].Id], DateTime.UtcNow);

        (await _repository.ListUnconsumedSelectionMissesAsync(_runId, [TrainItem], Limit)).ShouldBeEmpty();
    }

    private EvalRunItem Miss(string itemId) => new()
    {
        Id = Guid.NewGuid(),
        EvalRunId = _runId,
        ItemId = itemId,
        Locale = "de",
        ExpectedTool = ExpectedTool,
        ChosenTool = ChosenTool,
        ToolsetNamesJson = "[\"add_client_note\"]",
        RetrievalHit = true,
        SelectionHit = false,
        Passed = false,
        LatencyMs = 1200
    };

    private static async Task CleanupAsync(DataBaseContext context)
    {
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM eval_run_items WHERE eval_run_id IN (SELECT id FROM eval_runs WHERE goldset = {0})",
            Goldset);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM eval_runs WHERE goldset = {0}", Goldset);
    }
}
