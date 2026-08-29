// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Az5 of the Klacksy-Autonomie test spec (docs/knowledge/klacksy-autonomie-testspezifikation-2026-08-28.md
/// §4, "Rezidiv"): a container executes (Az1), then "goes empty again" 3 days later - proving that the
/// SAME real-world container opens a genuinely NEW ledger row rather than reopening or mutating the old
/// one, so dedup, dispatch and reporting for the recurrence are independent of the first pass.
///
/// DELIBERATE DEVIATION FROM THE SPEC'S LITERAL WORDING, DOCUMENTED RATHER THAN SILENTLY WORKED AROUND:
/// the spec's Then column says "Ledger Resolved -&gt; neue Detected-Row". AgentConditionStateMachine.
/// AllowedTransitions[Executed] = [] (Klacks.Api/Domain/Constants/AgentConditionStateMachine.cs) - Executed
/// has ZERO legal outgoing transitions, and MarkResolvedAsync only ever iterates GetOpenByKindAsync's
/// result, which excludes every TerminalStatuses member (Executed among them) by construction. An already-
/// Executed row can therefore never literally become Resolved in this codebase, no matter what the next
/// tick observes. What actually reopens the fingerprint for a fresh insert is simpler: Executed is ALREADY
/// terminal, so it already falls outside the partial unique index's open-row filter the moment it is
/// written - FindOpenByFingerprintAsync finds nothing, and UpsertDetectedAsync inserts a brand new row on
/// the very next detection, no intervening Resolved transition required. This test asserts that real
/// behaviour - the old row stays Executed forever - rather than a Resolved value the implementation cannot
/// produce for this Given. "Ledger Resolved" in the spec reads as the colloquial "the old finding is done",
/// not the literal enum value, for a Given that starts from Executed specifically.
///
/// Reuses the Az1/Az2/Az3 fixture shape (synthetic kind, SingleKindRegistry wrapping the real
/// EmptyContainerRemediationBinder, capturing executor) with its own TestPrefix. The value only a real
/// database can add here: Fingerprint carries a partial UNIQUE index scoped to open statuses
/// (AgentConditionConfiguration, built from the same AgentConditionStateMachine.TerminalStatuses set) -
/// EF InMemory does not enforce it, so only Postgres can prove the re-arm insert actually succeeds once the
/// old row is terminal, and would actually reject a genuine duplicate while the old row is still open.
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
public class EmptyContainerRecurrenceScenarioTests
{
    private const string TestPrefix = "INTEGRATION_TEST_AZ5_";
    private const string Kind = TestPrefix + "empty_container_like";

    private static readonly Guid OwnerUserId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTime FarPastUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Az5_TheSameContainerGoingEmptyAgainAfterExecution_OpensAFreshRowWithItsOwnDedupAndReport()
    {
        var shiftId = Guid.NewGuid();
        var fingerprint = TestPrefix + shiftId;
        var payloadJson = BuildPayloadJson(shiftId);

        var firstCondition = await GivenReportedConditionAsync(shiftId, fingerprint, payloadJson);

        var executor = new CapturingSkillExecutor();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var timeProvider = new SettableTimeProvider(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));

        await using (var firstContext = NewContext())
        {
            var firstResult = await NewService(firstContext, executor, reporter, timeProvider).RunAsync(CancellationToken.None);
            firstResult.Executed.ShouldBe(1);
        }

        // Advance well past AgentConditionActionDefaults.CascadeWindowMinutes (ProactiveHeartbeat.
        // ScanIntervalMinutes = 60) so IsCascadeAsync's GetExecutedSinceAsync(now - 60min) lookback no
        // longer includes the first execution. Without this the recurrence would be misidentified as a
        // cascade of its own predecessor (same EntityId, DetectedAtUtc >= HandledAtUtc) and skipped rather
        // than executed - confirmed by running this fixture with TimeProvider.System first (both runs then
        // land within milliseconds of each other): Executed came back 0, not 1.
        timeProvider.Now = timeProvider.Now.AddDays(3);

