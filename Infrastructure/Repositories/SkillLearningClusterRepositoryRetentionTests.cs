// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for SkillLearningClusterRepository.SoftDeleteRetentionEligibleOlderThanAsync against
/// the real PostgreSQL database. The method is a SET-based sweep (ExecuteUpdateAsync), and the EF InMemory
/// provider used by the unit tests cannot execute that shape at all - only a real database proves the
/// update, the partial-unique-index interplay with soft-deleted rows, and the global query filter on
/// is_deleted.
/// SHARED-DATABASE SAFETY: the sweep has no caller-side scoping - it collects EVERY eligible row older than
/// the threshold, including real dev-DB rows. This fixture therefore works with an inverted clock: the
/// sweep threshold is fixed at 1950 and the fixture's "old" rows are dated 1900, so the sweep can only
/// ever reach rows dated before 1950 - and no real cluster the dev app ever wrote is that old. Rows that
/// must stay untouched carry current dates, which are past the threshold by construction. Cleanup deletes
/// ONLY rows carrying this fixture's cluster_key prefix.
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

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class SkillLearningClusterRepositoryRetentionTests
{
    private const string TestPrefix = "INTEGRATION_TEST_RET_";

    private static readonly Guid TestAgentId = new("7e7a11de-0000-4000-8000-0000000000b1");

    // The inverted clock described in the class remarks: fixture "old" rows predate the sweep threshold,
    // every real dev-DB row postdates it, so the sweep is physically unable to touch real data.
    private static readonly DateTime FarPastUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SweepThresholdUtc = new(1950, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();
    }

    [TearDown]
    public async Task CleanupAsync()
    {
        // Physical delete via raw SQL: the query filter would hide the rows this fixture's own sweep
        // soft-deleted, and the partial unique index on (agent_id, cluster_key) only tolerates one live
        // row per key.
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM skill_learning_clusters WHERE cluster_key LIKE {0}",
            TestPrefix + "%");
    }

    [Test]
    public async Task Old_Eligible_Rows_AreSoftDeleted_AndASecondSweep_IsIdempotent()
    {
        foreach (var status in new[]
        {
            SkillLearningClusterStatuses.Retired,
            SkillLearningClusterStatuses.Dismissed,
            SkillLearningClusterStatuses.Unfulfillable,
        })
        {
            await GivenClusterAsync(status, statusChangedAtUtc: FarPastUtc, lastSeenAtUtc: FarPastUtc);
        }

        await using var context = NewContext();
        var repository = new SkillLearningClusterRepository(context);

        var affected = await repository.SoftDeleteRetentionEligibleOlderThanAsync(SweepThresholdUtc);
        var secondPass = await repository.SoftDeleteRetentionEligibleOlderThanAsync(SweepThresholdUtc);

        affected.ShouldBe(3);
        secondPass.ShouldBe(0);

        // Read-Back must ignore the query filter: the whole point of the sweep is that the rows drop out
        // of the filtered view, so only the unfiltered read can prove the soft delete actually landed.
        var rows = await context.SkillLearningClusters
            .IgnoreQueryFilters()
            .Where(c => c.ClusterKey.StartsWith(TestPrefix))
            .ToListAsync();

        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(c => c.IsDeleted && c.DeletedTime != null);
    }

    [Test]
    public async Task Young_Or_NotEligible_Rows_StayUntouched()
    {
        var now = DateTime.UtcNow;

        // Eligible status, but the last-seen clock is still running: an unfulfillable cluster keeps
        // counting recurrences, and that recurrence must protect it from the sweep.
        var stillCounting = await GivenClusterAsync(
            SkillLearningClusterStatuses.Unfulfillable, statusChangedAtUtc: FarPastUtc, lastSeenAtUtc: now);

        // Old, but statuses retention may never collect.
        var collecting = await GivenClusterAsync(
            SkillLearningClusterStatuses.Collecting, statusChangedAtUtc: FarPastUtc, lastSeenAtUtc: FarPastUtc);
        var ready = await GivenClusterAsync(
            SkillLearningClusterStatuses.Ready, statusChangedAtUtc: FarPastUtc, lastSeenAtUtc: FarPastUtc);
        var learnedPhrase = await GivenClusterAsync(
            SkillLearningClusterStatuses.LearnedPhrase, statusChangedAtUtc: FarPastUtc, lastSeenAtUtc: FarPastUtc);

        await using var context = NewContext();
        var repository = new SkillLearningClusterRepository(context);

        var affected = await repository.SoftDeleteRetentionEligibleOlderThanAsync(SweepThresholdUtc);

        affected.ShouldBe(0);

        var rows = await context.SkillLearningClusters
            .IgnoreQueryFilters()
            .Where(c => c.ClusterKey.StartsWith(TestPrefix))
            .ToListAsync();

        rows.Count.ShouldBe(4);
        rows.ShouldAllBe(c => !c.IsDeleted && c.DeletedTime == null);
        rows.Select(c => c.Id).ShouldBe(
            new[] { stillCounting.Id, collecting.Id, ready.Id, learnedPhrase.Id },
            ignoreOrder: true);
    }

    private static async Task<SkillLearningCluster> GivenClusterAsync(
        string status, DateTime statusChangedAtUtc, DateTime lastSeenAtUtc)
    {
        var cluster = new SkillLearningCluster
        {
            Id = Guid.NewGuid(),
            AgentId = TestAgentId,
            ClusterKey = TestPrefix + Guid.NewGuid().ToString("N")[..16],
            IntentExcerpt = TestPrefix + "intent",
            Locale = "de",
            Status = status,
            StatusChangedAtUtc = statusChangedAtUtc,
            FirstSeenAtUtc = statusChangedAtUtc,
            LastSeenAtUtc = lastSeenAtUtc,
        };

        await using var context = NewContext();
        context.SkillLearningClusters.Add(cluster);
        await context.SaveChangesAsync();

        return cluster;
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }
}
