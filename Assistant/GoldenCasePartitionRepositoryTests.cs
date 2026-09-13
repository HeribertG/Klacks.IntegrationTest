// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that the golden-case repository can read the holdout partition on its own, count it, list a
/// whole origin for the seeder, insert in bulk and write back a corrected expectation. Two behaviours
/// only a real database shows: the holdout budget has to be spent on the seeded goldset cases before the
/// learned ones, and the seeder's write-back updates rows it read without tracking. Runs against the
/// shared integration database and only ever touches rows whose query starts with INTEGRATION_TEST_.
/// </summary>

using Klacks.Api.Domain.Constants;
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
public class GoldenCasePartitionRepositoryTests
{
    private const string TestPrefix = "INTEGRATION_TEST_GOLDENPART_";
    private const string TrainQuery = TestPrefix + "train";
    private const string HoldoutQuery = TestPrefix + "holdout";
    private const string ClusterQuery = TestPrefix + "cluster";
    private const string ExpectedSkill = TestPrefix + "expected_skill";
    private const string CorrectedSkill = TestPrefix + "corrected_skill";

    private static readonly DateTime SeededAt = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private SkillLearningGoldenCaseRepository _repository = null!;

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
    public void SetUp()
    {
        _context = NewContext();
        _repository = new SkillLearningGoldenCaseRepository(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task AddRange_ThenHoldoutReads_SeeOnlyTheHoldoutPartition()
    {
        await _repository.AddRangeAsync(
        [
            Case(TrainQuery, GoldenCaseOrigins.Goldset, GoldenCasePartitions.Train),
            Case(HoldoutQuery, GoldenCaseOrigins.Goldset, GoldenCasePartitions.Holdout),
            Case(ClusterQuery, GoldenCaseOrigins.Cluster, GoldenCasePartitions.Holdout)
        ]);

        var holdout = await _repository.ListHoldoutAsync(
            SkillLearningDefaults.MaxGoldenCasesPerRegressionCheck);
        var ours = holdout.Where(c => c.Query.StartsWith(TestPrefix, StringComparison.Ordinal)).ToList();

        ours.Count.ShouldBe(2);
        ours.Select(c => c.Query).ShouldContain(HoldoutQuery);
        ours.Select(c => c.Query).ShouldContain(ClusterQuery);
        ours.Select(c => c.Query).ShouldNotContain(TrainQuery);
    }

    [Test]
    public async Task CountHoldout_CountsTheSamePopulationTheGateReplays()
    {
        var before = await _repository.CountHoldoutAsync();

        await _repository.AddRangeAsync(
        [
            Case(TrainQuery, GoldenCaseOrigins.Goldset, GoldenCasePartitions.Train),
            Case(HoldoutQuery, GoldenCaseOrigins.Goldset, GoldenCasePartitions.Holdout)
        ]);

        var after = await _repository.CountHoldoutAsync();

        (after - before).ShouldBe(1);
    }

    [Test]
    public async Task ListByOrigin_ReturnsTheSeederIdempotencyKeys()
    {
        await _repository.AddRangeAsync(
        [
            Case(HoldoutQuery, GoldenCaseOrigins.Goldset, GoldenCasePartitions.Holdout),
            Case(ClusterQuery, GoldenCaseOrigins.Cluster, GoldenCasePartitions.Holdout)
        ]);

        var goldset = await _repository.ListByOriginAsync(GoldenCaseOrigins.Goldset);
        var ours = goldset.Where(c => c.Query.StartsWith(TestPrefix, StringComparison.Ordinal)).ToList();

        ours.Count.ShouldBe(1);
        ours[0].Query.ShouldBe(HoldoutQuery);
    }

    // The goldset cases are seeded once at startup and are therefore the oldest holdout rows in the
    // database forever. A budget the size of the goldset half has to be spent on them alone, or the
    // gate replays cluster-born cases instead of the curated population it was built for.
    [Test]
    public async Task AnAgedGoldsetCase_SurvivesABudgetTheSizeOfTheGoldsetHalf()
    {
        await _repository.AddRangeAsync(
        [
            Case(HoldoutQuery, GoldenCaseOrigins.Goldset, GoldenCasePartitions.Holdout),
            Case(ClusterQuery, GoldenCaseOrigins.Cluster, GoldenCasePartitions.Holdout)
        ]);
        await AgeAsync(HoldoutQuery);

        var budget = await _context.SkillLearningGoldenCases.CountAsync(
            c => c.Partition == GoldenCasePartitions.Holdout && c.Origin == GoldenCaseOrigins.Goldset);

        var holdout = await _repository.ListHoldoutAsync(budget);

        holdout.Select(c => c.Query).ShouldContain(HoldoutQuery);
        holdout.Select(c => c.Query).ShouldNotContain(ClusterQuery);
    }

    // The shape of a later startup: the row is already in the table, and the seeder of the new process
    // reads it with AsNoTracking and hands it back changed. Only a real provider shows whether that
    // detached round trip reaches the table.
    [Test]
    public async Task UpdateRange_WritesBackACorrectedExpectationOfADetachedRow()
    {
        await _repository.AddRangeAsync(
        [
            Case(HoldoutQuery, GoldenCaseOrigins.Goldset, GoldenCasePartitions.Holdout)
        ]);

        await using var seederContext = NewContext();
        var seeder = new SkillLearningGoldenCaseRepository(seederContext);
        var detached = (await seeder.ListByOriginAsync(GoldenCaseOrigins.Goldset))
            .Single(c => c.Query == HoldoutQuery);
        detached.ExpectedSourceId = CorrectedSkill;

        await seeder.UpdateRangeAsync([detached]);

        await using var verification = NewContext();
        var stored = await verification.SkillLearningGoldenCases
            .AsNoTracking()
            .SingleAsync(c => c.Query == HoldoutQuery);
        stored.ExpectedSourceId.ShouldBe(CorrectedSkill);
    }

    private async Task AgeAsync(string query)
    {
        var stored = await _context.SkillLearningGoldenCases.SingleAsync(c => c.Query == query);
        stored.CreateTime = SeededAt;
        await _context.SaveChangesAsync();
    }

    private static SkillLearningGoldenCase Case(string query, string origin, string partition) =>
        new()
        {
            Id = Guid.NewGuid(),
            Query = query,
            Locale = "de",
            ExpectedSourceId = ExpectedSkill,
            Origin = origin,
            Partition = partition
        };

    private static async Task CleanupAsync(DataBaseContext context)
    {
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM skill_learning_golden_cases WHERE query LIKE {0}", TestPrefix + "%");
    }
}
