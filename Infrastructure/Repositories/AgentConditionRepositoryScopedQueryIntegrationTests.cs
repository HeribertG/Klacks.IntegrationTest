// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for AgentConditionRepository's restricted-scope branch, shared by
/// GetTopForContextAsync (Etappe 3g) and GetOpenForScopeAsync/CountOpenForScopeAsync (Etappe 3f), against
/// the real PostgreSQL database. Unit tests only exercise this query against the EF InMemory provider,
/// which accepts LINQ shapes Npgsql cannot always translate - in particular the GroupId-to-Group LEFT JOIN
/// via join/DefaultIfEmpty, the "g.Root ?? g.Id" root fallback, and visibleRootIds.Contains(...) where
/// visibleRootIds is statically typed IReadOnlySet&lt;Guid&gt; (its own instance Contains(T) method, not
/// Enumerable.Contains - a different expression-tree shape EF's parameterized-collection translator may or
/// may not recognize the same way). Nothing else in the codebase runs that exact shape:
/// PlanningAudienceResolver resolves Root ?? Id in a separate C# step after a plain single-row fetch,
/// never inside a LINQ join translated to SQL, so this is a genuinely new translation path that only a
/// real database can prove. Cleanup deletes ONLY rows this fixture created - the dev app shares this
/// database, which already carries thousands of AgentCondition rows from earlier live-verification
/// sessions; fixture rows are dated far in the past so they always sort first in both
/// GetTopForContextAsync's and GetOpenForScopeAsync's oldest-first tiebreak regardless of that volume,
/// instead of relying on a Take large enough to outrun it.
/// </summary>

