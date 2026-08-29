// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Az6 and (partially) Az8 of the Klacksy-Autonomie test spec
/// (docs/knowledge/klacksy-autonomie-testspezifikation-2026-08-28.md §4, "Eskalation" / "Fehlerinjektion"):
/// a remediation that fails three times in a row must escalate to a human on the 4th tick rather than being
/// retried forever, and the 4th tick must make no further attempt at all. Az8's own test method below adds
/// the one code path Az6's failing-but-returning executor cannot reach - see that test's own doc comment
/// for what Az8 half this covers and what it deliberately does not.
///
/// Each retry after a failure needs the row reclaimed from Prepared rather than claimed fresh from
/// Reported (RecordFailureAsync deliberately leaves a failed row on Prepared, see its own doc comment), and
/// TryClaimAsync only reclaims a Prepared row once it is stale (AgentConditionActionDefaults.
/// StaleClaimMinutes = 30, TryReclaimStaleAsync). A SettableTimeProvider therefore advances 31 minutes
/// between every tick - reusing Az5's realization that TimeProvider.System makes every tick land within
/// milliseconds of the last, which is far too fast for either the cascade guard or, here, the stale-claim
/// window.
///
/// AgentConditionActionDefaults.MaxAttemptsBeforeEscalation = 3, and the loop in AgentConditionActionService.
/// RunAsync checks AttemptCount &gt;= MaxAttemptsBeforeEscalation BEFORE attempting to (re)claim - so after
/// three failed attempts (AttemptCount = 3), the 4th tick escalates without ever calling TryClaimAsync or
/// the executor again. That ordering is exactly why "kein Versuch" on the 4th tick is a distinct,
/// independently checkable claim from "escalates" - a wrong ordering could escalate AND still attempt once
/// more.
///
/// Reuses the Az1 fixture shape (synthetic kind, SingleKindRegistry wrapping the real
/// EmptyContainerRemediationBinder) with its own TestPrefix, but a FAILING executor instead of a capturing
/// one.
///
/// Cleanup deletes ONLY rows this fixture created, by its own fingerprint prefix.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.Proactive;

[TestFixture]
[Category("RealDatabase")]
public class EmptyContainerEscalationScenarioTests
{
    private const string TestPrefix = "INTEGRATION_TEST_AZ6_";
    private const string Kind = TestPrefix + "empty_container_like";
    private const string FailureMessage = "simulated remediation failure";

