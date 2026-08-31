// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for ProactiveTriggerDispatchRepository.TryAdvanceReminderAsync and
/// TryRescheduleReminderAsync against the real PostgreSQL database. Both methods are compare-and-swap
/// ExecuteUpdateAsync statements (WHERE next_reminder_at_utc = expected AND acknowledged_at_utc IS NULL),
/// and the EF InMemory provider cannot execute that shape - only a real database proves the conditional
/// update, the affected-row count, and that the optimistic-concurrency semantics hold.
/// SHARED-DATABASE SAFETY: both CAS statements are scoped to a single row by primary key, so they can
/// only ever touch rows this fixture inserted. All fixture rows carry the DedupKey prefix
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

    private static readonly Guid TestUserId = new("8f8b22ef-0000-4000-8000-0000000000c2");

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

    private static async Task<ProactiveTriggerDispatchRow> GivenDispatchAsync(
        DateTime? nextReminderAtUtc,
        DateTime? acknowledgedAtUtc = null,
        DateTime? readAtUtc = null,
        int reminderCount = 0)
    {
        var row = new ProactiveTriggerDispatchRow
        {
            Id = Guid.NewGuid(),
            UserId = TestUserId.ToString(),
            TriggerKind = "IntegrationTest",
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