using System.Data;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
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
public class AgentConditionRepositoryScopedQueryIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_CTXQ_";
    private static readonly DateTime FarPastUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
    public async Task RestrictedScope_LeftJoinAndRootFallback_TranslateAgainstRealPostgres()
    {
        // Root-level visible group (its own Id is the "root" - Root is null, matching the g.Root ?? g.Id
        // fallback), a child underneath it (Root set, subtree membership only provable via the fallback),
        // and an unrelated foreign root the caller cannot see.
        var visibleRoot = await GivenGroupAsync(root: null);
        var visibleChild = await GivenGroupAsync(root: visibleRoot.Id);
        var foreignRoot = await GivenGroupAsync(root: null);

        var onVisibleRoot = await GivenConditionAsync("high", visibleRoot.Id);
        var onVisibleChild = await GivenConditionAsync("high", visibleChild.Id);
        var onForeignRoot = await GivenConditionAsync("high", foreignRoot.Id);
        var ungated = await GivenConditionAsync("high", groupId: null);

        await using var context = NewContext();
        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: false,
            visibleRootIds: new HashSet<Guid> { visibleRoot.Id },
            preferredGroupId: null,
            take: 50);

        var resultIds = result.Select(c => c.Id).ToHashSet();
        resultIds.ShouldContain(onVisibleRoot.Id);
        resultIds.ShouldContain(onVisibleChild.Id);
        resultIds.ShouldContain(ungated.Id);
        resultIds.ShouldNotContain(onForeignRoot.Id);
    }

    [Test]
    public async Task UnrestrictedScope_SkipsTheJoinEntirely_AndStillTranslates()
    {
        // "high", not "medium": GetTopForContextAsync ranks severity ahead of the age tiebreak, so a
        // "medium" row can never outrank the dev DB's real "high" rows regardless of how old it is dated.
        var group = await GivenGroupAsync(root: null);
        var condition = await GivenConditionAsync("high", group.Id);

        await using var context = NewContext();
        var result = await new AgentConditionRepository(context).GetTopForContextAsync(
            isUnrestricted: true,
            visibleRootIds: new HashSet<Guid>(),
            preferredGroupId: null,
            take: 50);

        result.Select(c => c.Id).ShouldContain(condition.Id);
    }

    [Test]
    public async Task GetOpenForScope_RestrictedScope_LeftJoinAndRootFallback_TranslateAgainstRealPostgres()
    {
        // Same shape as RestrictedScope_LeftJoinAndRootFallback_TranslateAgainstRealPostgres above, but
        // through GetOpenForScopeAsync/CountOpenForScopeAsync (Etappe 3f's list_open_findings skill) -
        // the two entry points ScopedPlannerRelevantQuery actually has to serve. If either method's own
        // OrderBy/Take on top of the shared query fragment somehow broke translation, only running it
        // through GetTopForContextAsync would miss that.
        var visibleRoot = await GivenGroupAsync(root: null);
        var visibleChild = await GivenGroupAsync(root: visibleRoot.Id);
        var foreignRoot = await GivenGroupAsync(root: null);

        var onVisibleRoot = await GivenConditionAsync("high", visibleRoot.Id);
        var onVisibleChild = await GivenConditionAsync("high", visibleChild.Id);
        var onForeignRoot = await GivenConditionAsync("high", foreignRoot.Id);
        var ungated = await GivenConditionAsync("high", groupId: null);

        var scope = new HashSet<Guid> { visibleRoot.Id };

        await using var context = NewContext();
        var repository = new AgentConditionRepository(context);
        var result = await repository.GetOpenForScopeAsync(isUnrestricted: false, visibleRootIds: scope, take: 50);
        var count = await repository.CountOpenForScopeAsync(isUnrestricted: false, visibleRootIds: scope);

        var resultIds = result.Select(c => c.Id).ToHashSet();
        resultIds.ShouldContain(onVisibleRoot.Id);
        resultIds.ShouldContain(onVisibleChild.Id);
        resultIds.ShouldContain(ungated.Id);
        resultIds.ShouldNotContain(onForeignRoot.Id);

        // count also covers the dev DB's own real ungated rows (GroupId == null), so it cannot be pinned
        // to an exact number - only proven to be at least large enough to cover this fixture's 3 in-scope
        // rows, and to translate/execute against Postgres at all without throwing.
        count.ShouldBeGreaterThanOrEqualTo(3);
    }

    /// <summary>
    /// The RequiresGroupScope withholding against real Postgres. Two translation risks the InMemory
    /// provider cannot expose: the negated array membership on trigger_kind
    /// (NOT (trigger_kind = ANY(@p))) now sitting inside the same WHERE as the LEFT JOIN's nullable
    /// root fallback, and its interaction with SQL three-valued logic on the join's null side, where a
    /// client-evaluated short circuit would give a different answer than the database does.
    /// </summary>
    [Test]
    public async Task RestrictedScope_WithholdsAGroupScopedKindWhoseGroupIsUnknown_AgainstRealPostgres()
    {
        var visibleRoot = await GivenGroupAsync(root: null);

        var groupScopedUngrouped = await GivenConditionAsync("high", groupId: null, triggerKind: AgentTriggerKinds.EmptyContainer);
        var alsoGroupScopedUngrouped = await GivenConditionAsync("high", groupId: null, triggerKind: AgentTriggerKinds.UncutFulldayShift);
        var globalUngrouped = await GivenConditionAsync("high", groupId: null, triggerKind: AgentTriggerKinds.TargetHoursDrift);
        var groupScopedWithGroup = await GivenConditionAsync("high", groupId: visibleRoot.Id, triggerKind: AgentTriggerKinds.EmptyContainer);

        var scope = new HashSet<Guid> { visibleRoot.Id };

        await using var context = NewContext();
        var repository = new AgentConditionRepository(context);
        var planner = await repository.GetOpenForScopeAsync(isUnrestricted: false, visibleRootIds: scope, take: 500);
        var plannerWithoutScope = await repository.GetOpenForScopeAsync(
            isUnrestricted: false, visibleRootIds: new HashSet<Guid>(), take: 500);
        var admin = await repository.GetOpenForScopeAsync(isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: 500);

        var plannerIds = planner.Select(c => c.Id).ToHashSet();
        plannerIds.ShouldNotContain(groupScopedUngrouped.Id);
        plannerIds.ShouldNotContain(alsoGroupScopedUngrouped.Id);
        plannerIds.ShouldContain(globalUngrouped.Id);
        plannerIds.ShouldContain(groupScopedWithGroup.Id);

        var unscopedIds = plannerWithoutScope.Select(c => c.Id).ToHashSet();
        unscopedIds.ShouldNotContain(groupScopedUngrouped.Id);
        unscopedIds.ShouldContain(globalUngrouped.Id);

        var adminIds = admin.Select(c => c.Id).ToHashSet();
        adminIds.ShouldContain(groupScopedUngrouped.Id);
        adminIds.ShouldContain(alsoGroupScopedUngrouped.Id);
    }

    /// <summary>
    /// Runs the new predicate over the dev database's WHOLE planner-relevant ledger, not just planted rows:
    /// whatever the dev app has accumulated (at the time of writing 50 empty_container plus 2
    /// uncut_fullday_shift rows with a null group_id, alongside ~2800 target_hours_drift ones) is classified
    /// by the same query, and every row the planner does not get is checked to be one the rule is allowed to
    /// withhold. One fixture row of each side is seeded so neither direction can pass vacuously on a day the
    /// live backlog happens to be empty.
    /// </summary>
    [Test]
    public async Task RestrictedScope_AcrossTheWholeLiveLedger_WithholdsOnlyRowsTheRuleAllows()
    {
        var seededGroupScoped = await GivenConditionAsync("high", groupId: null, triggerKind: AgentTriggerKinds.EmptyContainer);
        var seededGlobal = await GivenConditionAsync("high", groupId: null, triggerKind: AgentTriggerKinds.TargetHoursDrift);

        await using var context = NewContext();
        var repository = new AgentConditionRepository(context);

        // One RepeatableRead snapshot across both reads. The dev app shares this database and its
        // detector tick keeps moving rows to terminal statuses; a row resolving between the two queries
        // would sit in adminRows but not in plannerIds and fail the withholding assertion for a reason
        // that is not a bug. Safe to open here because this fixture's context has no retrying execution
        // strategy (see NewContext) - inside the API's own EnableRetryOnFailure context it would not be.
        await using var snapshot = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);

        var adminRows = await repository.GetOpenForScopeAsync(
            isUnrestricted: true, visibleRootIds: new HashSet<Guid>(), take: int.MaxValue);
        var plannerRows = await repository.GetOpenForScopeAsync(
            isUnrestricted: false, visibleRootIds: new HashSet<Guid>(), take: int.MaxValue);

        var plannerIds = plannerRows.Select(c => c.Id).ToHashSet();

        adminRows
            .Where(condition => !plannerIds.Contains(condition.Id))
            .ShouldAllBe(condition => condition.GroupId != null
                || AgentTriggerGroupScopedKinds.Values.Contains(condition.TriggerKind));

        var ungroupedGroupScoped = adminRows
            .Where(condition => condition.GroupId == null
                && AgentTriggerGroupScopedKinds.Values.Contains(condition.TriggerKind))
            .ToList();

        ungroupedGroupScoped.Select(condition => condition.Id).ShouldContain(seededGroupScoped.Id);
        ungroupedGroupScoped.ShouldAllBe(condition => !plannerIds.Contains(condition.Id));

        var ungroupedGlobal = adminRows
            .Where(condition => condition.GroupId == null
                && !AgentTriggerGroupScopedKinds.Values.Contains(condition.TriggerKind))
            .ToList();

        ungroupedGlobal.Select(condition => condition.Id).ShouldContain(seededGlobal.Id);
        ungroupedGlobal.ShouldAllBe(condition => plannerIds.Contains(condition.Id));
    }

    private static async Task<Group> GivenGroupAsync(Guid? root)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "group",
            Root = root,
            ValidFrom = DateTime.UtcNow,
        };

        await using var context = NewContext();
        context.Group.Add(group);
        await context.SaveChangesAsync();

        return group;
    }

    /// <param name="triggerKind">Defaults to this fixture's own synthetic kind. The RequiresGroupScope
    /// tests must plant REAL kind strings instead, because that is what the query classifies on - which is
    /// why cleanup keys on the Fingerprint prefix as well, the only marker such a row still carries.</param>
    private static async Task<AgentCondition> GivenConditionAsync(string severity, Guid? groupId, string? triggerKind = null)
    {
        // Dated far in the past (not DateTime.UtcNow) so this row always sorts first under
        // GetTopForContextAsync's oldest-first tiebreak, no matter how many real, newer rows already sit
        // in the shared dev DB - see the fixture-level remarks.
        var condition = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = triggerKind ?? TestPrefix + "kind",
            Fingerprint = TestPrefix + Guid.NewGuid(),
            Severity = severity,
            Status = AgentConditionStatus.Detected,
            GroupId = groupId,
            DetectedAtUtc = FarPastUtc,
            LastSeenAtUtc = FarPastUtc,
            PayloadJson = "{}",
        };

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
        // Every filter is this fixture's own marker: conditions carry the prefix in fingerprint (and, for
        // the rows that keep the synthetic kind, in trigger_kind too), groups in name. Fingerprint is the
        // load-bearing one since the RequiresGroupScope tests plant real trigger_kind values - matching on
        // trigger_kind alone would leave those rows behind in the shared dev DB, and widening the match to
        // the kind itself would delete the dev app's own 52 live rows. payload_json is matched as well:
        // scenario fixtures that keep a REAL kind (e.g. target_hours_drift) carry the prefix only in their
        // payload, and the dev app's own detectors once created 26 target_hours_drift conditions whose
        // payloads referenced INTEGRATION_TEST_ client names planted by Az0 (deleted manually 2026-08-30) -
        // hence the contains-match on payload_json with the generic marker, not this fixture's prefix.
        // No pattern can reach dev-app data, because real clients never carry the marker in their names.
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_condition_events WHERE condition_id IN "
            + "(SELECT id FROM agent_conditions WHERE trigger_kind LIKE {0} OR fingerprint LIKE {0} OR payload_json LIKE {1})",
            TestPrefix + "%", "%INTEGRATION_TEST_%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE trigger_kind LIKE {0} OR fingerprint LIKE {0} OR payload_json LIKE {1}",
            TestPrefix + "%", "%INTEGRATION_TEST_%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE name LIKE {0}",
            TestPrefix + "%");
    }
}
