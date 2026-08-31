// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Az4 of the Klacksy-Autonomie test spec extended by package F (B11, "repeat until acknowledged"):
/// a condition-linked proactive message that the planner never reacts to is re-delivered on the
/// reminder backoff schedule (first due +1h, then +4h, +24h, +48h, last step repeating) until the
/// acknowledgement - explicit or implied by a reaction - ends the loop.
///
/// REAL COMPOSITION, unlike every sibling fixture in this directory: no existing integration test
/// drives the actual AgentTriggerService, so this fixture composes the genuine pipeline - real
/// ProactiveTriggerDispatchRepository(context, timeProvider), real AgentConditionRepository(context)
/// (so AgentTriggerService.ResolveConditionIdAsync finds the ledger row through
/// AgentConditionLedgerPolicy.FingerprintFor), real InMemoryAgentTriggerPreferenceService and
/// AgentTriggerRateLimiter, real AgentConditionLedgerService for the ledger writes - and substitutes
/// only the outward-facing edges (SignalR notification, audience resolution, messenger, activity
/// tracking). The sweep is the real ProactiveReminderService, fed from the SAME SettableTimeProvider
/// as the repositories and AgentTriggerService, so every due date is deterministic.
///
/// The synthetic trigger kind carries the fixture prefix, and the clock starts at T0 = 2026-08-01,
/// well in the past: any dispatch row the rest of the system might write carries a first due date of
/// (real now + 1h), which is AFTER every sweep instant this fixture uses, so foreign rows can never
/// fall due inside a sweep. A foreign row already overdue below the sweep horizon would still be
/// picked up (the sweep deliberately has no kind filter), so OneTimeSetUp guards on exactly that set
/// and fails loudly instead of letting the sweep advance rows this fixture does not own.
///
/// Expected due dates are derived from the schedule ladder (ProactiveReminderDefaults.BackoffHours
/// = [1, 4, 24, 48]), not from the wall clock: after reminder N at time T the next due date is
/// T + BackoffHours[min(N, 3)], giving T0+1h, T0+5h, T0+29h, T0+77h, T0+125h. CreateTime is never
/// asserted - DataBaseContext stamps it from the system clock, so only Next/LastRemindedAtUtc carry
/// the injected time.
///
/// Cleanup deletes ONLY rows this fixture created: dispatches by trigger_kind prefix, ledger rows
/// (and their events) by fingerprint prefix.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
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
public class EmptyContainerReminderScenarioTests
{
    private const string TestPrefix = "INTEGRATION_TEST_AZ4_";
    private const string Kind = TestPrefix + "empty_container_like";

    private static readonly Guid PlannerUserId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly string PlannerUserIdString = PlannerUserId.ToString();

    private static readonly DateTime T0 = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Latest instant any sweep of this fixture ever runs at (T0 + 125h), with margin.</summary>
    private static readonly DateTime SweepHorizonUtc = T0.AddHours(200);

    private SettableTimeProvider _timeProvider = null!;
    private InMemoryAgentTriggerPreferenceService _preferences = null!;
    private AgentTriggerRateLimiter _rateLimiter = null!;
    private IAssistantNotificationService _notifications = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IOfflineMessengerNotifier _offlineMessengerNotifier = null!;
    private IProactiveMessengerTextComposer _messengerTextComposer = null!;
    private IUserActivityTracker _activityTracker = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();