        await using (var afterFirstRun = NewContext())
        {
            var stored = await afterFirstRun.AgentConditions.SingleAsync(c => c.Id == firstCondition.Id);
            stored.Status.ShouldBe(AgentConditionStatus.Executed);
        }

        // Three days later, the same real-world container is empty again. UpsertDetectedAsync is what
        // AgentTriggerBackgroundService.RunDetectorAsync calls per detected finding - driven directly here
        // rather than through the real EmptyContainerDetector, matching how Az1/Az2/Az3 hand-build their
        // AgentCondition rows for the same isolation reasons.
        (AgentCondition SecondCondition, bool IsNew) upsertResult;
        await using (var ledgerContext = NewContext())
        {
            var ledgerService = new AgentConditionLedgerService(
                new AgentConditionRepository(ledgerContext), timeProvider,
                NullLogger<AgentConditionLedgerService>.Instance);

            upsertResult = await ledgerService.UpsertDetectedAsync(
                Kind, fingerprint, shiftId, groupId: null, AgentTriggerSeverity.High, payloadJson,
                CancellationToken.None);

            upsertResult.IsNew.ShouldBeTrue(
                "Executed is terminal and already excluded from the open-fingerprint index, so the "
                + "recurrence must insert a fresh row, never reopen or mutate the first one.");
            upsertResult.SecondCondition.Id.ShouldNotBe(firstCondition.Id);
            upsertResult.SecondCondition.Fingerprint.ShouldBe(fingerprint);
            upsertResult.SecondCondition.Status.ShouldBe(AgentConditionStatus.Detected);

            await ledgerService.TryTransitionAsync(
                upsertResult.SecondCondition.Id, AgentConditionStatus.Detected, AgentConditionStatus.Reported,
                cancellationToken: CancellationToken.None);
        }

        await using (var secondContext = NewContext())
        {
            var secondResult = await NewService(secondContext, executor, reporter, timeProvider).RunAsync(CancellationToken.None);
            secondResult.Executed.ShouldBe(1, "The recurrence must be actioned on its own, independent of the first pass.");
        }

        executor.Invocations.Count.ShouldBe(2, "Two separate executions: one per row, dedup did not suppress the recurrence.");
        await reporter.Received(2).ReportAsync(OwnerUserId, Arg.Any<string>(), Arg.Any<CancellationToken>());

        await using var verify = NewContext();
        var rows = await verify.AgentConditions.Where(c => c.Fingerprint == fingerprint).ToListAsync();
        rows.Count.ShouldBe(2, "Both the original and the recurrence must exist as distinct rows sharing the same fingerprint.");
        rows.ShouldAllBe(c => c.Status == AgentConditionStatus.Executed);

        var events = await verify.AgentConditionEvents
            .Where(e => rows.Select(c => c.Id).Contains(e.ConditionId))
            .AsNoTracking()
            .ToListAsync();
        events.Where(e => e.ConditionId == firstCondition.Id && e.EventType == AgentConditionStatus.Executed.ToString())
            .Count().ShouldBe(1);
        events.Where(e => e.ConditionId == upsertResult.SecondCondition.Id && e.EventType == AgentConditionStatus.Executed.ToString())
            .Count().ShouldBe(1, "The recurrence's Executed event belongs to its own row, not appended to the first row's history.");
    }

    private static string BuildPayloadJson(Guid shiftId)
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

        return JsonSerializer.Serialize(triggerEvent.Payload);
    }

    private static async Task<AgentCondition> GivenReportedConditionAsync(Guid shiftId, string fingerprint, string payloadJson)
    {
        var condition = new AgentCondition
        {
            Id = shiftId,
            TriggerKind = Kind,
            Fingerprint = fingerprint,
            EntityId = shiftId,
            GroupId = null,
            Severity = AgentTriggerSeverity.High,
            Status = AgentConditionStatus.Reported,
            DetectedAtUtc = FarPastUtc,
            LastSeenAtUtc = FarPastUtc,
            PayloadJson = payloadJson,
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
