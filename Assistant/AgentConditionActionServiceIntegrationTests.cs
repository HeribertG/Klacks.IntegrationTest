// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end proof of the Etappe 5b action dispatcher against the real PostgreSQL database, on a
/// SYNTHETIC trigger kind rather than empty_container: the dispatcher's safety properties are generic,
/// and binding a fixture to the one kind that happens to have a remediation today would make it fail for
/// the wrong reason the next time the registry changes.
///
/// Two things only a real database can show, and which the unit suite above this therefore cannot:
///
/// (1) THE DOUBLE TICK. Two dispatchers claiming the same condition at the same moment must produce
///     exactly ONE execution. The fake repository has no transactions and no row locks, so there it is
///     true by construction; here it is true because ExecuteUpdateAsync's conditional UPDATE really is a
///     compare-and-swap and the loser really sees zero affected rows.
///
/// (2) THE BUDGET JOIN. The daily budget and the circuit breaker are counted with a join from
///     agent_condition_events to agent_conditions plus a StartsWith on Detail. The EF InMemory provider
///     accepts LINQ that Npgsql rejects - this project has been caught by that twice - so the query has
///     to be executed against Postgres, not merely written.
///
/// Cleanup deletes ONLY rows this fixture created, by its own trigger-kind prefix. The dev app shares
/// this database, so nothing is ever deleted by a production-plausible value.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class AgentConditionActionServiceIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_ACTION_";
    private const string SyntheticKind = TestPrefix + "demo_kind";
    private const string SkillName = TestPrefix + "demo_skill";
    private const string RequiredArgument = "containerId";

    private static readonly Guid OwnerUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

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
    public async Task TwoDispatchersOnTheSameCondition_ProduceExactlyOneExecution()
    {
        var condition = await GivenReportedConditionAsync();
        var executor = new CountingSkillExecutor();

        await using var firstContext = NewContext();
        await using var secondContext = NewContext();

        var first = NewService(firstContext, executor).RunAsync(CancellationToken.None);
        var second = NewService(secondContext, executor).RunAsync(CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        executor.Calls.ShouldBe(
            1,
            "The claim is a compare-and-swap. If both dispatchers could act, Klacksy would create the "
            + "same container template twice.");
        results.Sum(result => result.Executed).ShouldBe(1);

        // Deliberately NOT asserted: that the loser reports SkippedClaimLost. Whether it loses the swap
        // or no longer sees the row at all depends on how far the winner got before the loser's
        // candidate query ran, and pinning either would make this test flaky about something it is not
        // for. The one execution above is the property that matters.

        await using var verify = NewContext();
        var stored = await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == condition.Id);
        stored.Status.ShouldBe(AgentConditionStatus.Executed);
        stored.AttemptCount.ShouldBe(1, "Only the winner's claim may raise the attempt counter.");
        stored.HandlingKind.ShouldBe(AgentConditionHandlingKind.Executed);
    }

    [Test]
    public async Task OneExecutionLeavesExactlyOneBudgetCountingClaimEvent()
    {
        var condition = await GivenReportedConditionAsync();
        var executor = new CountingSkillExecutor();

        await using var context = NewContext();
        await NewService(context, executor).RunAsync(CancellationToken.None);

        await using var verify = NewContext();
        var claims = await new AgentConditionRepository(verify)
            .CountActionClaimsAsync(SyntheticKind, groupId: null, DateTime.UtcNow.Date);

        claims.ShouldBe(
            1,
            "The budget is counted from the claim's own audit event, written inside the claim's "
            + "transaction - which is what makes a false-negative compare-and-swap harmless.");

        var events = await verify.AgentConditionEvents.AsNoTracking()
            .Where(e => e.ConditionId == condition.Id)
            .OrderBy(e => e.AtUtc)
            .ToListAsync();

        events.Count(e => e.EventType == AgentConditionStatus.Prepared.ToString()).ShouldBe(1);
        events.Count(e => e.EventType == AgentConditionStatus.Executed.ToString()).ShouldBe(1);
    }

    /// <summary>
    /// Exercises the budget join itself against Postgres: the events-to-conditions join, the time bound
    /// and above all the StartsWith on Detail, which is what separates a budget-consuming claim from
    /// every other audit event on the same rows.
    /// </summary>
    [Test]
    public async Task TheBudgetQuery_CountsOnlyClaimMarkedEventsOfItsOwnKindInsideTheWindow()
    {
        var nowUtc = DateTime.UtcNow;
        var mine = await GivenReportedConditionAsync();
        var otherKind = await GivenReportedConditionAsync(triggerKind: TestPrefix + "other_kind");

        await GivenEventAsync(mine.Id, AgentConditionActionDefaults.ActionClaimDetailPrefix + "recent", nowUtc.AddMinutes(-5));
        await GivenEventAsync(mine.Id, AgentConditionActionDefaults.ActionClaimDetailPrefix + "old", nowUtc.AddHours(-20));
        await GivenEventAsync(mine.Id, AgentConditionActionDefaults.ActionOutcomeDetailPrefix + "executed", nowUtc.AddMinutes(-4));
        await GivenEventAsync(mine.Id, null, nowUtc.AddMinutes(-3));
        await GivenEventAsync(otherKind.Id, AgentConditionActionDefaults.ActionClaimDetailPrefix + "foreign", nowUtc.AddMinutes(-2));

        await using var context = NewContext();
        var repository = new AgentConditionRepository(context);

        (await repository.CountActionClaimsAsync(SyntheticKind, groupId: null, nowUtc.AddMinutes(-60))).ShouldBe(
            1,
            "Inside the window: the recent claim only - not the outcome event, not the event without a "
            + "detail, and not the other kind's claim.");

        (await repository.CountActionClaimsAsync(SyntheticKind, groupId: null, nowUtc.AddHours(-24))).ShouldBe(
            2, "Widening the window to a day must pick up the older claim as well.");
    }

    /// <summary>
    /// The GROUP dimension of the budget query, and the one property no in-memory provider can speak to:
    /// the null branch has to reach the rows that carry no group at all. A single GroupId == groupId
    /// comparison with a null argument is the classic SQL trap - "= NULL" matches nothing - which would
    /// leave every installation-wide kind believing its budget untouched forever. Whether Npgsql would in
    /// fact translate that form to IS NULL is not asserted here; what is asserted is the OBSERVABLE
    /// number the dispatcher's budget gate depends on.
    /// </summary>
    [Test]
    public async Task TheBudgetQuery_CountsPerGroup_AndCountsTheGroupLessRowsForANullGroup()
    {
        var nowUtc = DateTime.UtcNow;
        var busyGroupId = Guid.NewGuid();
        var quietGroupId = Guid.NewGuid();

        var busy = await GivenReportedConditionAsync(groupId: busyGroupId);
        var quiet = await GivenReportedConditionAsync(groupId: quietGroupId);
        var installationWide = await GivenReportedConditionAsync();

        await GivenEventAsync(busy.Id, AgentConditionActionDefaults.ActionClaimDetailPrefix + "busy-one", nowUtc.AddMinutes(-5));
        await GivenEventAsync(busy.Id, AgentConditionActionDefaults.ActionClaimDetailPrefix + "busy-two", nowUtc.AddMinutes(-4));
        await GivenEventAsync(quiet.Id, AgentConditionActionDefaults.ActionClaimDetailPrefix + "quiet-one", nowUtc.AddMinutes(-3));
        await GivenEventAsync(installationWide.Id, AgentConditionActionDefaults.ActionClaimDetailPrefix + "global-one", nowUtc.AddMinutes(-2));

        await using var context = NewContext();
        var repository = new AgentConditionRepository(context);
        var sinceUtc = nowUtc.AddMinutes(-60);

        (await repository.CountActionClaimsAsync(SyntheticKind, busyGroupId, sinceUtc)).ShouldBe(
            2, "The busy group's own two claims - and nothing of the other two scopes.");

        (await repository.CountActionClaimsAsync(SyntheticKind, quietGroupId, sinceUtc)).ShouldBe(
            1,
            "A quiet group is charged only for what it spent itself. Three would mean the count is "
            + "pooled across groups, which is what let a busy group exhaust a quiet one's budget.");

        (await repository.CountActionClaimsAsync(SyntheticKind, groupId: null, sinceUtc)).ShouldBe(
            1,
            "Null is the installation-wide bucket, not 'any group'. Zero would be the SQL '= NULL' "
            + "failure; four would mean no group filter reached the query at all.");
    }

    [Test]
    public async Task AnAbandonedClaim_IsResumedOnceItHasGoneStale_AndAFreshOneIsLeftAlone()
    {
        var nowUtc = DateTime.UtcNow;
        var stale = await GivenConditionAsync(
            AgentConditionStatus.Prepared,
            lastAttemptAtUtc: nowUtc.AddMinutes(-AgentConditionActionDefaults.StaleClaimMinutes - 5));
        var fresh = await GivenConditionAsync(
            AgentConditionStatus.Prepared,
            triggerKind: TestPrefix + "fresh_kind",
            lastAttemptAtUtc: nowUtc.AddMinutes(-1));

        var executor = new CountingSkillExecutor();
        await using var context = NewContext();
        await NewService(context, executor, TestPrefix + "fresh_kind").RunAsync(CancellationToken.None);
        await NewService(context, executor).RunAsync(CancellationToken.None);

        await using var verify = NewContext();
        var storedStale = await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == stale.Id);
        var storedFresh = await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == fresh.Id);

        storedStale.Status.ShouldBe(AgentConditionStatus.Executed);
        storedStale.AttemptCount.ShouldBe(1, "Resuming an abandoned claim is itself an attempt.");
        storedFresh.Status.ShouldBe(
            AgentConditionStatus.Prepared,
            "A claim that is still inside the stale window belongs to whoever holds it.");
    }

    private static AgentConditionActionService NewService(
        DataBaseContext context, CountingSkillExecutor executor, string triggerKind = SyntheticKind)
    {
        var repository = new AgentConditionRepository(context);
        var ledger = new AgentConditionLedgerService(
            repository, TimeProvider.System, NullLogger<AgentConditionLedgerService>.Instance);

        var governance = Substitute.For<IProactiveGovernanceResolver>();
        governance
            .ResolveAsync(triggerKind, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDecision(
                TriggerKind: triggerKind,
                GroupId: null,
                EffectiveMaxAction: ProactiveMaxAction.Execute,
                ConfiguredMaxAction: ProactiveMaxAction.Execute,
                Enabled: true,
                KillSwitchActive: false,
                ResponsibleOwnerUserId: OwnerUserId,
                DailyActionBudget: 50,
                WindowActionLimit: 50,
                WindowMinutes: 60,
                IsStored: true));

        var quietWindow = Substitute.For<IQuietWindowService>();
        quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        var identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Resolved(
                new SkillExecutionContext
                {
                    UserId = OwnerUserId,
                    TenantId = Guid.Empty,
                    UserName = KlacksyIdentity.SystemUserName,
                    UserPermissions = ["some.permission"],
                    BypassAutonomyGate = true
                },
                ["some.permission"]));

        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        return new AgentConditionActionService(
            repository,
            ledger,
            governance,
            new SingleKindRegistry(triggerKind),
            quietWindow,
            identityProvider,
            executor,
            reporter,
            TimeProvider.System,
            NullLogger<AgentConditionActionService>.Instance);
    }

    private static async Task<AgentCondition> GivenReportedConditionAsync(
        string triggerKind = SyntheticKind, Guid? groupId = null) =>
        await GivenConditionAsync(AgentConditionStatus.Reported, triggerKind, groupId: groupId);

    private static async Task<AgentCondition> GivenConditionAsync(
        AgentConditionStatus status,
        string triggerKind = SyntheticKind,
        DateTime? lastAttemptAtUtc = null,
        Guid? groupId = null)
    {
        var nowUtc = DateTime.UtcNow;
        var condition = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = triggerKind,
            Fingerprint = TestPrefix + Guid.NewGuid(),
            EntityId = Guid.NewGuid(),
            GroupId = groupId,
            Severity = AgentTriggerSeverity.Medium,
            Status = status,
            DetectedAtUtc = nowUtc.AddHours(-2),
            LastSeenAtUtc = nowUtc,
            LastAttemptAtUtc = lastAttemptAtUtc,
            PayloadJson = "{}"
        };

        await using var context = NewContext();
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        return condition;
    }

    private static async Task GivenEventAsync(Guid conditionId, string? detail, DateTime atUtc)
    {
        await using var context = NewContext();
        context.AgentConditionEvents.Add(new AgentConditionEvent
        {
            Id = Guid.NewGuid(),
            ConditionId = conditionId,
            EventType = TestPrefix + "seeded",
            AtUtc = atUtc,
            Detail = detail
        });

        await context.SaveChangesAsync();
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
            "DELETE FROM agent_condition_events WHERE condition_id IN (SELECT id FROM agent_conditions WHERE trigger_kind LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE trigger_kind LIKE {0}",
            TestPrefix + "%");
    }

    private sealed class SingleKindRegistry : IConditionRemediationRegistry
    {
        private readonly string _triggerKind;

        public SingleKindRegistry(string triggerKind)
        {
            _triggerKind = triggerKind;
        }

        public IReadOnlyCollection<string> RegisteredKinds => [_triggerKind];

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = triggerKind == _triggerKind
                ? new ConditionRemediationEntry(SkillName, new ConstantBinder(), [RequiredArgument])
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == _triggerKind ? configuredMaxAction : ProactiveMaxAction.Hint;

        private sealed class ConstantBinder : IConditionRemediationParameterBinder
        {
            public IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> conditionPayload) =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [RequiredArgument] = Guid.NewGuid().ToString()
                };
        }
    }

    /// <summary>
    /// Counts executions across BOTH dispatchers of the double-tick test, which is the assertion that
    /// test exists for; a substituted executor per instance could not see the other's calls.
    /// </summary>
    private sealed class CountingSkillExecutor : ISkillExecutor
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<SkillResult> ExecuteAsync(
            SkillInvocation invocation,
            SkillExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(SkillResult.SuccessResult(null, "done"));
        }

        public Task<IReadOnlyList<SkillResult>> ExecuteChainAsync(
            IReadOnlyList<SkillInvocation> invocations,
            SkillExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
