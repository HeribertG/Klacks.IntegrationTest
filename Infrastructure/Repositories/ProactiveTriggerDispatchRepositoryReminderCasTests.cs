// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the ExecuteUpdateAsync surface of ProactiveTriggerDispatchRepository against
/// the real PostgreSQL database: the compare-and-swap pair TryAdvanceReminderAsync /
/// TryRescheduleReminderAsync (WHERE next_reminder_at_utc = expected AND acknowledged_at_utc IS NULL)
/// and the set-based AcknowledgeAllForKindAsync of F1, which acknowledges every open row of one user
/// for one trigger kind when that user mutes the kind. The EF InMemory provider cannot execute that
/// shape at all, so only a real database proves the conditional update, the affected-row count, and
/// that the optimistic-concurrency semantics hold.
/// SHARED-DATABASE SAFETY: the two CAS statements are scoped to a single row by primary key, so they
/// can only ever touch rows this fixture inserted. AcknowledgeAllForKindAsync is scoped by
/// (user, kind) instead, so its tests use trigger kinds that carry the fixture prefix and a synthetic
/// user id - no production row can share both. All fixture rows carry the DedupKey prefix
/// INTEGRATION_TEST_DISPATCH_, and cleanup deletes only rows with that prefix.
/// </summary>

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
public class ProactiveTriggerDispatchRepositoryReminderCasTests
{
    private const string TestPrefix = "INTEGRATION_TEST_DISPATCH_";

    private const string AcknowledgeAllKind = TestPrefix + "ack_all";
    private const string ForeignKind = TestPrefix + "ack_all_other";