    private static readonly Guid OwnerUserId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTime FarPastUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Az6_ThreeFailedAttempts_EscalateOnTheFourthTickWithoutAFurtherAttempt()
    {
        var shiftId = Guid.NewGuid();
        var condition = await GivenReportedConditionAsync(shiftId);

        var executor = new FailingSkillExecutor();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var timeProvider = new SettableTimeProvider(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var context = NewContext();
            var result = await NewService(context, executor, reporter, timeProvider).RunAsync(CancellationToken.None);
            result.Executed.ShouldBe(0, $"Attempt {attempt} must fail, not execute.");

            timeProvider.Now = timeProvider.Now.AddMinutes(31);
        }

        executor.CallCount.ShouldBe(3, "Exactly one executor call per failed attempt, no more.");

        await using (var afterThree = NewContext())
        {
            var stored = await afterThree.AgentConditions.SingleAsync(c => c.Id == condition.Id);
            stored.Status.ShouldBe(AgentConditionStatus.Prepared, "Still Prepared, not escalated, until the 4th tick.");
            stored.AttemptCount.ShouldBe(3);
        }

        await using (var fourthContext = NewContext())
        {
            var fourthResult = await NewService(fourthContext, executor, reporter, timeProvider).RunAsync(CancellationToken.None);
            fourthResult.Executed.ShouldBe(0);
        }

        executor.CallCount.ShouldBe(3, "The 4th tick escalates without ever calling the executor again.");

        await using var verify = NewContext();
        var final = await verify.AgentConditions.SingleAsync(c => c.Id == condition.Id);
        final.Status.ShouldBe(AgentConditionStatus.Escalated);
        final.AttemptCount.ShouldBe(3, "AttemptCount is not incremented by the escalation itself.");

        var events = await verify.AgentConditionEvents
            .Where(e => e.ConditionId == condition.Id)
            .AsNoTracking()
            .ToListAsync();
        events.Count(e => e.EventType == AgentConditionEventTypes.AttemptFailed).ShouldBe(3);
        events.Count(e => e.EventType == AgentConditionStatus.Escalated.ToString()).ShouldBe(1);

        // 3 failure reports plus 1 escalation report, all to the same owner (GroupId is null throughout).
        await reporter.Received(4).ReportAsync(OwnerUserId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Az8_ExecutorThrows_RecordsTheFailureThroughTheCatchBranchInsteadOfTheResultBranch()
    {
        // Az8 "Fehlerinjektion": SkillResult.Error(...) (Az6, above) and an executor that THROWS hit two
        // different branches in AgentConditionActionService.ExecuteAsync - the `if (!result.Success)`
        // branch versus a separate `catch (Exception ex)` block a few lines above it. Both call
        // RecordFailureAsync with the same shape, but nothing exercises the catch branch specifically
        // without this test. The "DB error after claim" half of Az8 is deliberately NOT built here: it
        // would need either a connection-killing harness or a repository decorator that throws mid-
        // transaction, both new infrastructure whose only payoff is a path this catch block already
        // handles identically to a thrown skill exception.
        var shiftId = Guid.NewGuid();
        var condition = await GivenReportedConditionAsync(shiftId);

        var executor = new ThrowingSkillExecutor();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var timeProvider = new SettableTimeProvider(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

        await using (var context = NewContext())
        {
            var result = await NewService(context, executor, reporter, timeProvider).RunAsync(CancellationToken.None);
            result.Executed.ShouldBe(0);
        }

        executor.CallCount.ShouldBe(1);

        await using var verify = NewContext();
        var stored = await verify.AgentConditions.SingleAsync(c => c.Id == condition.Id);
        stored.Status.ShouldBe(AgentConditionStatus.Prepared, "No half-written state: a thrown exception leaves the row exactly where a returned failure would.");
        stored.AttemptCount.ShouldBe(1);

        var events = await verify.AgentConditionEvents.Where(e => e.ConditionId == condition.Id).AsNoTracking().ToListAsync();
        events.Count(e => e.EventType == AgentConditionEventTypes.AttemptFailed).ShouldBe(1);

        await reporter.Received(1).ReportAsync(OwnerUserId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task<AgentCondition> GivenReportedConditionAsync(Guid shiftId)
    {
        var triggerEvent = new EmptyContainerTriggerEvent(
            shiftId,
            TestPrefix + "container",
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            [],
            new ContainerScheduleSnapshot(
                new TimeOnly(6, 0), new TimeOnly(14, 0), [3, 5], IsHoliday: false, IsWeekdayAndHoliday: false),
            IsPeriodActive: true);

        var condition = new AgentCondition
        {
            Id = shiftId,
            TriggerKind = Kind,
            Fingerprint = TestPrefix + shiftId,
            EntityId = shiftId,
            GroupId = null,
            Severity = AgentTriggerSeverity.High,
            Status = AgentConditionStatus.Reported,
            DetectedAtUtc = FarPastUtc,
            LastSeenAtUtc = FarPastUtc,
            PayloadJson = JsonSerializer.Serialize(triggerEvent.Payload),
        };

        await using var context = NewContext();
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        return condition;
    }

    private static AgentConditionActionService NewService(
        DataBaseContext context, ISkillExecutor executor, IProactiveActionReporter reporter, TimeProvider timeProvider)
    {
        var repository = new AgentConditionRepository(context);
        var ledger = new AgentConditionLedgerService(repository, timeProvider, NullLogger<AgentConditionLedgerService>.Instance);

        var governance = Substitute.For<IProactiveGovernanceResolver>();
        governance
            .ResolveAsync(Kind, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDecision(
                TriggerKind: Kind,
                GroupId: null,
                EffectiveMaxAction: ProactiveMaxAction.Execute,
                ConfiguredMaxAction: ProactiveMaxAction.Execute,
                Enabled: true,
                KillSwitchActive: false,
                ResponsibleOwnerUserId: OwnerUserId,
                DailyActionBudget: 50,
                WindowActionLimit: 50,
                WindowMinutes: 60,
                IsStored: true,
                GlobalAutonomyCap: ProactiveMaxAction.Execute));

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

        return new AgentConditionActionService(
            repository,
            ledger,
            governance,
            new SingleKindRegistry(Kind),
            quietWindow,
            identityProvider,
            executor,
            reporter,
            timeProvider,
            NullLogger<AgentConditionActionService>.Instance);
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
            "DELETE FROM agent_condition_events WHERE condition_id IN "
            + "(SELECT id FROM agent_conditions WHERE fingerprint LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE fingerprint LIKE {0}",
            TestPrefix + "%");
    }

    /// <summary>
    /// Wraps the REAL EmptyContainerRemediationBinder behind a synthetic trigger kind - see the sibling
    /// fixture EmptyContainerActionScenarioTests.SingleKindRegistry for the incident this pattern fixed.
    /// </summary>
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
                ? new ConditionRemediationEntry(
                    CreateContainerTemplateParameters.SkillName,
                    new EmptyContainerRemediationBinder(),
                    CreateContainerTemplateParameters.Required)
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == _triggerKind ? configuredMaxAction : ProactiveMaxAction.Hint;
    }

    private sealed class FailingSkillExecutor : ISkillExecutor
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<SkillResult> ExecuteAsync(
            SkillInvocation invocation, SkillExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(SkillResult.Error(FailureMessage));
        }

        public Task<IReadOnlyList<SkillResult>> ExecuteChainAsync(
            IReadOnlyList<SkillInvocation> invocations, SkillExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingSkillExecutor : ISkillExecutor
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<SkillResult> ExecuteAsync(
            SkillInvocation invocation, SkillExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException(FailureMessage);
        }

        public Task<IReadOnlyList<SkillResult>> ExecuteChainAsync(
            IReadOnlyList<SkillInvocation> invocations, SkillExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
