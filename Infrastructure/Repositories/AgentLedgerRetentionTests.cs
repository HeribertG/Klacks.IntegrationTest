// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the ledger retention soft-delete against the real PostgreSQL database, the part
/// the in-memory provider cannot run because it does not support ExecuteUpdateAsync. The cut-offs are
/// pinned in the year 2001 and every seeded row is dated 2000 or 2001, so the bulk UPDATE cannot reach any
/// row the dev app or another fixture wrote. Cleanup deletes ONLY rows carrying the INTEGRATION_TEST_ prefix.
/// </summary>

using Klacks.Api.Domain.Enums;
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
public class AgentLedgerRetentionTests
{
    private const string TestPrefix = "INTEGRATION_TEST_RETENTION_";

    private static readonly DateTime OldUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CutoffUtc = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RecentUtc = new(2001, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowUtc = new(2001, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task ExpiredTerminalConditions_AreSoftDeletedWithTheirEvents_OpenAndRecentOnesAreKept()
    {
        var expired = await GivenConditionAsync(AgentConditionStatus.Resolved, OldUtc);
        var recent = await GivenConditionAsync(AgentConditionStatus.Resolved, RecentUtc);
        var open = await GivenConditionAsync(AgentConditionStatus.Reported, OldUtc);

        await using (var context = NewContext())
        {
            var repository = new AgentConditionRepository(context);
            var (conditions, events) = await repository.SoftDeleteExpiredAsync(CutoffUtc, CutoffUtc, NowUtc);

            conditions.ShouldBeGreaterThanOrEqualTo(1);
            events.ShouldBeGreaterThanOrEqualTo(1);
        }

        await using var verify = NewContext();
        (await verify.AgentConditions.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == expired.Id))
            .IsDeleted.ShouldBeTrue();
        (await verify.AgentConditions.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == recent.Id))
            .IsDeleted.ShouldBeFalse();
        (await verify.AgentConditions.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == open.Id))
            .IsDeleted.ShouldBeFalse();

        (await verify.AgentConditionEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.ConditionId == expired.Id).AllAsync(e => e.IsDeleted)).ShouldBeTrue();
        (await verify.AgentConditionEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.ConditionId == recent.Id).AllAsync(e => !e.IsDeleted)).ShouldBeTrue();
    }

    [Test]
    public async Task ExpiredDispatches_AreSoftDeleted_ExceptRowsLinkedToAnOpenCondition()
    {
        var open = await GivenConditionAsync(AgentConditionStatus.Reported, OldUtc);
        var closed = await GivenConditionAsync(AgentConditionStatus.Resolved, RecentUtc);
        var unlinked = await GivenDispatchAsync(null);
        var linkedToOpen = await GivenDispatchAsync(open.Id);
        var linkedToClosed = await GivenDispatchAsync(closed.Id);

        await using (var context = NewContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);
            await repository.SoftDeleteExpiredAsync(RecentUtc, NowUtc);
        }

        await using var verify = NewContext();
        (await verify.AgentTriggerDispatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == unlinked.Id))
            .IsDeleted.ShouldBeTrue();
        (await verify.AgentTriggerDispatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == linkedToOpen.Id))
            .IsDeleted.ShouldBeFalse();
        (await verify.AgentTriggerDispatches.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == linkedToClosed.Id))
            .IsDeleted.ShouldBeTrue();
    }

    private async Task<AgentCondition> GivenConditionAsync(AgentConditionStatus status, DateTime lastSeenAtUtc)
    {
        var condition = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = TestPrefix + "kind",
            Fingerprint = TestPrefix + Guid.NewGuid(),
            Severity = "low",
            Status = status,
            DetectedAtUtc = lastSeenAtUtc,
            LastSeenAtUtc = lastSeenAtUtc,
            PayloadJson = "{}"
        };
        var detection = new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = condition.Id,
            EventType = AgentConditionStatus.Detected.ToString(),
            AtUtc = lastSeenAtUtc,
            Detail = TestPrefix + "event"
        };

        await using var context = NewContext();
        context.AgentConditions.Add(condition);
        context.AgentConditionEvents.Add(detection);
        await context.SaveChangesAsync();

        return condition;
    }

    private async Task<ProactiveTriggerDispatchRow> GivenDispatchAsync(Guid? conditionId)
    {
        var row = new ProactiveTriggerDispatchRow
        {
            Id = Guid.NewGuid(),
            UserId = TestPrefix + "user",
            TriggerKind = TestPrefix + "kind",
            DedupKey = TestPrefix + Guid.NewGuid(),
            ConditionId = conditionId
        };

        await using var context = NewContext();
        context.AgentTriggerDispatches.Add(row);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE agent_trigger_dispatches SET create_time = {0} WHERE id = {1}", OldUtc, row.Id);

        return row;
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_trigger_dispatches WHERE trigger_kind LIKE {0}", TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_condition_events WHERE condition_id IN (SELECT id FROM agent_conditions WHERE trigger_kind LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE trigger_kind LIKE {0}", TestPrefix + "%");
    }
}