        // The reminder sweep has no kind filter by design, so it would advance ANY row due below the
        // sweep horizon - including ones this fixture does not own. Fail loudly instead of mutating
        // foreign rows; rows created later by the running system are due at real-now + 1h, which is
        // beyond the horizon and therefore never picked up.
        await using var context = NewContext();
        var foreignDue = await context.AgentTriggerDispatches.CountAsync(d =>
            d.ContentKey != null
            && d.ConditionId != null
            && d.AcknowledgedAtUtc == null
            && d.NextReminderAtUtc != null
            && d.NextReminderAtUtc <= SweepHorizonUtc
            && !d.TriggerKind.StartsWith(TestPrefix));
        foreignDue.ShouldBe(0,
            "Foreign dispatch rows are due below this fixture's sweep horizon; the kind-blind sweep would advance them.");
    }

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new SettableTimeProvider(T0);
        _preferences = new InMemoryAgentTriggerPreferenceService(_timeProvider);
        _rateLimiter = new AgentTriggerRateLimiter(_timeProvider);

        _notifications = Substitute.For<IAssistantNotificationService>();
        _notifications.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());

        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PlannerUserIdString });
        _audienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PlannerUserIdString });

        _offlineMessengerNotifier = Substitute.For<IOfflineMessengerNotifier>();
        _offlineMessengerNotifier
            .TrySendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(OfflineMessengerDeliveryResult.NoContact);

        _messengerTextComposer = Substitute.For<IProactiveMessengerTextComposer>();
        _activityTracker = Substitute.For<IUserActivityTracker>();
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Az4_AnUnacknowledgedMessage_IsRemindedOnTheBackoffScheduleUntilAcknowledged()
    {
        var shiftId = Guid.NewGuid();
        var condition = await GivenReportedConditionAsync(shiftId);

        await using (var dispatchContext = NewContext())
        {
            await NewTriggerService(dispatchContext).OnEventAsync(new TestTriggerEvent(shiftId), CancellationToken.None);
        }

        var row = await ReloadDispatchAsync(shiftId);
        row.ConditionId.ShouldBe(condition.Id,
            "The event is ledger-tracked, so the dispatch row must link the open ledger row found by fingerprint.");
        row.UserId.ShouldBe(PlannerUserIdString);
        row.ReminderCount.ShouldBe(0);
        row.LastRemindedAtUtc.ShouldBeNull();
        row.AcknowledgedAtUtc.ShouldBeNull();
        row.NextReminderAtUtc.ShouldBe(T0.AddHours(1), "FirstDueAfter(T0) = T0 + BackoffHours[0].");

        // +30 min: nothing due yet.
        _timeProvider.Now = T0.AddMinutes(30);
        var early = await RunSweepAsync();
        early.Due.ShouldBe(0);
        early.Reminded.ShouldBe(0);

        // +1h: first reminder. Next = T1 + BackoffHours[1] = T0+1h + 4h.
        _timeProvider.Now = T0.AddHours(1);
        var first = await RunSweepAsync();
        first.Due.ShouldBe(1);
        first.Reminded.ShouldBe(1);
        row = await ReloadDispatchAsync(shiftId);
        row.ReminderCount.ShouldBe(1);
        row.LastRemindedAtUtc.ShouldBe(T0.AddHours(1));
        row.NextReminderAtUtc.ShouldBe(T0.AddHours(5));
        row.ReadAtUtc.ShouldBeNull("The advance resurfaces the row as unread.");

        // +5h: second reminder. Next = T0+5h + BackoffHours[2] = +24h.
        _timeProvider.Now = T0.AddHours(5);
        (await RunSweepAsync()).Reminded.ShouldBe(1);
        row = await ReloadDispatchAsync(shiftId);
        row.ReminderCount.ShouldBe(2);
        row.LastRemindedAtUtc.ShouldBe(T0.AddHours(5));
        row.NextReminderAtUtc.ShouldBe(T0.AddHours(29));

        // +29h: third reminder. Next = T0+29h + BackoffHours[3] = +48h.
        _timeProvider.Now = T0.AddHours(29);
        (await RunSweepAsync()).Reminded.ShouldBe(1);
        row = await ReloadDispatchAsync(shiftId);
        row.ReminderCount.ShouldBe(3);
        row.LastRemindedAtUtc.ShouldBe(T0.AddHours(29));
        row.NextReminderAtUtc.ShouldBe(T0.AddHours(77));

        // +77h: fourth reminder. The schedule is exhausted, so the last step repeats: +48h again.
        _timeProvider.Now = T0.AddHours(77);
        (await RunSweepAsync()).Reminded.ShouldBe(1);
        row = await ReloadDispatchAsync(shiftId);
        row.ReminderCount.ShouldBe(4);
        row.LastRemindedAtUtc.ShouldBe(T0.AddHours(77));
        row.NextReminderAtUtc.ShouldBe(T0.AddHours(125),
            "RepeatLastStepUntilAcknowledged: counts beyond the ladder keep the last backoff interval.");

        // The user finally acknowledges: AcknowledgedAtUtc is stamped and the loop ends.
        await using (var ackContext = NewContext())
        {
            var handler = new AcknowledgeProactiveMessageCommandHandler(
                new ProactiveTriggerDispatchRepository(ackContext, _timeProvider));
            var acknowledged = await handler.Handle(
                new AcknowledgeProactiveMessageCommand { Id = row.Id, UserId = PlannerUserIdString },
                CancellationToken.None);
            acknowledged.ShouldBeTrue();
        }

        row = await ReloadDispatchAsync(shiftId);
        row.AcknowledgedAtUtc.ShouldBe(T0.AddHours(77));
        row.NextReminderAtUtc.ShouldBeNull();

        // The due date the fourth reminder had set comes and goes with no further delivery.
        _timeProvider.Now = T0.AddHours(125);
        var afterAck = await RunSweepAsync();
        afterAck.Due.ShouldBe(0);
        afterAck.Reminded.ShouldBe(0);
    }

    [Test]
    public async Task Az4_AMutedUser_DefersTheReminderWithoutBurningABackoffStep()
    {
        var shiftId = Guid.NewGuid();
        await GivenReportedConditionAsync(shiftId);

        await using (var dispatchContext = NewContext())
        {
            await NewTriggerService(dispatchContext).OnEventAsync(new TestTriggerEvent(shiftId), CancellationToken.None);
        }

        // The mute is set AFTER the dispatch on purpose: OnEventAsync applies the same preference
        // gate, and muting before it would swallow the initial message instead of the reminder.
        await _preferences.MuteAsync(PlannerUserIdString, Kind, true);

        _timeProvider.Now = T0.AddHours(1);
        var muted = await RunSweepAsync();
        muted.Due.ShouldBe(1);
        muted.Skipped.ShouldBe(1, "A muted user's due row is deferred, not delivered.");
        muted.Reminded.ShouldBe(0);

        var row = await ReloadDispatchAsync(shiftId);
        row.ReminderCount.ShouldBe(0, "A muted deferral must not burn a backoff step.");
        row.LastRemindedAtUtc.ShouldBeNull();
        row.NextReminderAtUtc.ShouldBe(T0.AddHours(2),
            "Deferred to NextDueAfter(count = 0, now) = T0+1h + BackoffHours[0].");

        // Unmuted again, the row continues on the FIRST backoff step, not a later one.
        await _preferences.MuteAsync(PlannerUserIdString, Kind, false);
        _timeProvider.Now = T0.AddHours(2);
        (await RunSweepAsync()).Reminded.ShouldBe(1);

        row = await ReloadDispatchAsync(shiftId);
        row.ReminderCount.ShouldBe(1);
        row.LastRemindedAtUtc.ShouldBe(T0.AddHours(2));
        row.NextReminderAtUtc.ShouldBe(T0.AddHours(6), "T0+2h + BackoffHours[1] - the mute cost time, not a step.");
    }

    [Test]
    public async Task Az4_ADismissal_AcknowledgesTheMessageAndRejectsTheLedgerCondition()
    {
        var shiftId = Guid.NewGuid();
        var condition = await GivenReportedConditionAsync(shiftId);

        await using (var dispatchContext = NewContext())
        {
            await NewTriggerService(dispatchContext).OnEventAsync(new TestTriggerEvent(shiftId), CancellationToken.None);
        }

        _timeProvider.Now = T0.AddHours(1);
        (await RunSweepAsync()).Reminded.ShouldBe(1);
        var row = await ReloadDispatchAsync(shiftId);
        row.NextReminderAtUtc.ShouldBe(T0.AddHours(5));

        await using (var dismissContext = NewContext())
        {
            var handler = new SetProactiveReactionCommandHandler(
                new ProactiveTriggerDispatchRepository(dismissContext, _timeProvider),
                Substitute.For<IDismissStreakEvaluator>(),
                new AgentConditionLedgerService(
                    new AgentConditionRepository(dismissContext), _timeProvider,
                    NullLogger<AgentConditionLedgerService>.Instance),
                Substitute.For<IHelpfulBoostEvaluator>(),
                _timeProvider,
                NullLogger<SetProactiveReactionCommandHandler>.Instance);

            var handled = await handler.Handle(
                new SetProactiveReactionCommand
                {
                    Id = row.Id,
                    UserId = PlannerUserIdString,
                    Reaction = ProactiveReaction.Dismissed,
                    RejectReason = AgentConditionRejectReason.WrongThisTime,
                },
                CancellationToken.None);
            handled.ShouldBeTrue();
        }

        row = await ReloadDispatchAsync(shiftId);
        row.Reaction.ShouldBe(ProactiveReaction.Dismissed);
        row.RejectReason.ShouldBe(AgentConditionRejectReason.WrongThisTime);
        row.AcknowledgedAtUtc.ShouldBe(T0.AddHours(1),
            "A reaction settles the message, which implies the acknowledgement that stops the loop.");
        row.NextReminderAtUtc.ShouldBeNull();

        await using (var ledgerVerify = NewContext())
        {
            var storedCondition = await ledgerVerify.AgentConditions.SingleAsync(c => c.Id == condition.Id);
            storedCondition.Status.ShouldBe(AgentConditionStatus.Rejected,
                "The dismissal writes the rejection back onto the finding the message reported.");
        }

        _timeProvider.Now = T0.AddHours(5);
        var afterDismiss = await RunSweepAsync();
        afterDismiss.Due.ShouldBe(0, "Neither the acknowledged row nor a terminal condition can be reminded.");
        afterDismiss.Reminded.ShouldBe(0);
    }

    private async Task<ProactiveReminderSweepResult> RunSweepAsync()
    {
        await using var context = NewContext();
        return await new ProactiveReminderService(
            new ProactiveTriggerDispatchRepository(context, _timeProvider),
            new AgentConditionRepository(context),
            _preferences,
            _notifications,
            _activityTracker,
            _timeProvider,
            NullLogger<ProactiveReminderService>.Instance)
            .RunAsync(CancellationToken.None);
    }

    private AgentTriggerService NewTriggerService(DataBaseContext context) =>
        new(
            _rateLimiter,
            _preferences,
            _notifications,
            new ProactiveTriggerDispatchRepository(context, _timeProvider),
            new AgentConditionRepository(context),
            _activityTracker,
            _audienceResolver,
            _offlineMessengerNotifier,
            _messengerTextComposer,
            _timeProvider,
            NullLogger<AgentTriggerService>.Instance);

    /// <summary>
    /// The T0 "Upsert + Reported" of the spec, driven through the real ledger service the way the
    /// detector tick does it: UpsertDetectedAsync opens the row (Detected), then the legal
    /// Detected -&gt; Reported transition makes it visible to the pipeline.
    /// </summary>
    private async Task<AgentCondition> GivenReportedConditionAsync(Guid shiftId)
    {
        var fingerprint = AgentConditionLedgerPolicy.FingerprintFor(Kind, shiftId.ToString());

        await using var context = NewContext();
        var ledger = new AgentConditionLedgerService(
            new AgentConditionRepository(context), _timeProvider,
            NullLogger<AgentConditionLedgerService>.Instance);

        var (condition, isNew) = await ledger.UpsertDetectedAsync(
            Kind, fingerprint, shiftId, groupId: null, AgentTriggerSeverity.High, "{}",
            CancellationToken.None);
        isNew.ShouldBeTrue();

        var reported = await ledger.TryTransitionAsync(
            condition.Id, AgentConditionStatus.Detected, AgentConditionStatus.Reported,
            cancellationToken: CancellationToken.None);
        reported.ShouldBeTrue();

        return condition;
    }

    private static async Task<ProactiveTriggerDispatchRow> ReloadDispatchAsync(Guid shiftId)
    {
        await using var context = NewContext();
        return await context.AgentTriggerDispatches
            .SingleAsync(d => d.TriggerKind == Kind && d.DedupKey == shiftId.ToString());
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
            "DELETE FROM agent_trigger_dispatches WHERE trigger_kind LIKE {0}",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_condition_events WHERE condition_id IN "
            + "(SELECT id FROM agent_conditions WHERE fingerprint LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE fingerprint LIKE {0}",
            TestPrefix + "%");
    }

    /// <summary>
    /// Synthetic stand-in for EmptyContainerTriggerEvent under the fixture's own kind, so the rows
    /// stay attributable to this fixture and cleanup can find them. Mirrors the real event's shape:
    /// planner-audited (PlannersOnly + RequiresGroupScope, no groups =&gt; admin audience), high
    /// severity, shift-id dedup key. Ledger-tracked per AgentConditionLedgerPolicy.IsLedgerTracked,
    /// which is what links the dispatch row to the condition and arms the reminder loop.
    /// </summary>
    private sealed record TestTriggerEvent(Guid ShiftId) : IAgentTriggerEvent
    {
        public string Kind => EmptyContainerReminderScenarioTests.Kind;

        public string Severity => AgentTriggerSeverity.High;

        public string Summary => TestPrefix + "summary";

        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();

        public bool PlannersOnly => true;

        public bool RequiresGroupScope => true;

        public string DedupKey => ShiftId.ToString();

        public Guid? EntityId => ShiftId;
    }
}
