// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Az3 of the Klacksy-Autonomie test spec (docs/knowledge/klacksy-autonomie-testspezifikation-2026-08-28.md
/// §4, "Budget"): 6 empty_container-shaped findings, a daily action budget of 5. Reuses the Az1 fixture
/// shape (synthetic kind, SingleKindRegistry wrapping the real EmptyContainerRemediationBinder, capturing
/// executor) from EmptyContainerActionScenarioTests.cs, but with its own TestPrefix and a SettableTimeProvider
/// so the clock can be moved across a day boundary deterministically.
///
/// AgentConditionActionDefaults.MaxExecutionsPerKindPerTick is ALSO 5, hardcoded, and is checked BEFORE the
/// daily budget inside AgentConditionActionService.RunAsync's loop - so with 6 candidates in one tick, the
/// observable Day-1 outcome (5 Executed, 1 left Reported with a budget-stop report) is produced by the TICK
/// CAP, not by ActionBudget.DescribeBlockAsync, since both gates coincide at 5 in this codebase and the tick
/// cap is checked first. This is not a shortcoming of the test: it is what production actually does with 6
/// same-tick candidates. What Az3 specifically proves - and what genuinely exercises
/// AgentConditionRepository.CountActionClaimsAsync's date-scoped query rather than the tick cap - is the
/// ROLLOVER on Day 2: the clock moves +24h, CountActionClaimsAsync(..., sinceUtc: Day2 00:00 UTC, ...) no
/// longer sees Day 1's five klacksy-claim: events (their AtUtc is < Day2 00:00), so the 6th condition, still
/// Reported from Day 1, is claimed and executed on the very next tick.
///
/// Governance's WindowActionLimit is set generously above 5 so the 60-minute sliding window cannot also
/// explain the Day-1 block, keeping the tick-cap the one and only reason - DailyActionBudget stays at the
/// spec's literal 5 and is what the Day-2 assertions actually turn on.
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
public class EmptyContainerActionBudgetScenarioTests
{
    private const string TestPrefix = "INTEGRATION_TEST_AZ3_";
    private const string Kind = TestPrefix + "empty_container_like";
    private const int SeededContainerCount = 6;
    private const int DailyActionBudget = 5;

    private static readonly Guid OwnerUserId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTime Day1NowUtc = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Az3_SixContainersAgainstADailyBudgetOfFive_TheSixthExecutesOnlyAfterTheDayRolls()
    {
        var seededIds = await GivenSixEmptyContainerConditionsAsync();

        var executor = new CapturingSkillExecutor();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var timeProvider = new SettableTimeProvider(Day1NowUtc);

        await using (var day1Context = NewContext())
        {
            var day1Result = await NewService(day1Context, executor, reporter, timeProvider)
                .RunAsync(CancellationToken.None);
            day1Result.Executed.ShouldBe(5);
        }

        executor.Invocations.Count.ShouldBe(5);

        await using (var afterDay1 = NewContext())
        {
            var stored = await afterDay1.AgentConditions
                .Where(c => seededIds.Contains(c.Id))
                .ToListAsync();
            stored.Count(c => c.Status == AgentConditionStatus.Executed).ShouldBe(5);
            stored.Count(c => c.Status == AgentConditionStatus.Reported).ShouldBe(
                1, "The 6th condition must stay Reported, not fail or vanish, while the budget is used up.");
        }

        // 5 success reports (one per Executed condition, AgentConditionActionService.cs:645) plus 1
        // budget-stop report (ReportBudgetStopAsync) for the 6th - both address the same owner, since
        // GroupId is null for every seeded condition here.
        await reporter.Received(6).ReportAsync(OwnerUserId, Arg.Any<string>(), Arg.Any<CancellationToken>());

        timeProvider.Now = Day1NowUtc.AddHours(24);

        await using (var day2Context = NewContext())
        {
            var day2Result = await NewService(day2Context, executor, reporter, timeProvider)
                .RunAsync(CancellationToken.None);
            day2Result.Executed.ShouldBe(
                1, "The day rolled: CountActionClaimsAsync no longer counts yesterday's 5 claims, so the "
                + "one remaining condition is claimable again.");
        }

        executor.Invocations.Count.ShouldBe(6, "All 6 seeded conditions have now executed exactly once each.");

        await using var verify = NewContext();
        var final = await verify.AgentConditions.Where(c => seededIds.Contains(c.Id)).ToListAsync();
        final.Count.ShouldBe(6);
        final.ShouldAllBe(c => c.Status == AgentConditionStatus.Executed);
    }

    private static async Task<HashSet<Guid>> GivenSixEmptyContainerConditionsAsync()
    {
        await using var context = NewContext();
        var conditions = new List<AgentCondition>();

        for (var i = 0; i < SeededContainerCount; i++)
        {
            var shiftId = Guid.NewGuid();
            var triggerEvent = new EmptyContainerTriggerEvent(
                shiftId,
                TestPrefix + "container",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                [],
                new ContainerScheduleSnapshot(
                    new TimeOnly(8, 0), new TimeOnly(16, 0), [1], IsHoliday: false, IsWeekdayAndHoliday: false),
                IsPeriodActive: true);

            conditions.Add(new AgentCondition
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
            });
        }

        context.AgentConditions.AddRange(conditions);
        await context.SaveChangesAsync();

        return conditions.Select(c => c.Id).ToHashSet();
    }

    private static readonly DateTime FarPastUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
                DailyActionBudget: DailyActionBudget,
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

    private sealed class CapturingSkillExecutor : ISkillExecutor
    {
        private readonly List<SkillInvocation> _invocations = new();

        public IReadOnlyList<SkillInvocation> Invocations => _invocations;

        public Task<SkillResult> ExecuteAsync(
            SkillInvocation invocation, SkillExecutionContext context, CancellationToken cancellationToken = default)
        {
            lock (_invocations)
            {
                _invocations.Add(invocation);
            }

            return Task.FromResult(SkillResult.SuccessResult(null, "done"));
        }

        public Task<IReadOnlyList<SkillResult>> ExecuteChainAsync(
            IReadOnlyList<SkillInvocation> invocations, SkillExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
