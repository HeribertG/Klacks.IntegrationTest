// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for AgentConditionRepository against the real PostgreSQL database. They prove the
/// three properties the in-memory provider cannot: ExecuteUpdateAsync's conditional UPDATE really is a
/// compare-and-swap, so two API instances racing the same row produce exactly one winner; the partial
/// unique index on Fingerprint really rejects a second open row for the same fingerprint, which is what
/// makes InsertAsync return null instead of duplicating a finding; and a terminal row really stops
/// blocking that index, which is what lets a resolved condition re-arm.
///
/// NOT proven here: that the transaction around transition plus audit event holds. The loser of a claim
/// returns before it stages an event, so the single-event assertion follows from the early return; the
/// transaction only matters for a won update whose event insert then fails, which needs fault injection.
/// The matching unit tests run against a fake repository and prove the service above it, not this.
/// Cleanup deletes ONLY rows this fixture created - the dev app shares this database.
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
public class AgentConditionRepositoryCasTests
{
    private const string TestPrefix = "INTEGRATION_TEST_COND_";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
    }

    [Test]
    public async Task TwoInstancesClaimingTheSameTransition_ExactlyOneWins_AndOnlyItsEventIsWritten()
    {
        var condition = await GivenConditionAsync(NewFingerprint(), AgentConditionStatus.Detected);

        await using var firstContext = NewContext();
        await using var secondContext = NewContext();

        var firstClaim = new AgentConditionRepository(firstContext).TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Detected,
            AgentConditionStatus.Reported,
            null,
            AuditEvent(condition.Id, AgentConditionStatus.Reported, "first"));

        var secondClaim = new AgentConditionRepository(secondContext).TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Detected,
            AgentConditionStatus.Reported,
            null,
            AuditEvent(condition.Id, AgentConditionStatus.Reported, "second"));

        var outcomes = await Task.WhenAll(firstClaim, secondClaim);

        outcomes.Count(won => won).ShouldBe(1);

        await using var verify = NewContext();
        (await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == condition.Id))
            .Status.ShouldBe(AgentConditionStatus.Reported);

        // One event, not two, because the loser returns on affected == 0 before staging its event. This
        // does NOT prove the transaction: that covers the opposite case, a won update whose event insert
        // then fails, which needs fault injection no test here performs.
        var events = await verify.AgentConditionEvents.AsNoTracking()
            .Where(e => e.ConditionId == condition.Id)
            .ToListAsync();
        events.Count.ShouldBe(1);
        events.Single().EventType.ShouldBe(AgentConditionStatus.Reported.ToString());
    }

    [Test]
    public async Task ClaimAgainstAStaleExpectedStatus_LosesAndChangesNothing()
    {
        var condition = await GivenConditionAsync(NewFingerprint(), AgentConditionStatus.Detected);

        await using var context = NewContext();
        var repository = new AgentConditionRepository(context);

        (await repository.TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Detected,
            AgentConditionStatus.Reported,
            null,
            AuditEvent(condition.Id, AgentConditionStatus.Reported, "won"))).ShouldBeTrue();

        (await repository.TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Detected,
            AgentConditionStatus.Reported,
            null,
            AuditEvent(condition.Id, AgentConditionStatus.Reported, "stale"))).ShouldBeFalse();

        await using var verify = NewContext();
        (await verify.AgentConditionEvents.AsNoTracking().CountAsync(e => e.ConditionId == condition.Id))
            .ShouldBe(1);
    }

    [Test]
    public async Task TransitionWritesTheSuppliedFieldsAndLeavesUnsetOnesAlone()
    {
        var condition = await GivenConditionAsync(NewFingerprint(), AgentConditionStatus.Prepared);
        var rejectedBy = Guid.NewGuid();

        await using var context = NewContext();
        var moved = await new AgentConditionRepository(context).TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Prepared,
            AgentConditionStatus.Rejected,
            new AgentConditionTransitionFields(
                HandledAtUtc: condition.DetectedAtUtc.AddMinutes(3),
                HandlingKind: AgentConditionHandlingKind.Hint,
                RejectReason: AgentConditionRejectReason.GenerallyUnwanted,
                RejectedByUserId: rejectedBy),
            AuditEvent(condition.Id, AgentConditionStatus.Rejected, "rejected"));

        moved.ShouldBeTrue();

        await using var verify = NewContext();
        var stored = await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == condition.Id);
        stored.Status.ShouldBe(AgentConditionStatus.Rejected);
        stored.HandledAtUtc.ShouldNotBeNull();
        stored.HandlingKind.ShouldBe(AgentConditionHandlingKind.Hint);
        stored.RejectReason.ShouldBe(AgentConditionRejectReason.GenerallyUnwanted);
        stored.RejectedByUserId.ShouldBe(rejectedBy);
        stored.ResolvedAtUtc.ShouldBeNull();
        stored.EscalatedAtUtc.ShouldBeNull();
        stored.ScenarioId.ShouldBeNull();
    }

    [Test]
    public async Task SecondOpenRowForTheSameFingerprint_IsRefusedByTheIndex_AndInsertReportsItAsNull()
    {
        var fingerprint = NewFingerprint();
        await GivenConditionAsync(fingerprint, AgentConditionStatus.Detected);

        await using var context = NewContext();
        var duplicate = NewCondition(fingerprint, AgentConditionStatus.Detected);
        var inserted = await new AgentConditionRepository(context)
            .InsertAsync(duplicate, DetectionEvent(duplicate.Id));

        inserted.ShouldBeNull();

        await using var verify = NewContext();
        (await verify.AgentConditions.AsNoTracking().CountAsync(c => c.Fingerprint == fingerprint))
            .ShouldBe(1);
        (await verify.AgentConditionEvents.AsNoTracking().CountAsync(e => e.ConditionId == duplicate.Id))
            .ShouldBe(0);
    }

    [Test]
    public async Task AfterResolved_TheSameFingerprintCanOpenAFreshRow_AndTheResolvedOneSurvives()
    {
        var fingerprint = NewFingerprint();
        var first = await GivenConditionAsync(fingerprint, AgentConditionStatus.Detected);

        await using var context = NewContext();
        var repository = new AgentConditionRepository(context);

        (await repository.TryTransitionAsync(
            first.Id,
            AgentConditionStatus.Detected,
            AgentConditionStatus.Resolved,
            new AgentConditionTransitionFields(ResolvedAtUtc: DateTime.UtcNow),
            AuditEvent(first.Id, AgentConditionStatus.Resolved, "gone"))).ShouldBeTrue();

        var reArmed = NewCondition(fingerprint, AgentConditionStatus.Detected);
        var inserted = await repository.InsertAsync(reArmed, DetectionEvent(reArmed.Id));

        inserted.ShouldNotBeNull();

        await using var verify = NewContext();
        var rows = await verify.AgentConditions.AsNoTracking()
            .Where(c => c.Fingerprint == fingerprint)
            .ToListAsync();
        rows.Count.ShouldBe(2);
        rows.Count(c => c.Status == AgentConditionStatus.Resolved).ShouldBe(1);
        rows.Count(c => c.Status == AgentConditionStatus.Detected).ShouldBe(1);

        var open = await repository.FindOpenByFingerprintAsync(fingerprint);
        open.ShouldNotBeNull();
        open.Id.ShouldBe(reArmed.Id);
    }

    [Test]
    public async Task TouchLastSeen_MovesForwardOnOpenRowsOnly_AndNeverBackwards()
    {
        var condition = await GivenConditionAsync(NewFingerprint(), AgentConditionStatus.Detected);
        var closed = await GivenConditionAsync(NewFingerprint(), AgentConditionStatus.Resolved);

        await using var context = NewContext();
        var repository = new AgentConditionRepository(context);
        var laterUtc = condition.LastSeenAtUtc.AddMinutes(10);

        (await repository.TouchLastSeenAsync(condition.Id, laterUtc)).ShouldBeTrue();
        (await repository.TouchLastSeenAsync(condition.Id, condition.LastSeenAtUtc)).ShouldBeFalse();
        (await repository.TouchLastSeenAsync(closed.Id, laterUtc)).ShouldBeFalse();

        await using var verify = NewContext();
        (await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == condition.Id))
            .LastSeenAtUtc.ShouldBe(laterUtc, TimeSpan.FromMilliseconds(1));
        (await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == closed.Id))
            .LastSeenAtUtc.ShouldBe(closed.LastSeenAtUtc, TimeSpan.FromMilliseconds(1));
    }

    private static string NewFingerprint() => TestPrefix + Guid.NewGuid();

    private static AgentCondition NewCondition(string fingerprint, AgentConditionStatus status)
    {
        var nowUtc = DateTime.UtcNow;

        return new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = TestPrefix + "kind",
            Fingerprint = fingerprint,
            Severity = "low",
            Status = status,
            DetectedAtUtc = nowUtc,
            LastSeenAtUtc = nowUtc,
            PayloadJson = "{}"
        };
    }

    private static AgentConditionEvent DetectionEvent(Guid conditionId) => new()
    {
        Id = Guid.NewGuid(),
        ConditionId = conditionId,
        EventType = AgentConditionStatus.Detected.ToString(),
        AtUtc = DateTime.UtcNow
    };

    private static AgentConditionEvent AuditEvent(Guid conditionId, AgentConditionStatus toStatus, string detail) => new()
    {
        Id = Guid.NewGuid(),
        ConditionId = conditionId,
        EventType = toStatus.ToString(),
        AtUtc = DateTime.UtcNow,
        Detail = TestPrefix + detail
    };

    private async Task<AgentCondition> GivenConditionAsync(string fingerprint, AgentConditionStatus status)
    {
        var condition = NewCondition(fingerprint, status);

        await using var context = NewContext();
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        return condition;
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
        // Both filters are this fixture's own marker: every row it creates carries the prefix in
        // trigger_kind, and its events in detail. Neither pattern can reach dev-app data.
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_condition_events WHERE condition_id IN (SELECT id FROM agent_conditions WHERE trigger_kind LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE trigger_kind LIKE {0}",
            TestPrefix + "%");
    }
}