    private static readonly Guid TestUserId = new("8f8b22ef-0000-4000-8000-0000000000c2");
    private static readonly Guid ForeignUserId = new("8f8b22ef-0000-4000-8000-0000000000c3");

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();
    }

    [TearDown]
    public async Task CleanupAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_trigger_dispatches WHERE dedup_key LIKE {0}",
            TestPrefix + "%");
    }

    [Test]
    public async Task TryAdvanceReminderAsync_HappyPath_IncrementsCountAndMovesSchedule()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var nextDue = DateTime.UtcNow.AddHours(4);
        var row = await GivenDispatchAsync(nextReminderAtUtc: due);

        await using var context = NewContext();
        var repository = NewRepository(context);
        var remindedAt = DateTime.UtcNow;

        var result = await repository.TryAdvanceReminderAsync(row.Id, due, remindedAt, nextDue);

        result.ShouldBeTrue();

        var reloaded = await ReloadAsync(row.Id);
        reloaded.ReminderCount.ShouldBe(1);
        reloaded.LastRemindedAtUtc.ShouldNotBeNull().ShouldBe(remindedAt, TimeSpan.FromSeconds(2));
        reloaded.NextReminderAtUtc.ShouldNotBeNull().ShouldBe(nextDue, TimeSpan.FromSeconds(2));
        reloaded.ReadAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task TryAdvanceReminderAsync_WrongExpectedDue_LosesCasAndLeavesRowUntouched()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var row = await GivenDispatchAsync(nextReminderAtUtc: due);

        await using var context = NewContext();
        var repository = NewRepository(context);

        var result = await repository.TryAdvanceReminderAsync(
            row.Id, expectedDueUtc: due.AddMinutes(1), remindedAtUtc: DateTime.UtcNow, nextDueUtc: due.AddHours(4));

        result.ShouldBeFalse();

        var reloaded = await ReloadAsync(row.Id);
        reloaded.ReminderCount.ShouldBe(0);
        reloaded.LastRemindedAtUtc.ShouldBeNull();
        reloaded.NextReminderAtUtc.ShouldNotBeNull().ShouldBe(due, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task TryAdvanceReminderAsync_AcknowledgedRow_LosesCasAndLeavesRowUntouched()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var acknowledgedAt = DateTime.UtcNow.AddMinutes(-1);
        var row = await GivenDispatchAsync(nextReminderAtUtc: due, acknowledgedAtUtc: acknowledgedAt);

        await using var context = NewContext();
        var repository = NewRepository(context);

        var result = await repository.TryAdvanceReminderAsync(row.Id, due, DateTime.UtcNow, due.AddHours(4));

        result.ShouldBeFalse();

        var reloaded = await ReloadAsync(row.Id);
        reloaded.ReminderCount.ShouldBe(0);
        reloaded.LastRemindedAtUtc.ShouldBeNull();
        reloaded.NextReminderAtUtc.ShouldNotBeNull().ShouldBe(due, TimeSpan.FromSeconds(2));
        reloaded.AcknowledgedAtUtc.ShouldNotBeNull().ShouldBe(acknowledgedAt, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task TryAdvanceReminderAsync_ClearsReadMark()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var row = await GivenDispatchAsync(nextReminderAtUtc: due, readAtUtc: DateTime.UtcNow.AddMinutes(-2));

        await using var context = NewContext();
        var repository = NewRepository(context);

        var result = await repository.TryAdvanceReminderAsync(row.Id, due, DateTime.UtcNow, due.AddHours(4));

        result.ShouldBeTrue();

        var reloaded = await ReloadAsync(row.Id);
        reloaded.ReadAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task TryRescheduleReminderAsync_HappyPath_MovesNextDueWithoutTouchingCount()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var newDue = DateTime.UtcNow.AddHours(24);
        var row = await GivenDispatchAsync(nextReminderAtUtc: due, reminderCount: 2);

        await using var context = NewContext();
        var repository = NewRepository(context);

        var result = await repository.TryRescheduleReminderAsync(row.Id, due, newDue);

        result.ShouldBeTrue();

        var reloaded = await ReloadAsync(row.Id);
        reloaded.NextReminderAtUtc.ShouldNotBeNull().ShouldBe(newDue, TimeSpan.FromSeconds(2));
        reloaded.ReminderCount.ShouldBe(2);
        reloaded.LastRemindedAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task TryRescheduleReminderAsync_NullNextDue_StopsReminders()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var row = await GivenDispatchAsync(nextReminderAtUtc: due, reminderCount: 3);

        await using var context = NewContext();
        var repository = NewRepository(context);

        var result = await repository.TryRescheduleReminderAsync(row.Id, due, nextDueUtc: null);

        result.ShouldBeTrue();

        var reloaded = await ReloadAsync(row.Id);
        reloaded.NextReminderAtUtc.ShouldBeNull();
        reloaded.ReminderCount.ShouldBe(3);
    }

    [Test]
    public async Task TryRescheduleReminderAsync_WrongExpectedDue_LosesCasAndLeavesRowUntouched()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var row = await GivenDispatchAsync(nextReminderAtUtc: due, reminderCount: 1);

        await using var context = NewContext();
        var repository = NewRepository(context);

        var result = await repository.TryRescheduleReminderAsync(
            row.Id, expectedDueUtc: due.AddMinutes(1), nextDueUtc: due.AddHours(24));

        result.ShouldBeFalse();

        var reloaded = await ReloadAsync(row.Id);
        reloaded.NextReminderAtUtc.ShouldNotBeNull().ShouldBe(due, TimeSpan.FromSeconds(2));
        reloaded.ReminderCount.ShouldBe(1);
    }

    [Test]
    public async Task AcknowledgeAllForKindAsync_AcknowledgesEveryOpenRowOfThatUserAndKindAndStopsTheirReminders()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var first = await GivenDispatchAsync(nextReminderAtUtc: due, triggerKind: AcknowledgeAllKind);
        var second = await GivenDispatchAsync(nextReminderAtUtc: due.AddHours(3), triggerKind: AcknowledgeAllKind);

        var before = DateTime.UtcNow;
        await using var context = NewContext();

        var acknowledged = await NewRepository(context)
            .AcknowledgeAllForKindAsync(TestUserId.ToString(), AcknowledgeAllKind);

        acknowledged.ShouldBe(2);

        foreach (var id in new[] { first.Id, second.Id })
        {
            var reloaded = await ReloadAsync(id);
            reloaded.AcknowledgedAtUtc.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-2));
            reloaded.NextReminderAtUtc.ShouldBeNull();
        }
    }

    [Test]
    public async Task AcknowledgeAllForKindAsync_LeavesOtherKindsOtherUsersAndAlreadyAcknowledgedRowsAlone()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var firstAcknowledgedAt = DateTime.UtcNow.AddHours(-3);

        var target = await GivenDispatchAsync(nextReminderAtUtc: due, triggerKind: AcknowledgeAllKind);
        var alreadyAcknowledged = await GivenDispatchAsync(
            nextReminderAtUtc: null, acknowledgedAtUtc: firstAcknowledgedAt, triggerKind: AcknowledgeAllKind);
        var otherKind = await GivenDispatchAsync(nextReminderAtUtc: due, triggerKind: ForeignKind);
        var otherUser = await GivenDispatchAsync(
            nextReminderAtUtc: due, triggerKind: AcknowledgeAllKind, userId: ForeignUserId);

        await using var context = NewContext();

        var acknowledged = await NewRepository(context)
            .AcknowledgeAllForKindAsync(TestUserId.ToString(), AcknowledgeAllKind);

        acknowledged.ShouldBe(1, "Only the one still open row of that user and kind is acknowledged.");
        (await ReloadAsync(target.Id)).AcknowledgedAtUtc.ShouldNotBeNull();

        (await ReloadAsync(alreadyAcknowledged.Id)).AcknowledgedAtUtc
            .ShouldNotBeNull().ShouldBe(firstAcknowledgedAt, TimeSpan.FromSeconds(2),
                "An acknowledged row keeps its first timestamp.");

        var untouchedKind = await ReloadAsync(otherKind.Id);
        untouchedKind.AcknowledgedAtUtc.ShouldBeNull();
        untouchedKind.NextReminderAtUtc.ShouldNotBeNull().ShouldBe(due, TimeSpan.FromSeconds(2));

        var untouchedUser = await ReloadAsync(otherUser.Id);
        untouchedUser.AcknowledgedAtUtc.ShouldBeNull();
        untouchedUser.NextReminderAtUtc.ShouldNotBeNull().ShouldBe(due, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task AcknowledgeAllForKindAsync_NothingOpen_ReturnsZero()
    {
        await using var context = NewContext();

        var acknowledged = await NewRepository(context)
            .AcknowledgeAllForKindAsync(TestUserId.ToString(), TestPrefix + "never_used");

        acknowledged.ShouldBe(0);
    }

    private static async Task<ProactiveTriggerDispatchRow> GivenDispatchAsync(
        DateTime? nextReminderAtUtc,
        DateTime? acknowledgedAtUtc = null,
        DateTime? readAtUtc = null,
        int reminderCount = 0,
        string triggerKind = "IntegrationTest",
        Guid? userId = null)
    {
        var row = new ProactiveTriggerDispatchRow
        {
            Id = Guid.NewGuid(),
            UserId = (userId ?? TestUserId).ToString(),
            TriggerKind = triggerKind,
            DedupKey = TestPrefix + Guid.NewGuid().ToString("N")[..16],
            NextReminderAtUtc = nextReminderAtUtc,
            AcknowledgedAtUtc = acknowledgedAtUtc,
            ReadAtUtc = readAtUtc,
            ReminderCount = reminderCount,
        };

        await using var context = NewContext();
        context.AgentTriggerDispatches.Add(row);
        await context.SaveChangesAsync();

        return row;
    }

    private static async Task<ProactiveTriggerDispatchRow> ReloadAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.AgentTriggerDispatches.SingleAsync(d => d.Id == id);
    }

    private static ProactiveTriggerDispatchRepository NewRepository(DataBaseContext context)
    {
        return new ProactiveTriggerDispatchRepository(context, TimeProvider.System);
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
