// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end proof against PostgreSQL that a proactive finding naming employees reaches each planner only
/// with the employees that planner may see. Composes the genuine pipeline - AgentConditionLedgerService for
/// the ledger row, AgentTriggerService for the dispatch, ProactiveTriggerDispatchRepository and
/// AgentConditionRepository on a real DbContext, GetProactiveMessagesQueryHandler for the inbox read,
/// ProactiveReminderService for the sweep, AcknowledgeProactiveMessageCommandHandler for the stop - and
/// substitutes only the audience resolver (whose client-membership query has its own Postgres test,
/// GroupItemVisibilityMembershipPostgresTests), SignalR and the messenger.
///
/// The scenario uses the real ClientMissingCoreDataSummaryTriggerEvent, the aggregate whose ledger payload
/// carries "names" and "count" scalars that the inbox merges over the frozen params. Its DedupKey is the
/// missing-field string alone, so a prefixed field keeps every row this fixture writes attributable:
/// dispatches by dedup_key prefix, ledger rows by fingerprint. The clock starts at T0 = 2026-08-01, in the
/// past, for the same reason EmptyContainerReminderScenarioTests documents (foreign rows are never due inside
/// a sweep of this fixture; OneTimeSetUp fails loudly if they were). Same operating constraint: not while a
/// dev app sweeps the same database.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
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
public class ClientScopedNotificationAudienceScenarioTests
{
    private const string TestPrefix = "INTEGRATION_TEST_AUDSCOPE_";
    private const string MissingField = TestPrefix + "contact";

    private static readonly string Admin = Guid.Parse("71000000-0000-0000-0000-000000000001").ToString();
    private static readonly string SupervisorOfAnn = Guid.Parse("71000000-0000-0000-0000-000000000002").ToString();
    private static readonly string SupervisorOfNobody = Guid.Parse("71000000-0000-0000-0000-000000000003").ToString();

    private static readonly Guid AnnId = Guid.NewGuid();
    private static readonly Guid BobId = Guid.NewGuid();
    private static readonly Guid CarlId = Guid.NewGuid();

    private static readonly DateTime T0 = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SweepHorizonUtc = T0.AddHours(10);

    private SettableTimeProvider _timeProvider = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAgentTriggerPreferenceService _preferences = null!;
    private IAssistantNotificationService _notifications = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();

        await using var context = NewContext();
        var foreignDue = await context.AgentTriggerDispatches.CountAsync(d =>
            d.ConditionId != null
            && d.AcknowledgedAtUtc == null
            && d.NextReminderAtUtc != null
            && d.NextReminderAtUtc <= SweepHorizonUtc
            && !d.DedupKey.StartsWith(TestPrefix));
        foreignDue.ShouldBe(0,
            "Foreign dispatch rows are due below this fixture's sweep horizon; the kind-blind sweep would advance them.");
    }

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new SettableTimeProvider(T0);

        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>()).Returns(Set(Admin));
        _audienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Set(Admin, SupervisorOfAnn, SupervisorOfNobody));
        _audienceResolver.GetPlanningUserIdsForClientAsync(AnnId, Arg.Any<CancellationToken>())
            .Returns(Set(Admin, SupervisorOfAnn));
        _audienceResolver.GetPlanningUserIdsForClientAsync(BobId, Arg.Any<CancellationToken>()).Returns(Set(Admin));
        _audienceResolver.GetPlanningUserIdsForClientAsync(CarlId, Arg.Any<CancellationToken>()).Returns(Set(Admin));

        _preferences = Substitute.For<IAgentTriggerPreferenceService>();
        _preferences.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        _notifications = Substitute.For<IAssistantNotificationService>();
        _notifications.GetConnectedUserIdsAsync().Returns(Array.Empty<string>());
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task AggregateFinding_IsNarrowedPerPlanner_InTheRowTheInboxAndAcrossReminderAndAcknowledge()
    {
        var firstScan = Event(("Ann", AnnId), ("Bob", BobId));
        var condition = await GivenReportedConditionAsync(firstScan);

        await using (var dispatchContext = NewContext())
        {
            await NewTriggerService(dispatchContext).OnEventAsync(firstScan, CancellationToken.None);
        }

        var rows = await LoadRowsAsync();
        rows.Select(row => row.UserId).ShouldBe([Admin, SupervisorOfAnn], ignoreOrder: true);
        rows.ShouldAllBe(row => row.ConditionId == condition.Id && row.NextReminderAtUtc == T0.AddHours(1));

        var supervisorFrozen = Params(rows.Single(row => row.UserId == SupervisorOfAnn).ContentParamsJson);
        supervisorFrozen["count"].ShouldBe("1");
        supervisorFrozen["names"].ShouldBe("Ann");
        Params(rows.Single(row => row.UserId == Admin).ContentParamsJson)["count"].ShouldBe("2");

        // The next tick refreshes the ledger payload with a grown, workforce-wide list.
        await RefreshConditionPayloadAsync(Event(("Ann", AnnId), ("Bob", BobId), ("Carl", CarlId)));

        var supervisorInbox = await ReadInboxAsync(SupervisorOfAnn);
        supervisorInbox.ContentParams["count"].ShouldBe("1");
        supervisorInbox.ContentParams["names"].ShouldBe("Ann");

        var adminInbox = await ReadInboxAsync(Admin);
        adminInbox.ContentParams["count"].ShouldBe("3");
        adminInbox.ContentParams["names"].ShouldContain("Carl");

        (await ReadInboxPageAsync(SupervisorOfNobody)).ShouldBeEmpty();

        // The narrowed row is a normal member of the reminder loop and stops on acknowledge.
        _timeProvider.Now = T0.AddHours(1);
        (await RunSweepAsync()).Reminded.ShouldBe(2);
        (await ReadInboxAsync(SupervisorOfAnn)).ContentParams["names"].ShouldBe("Ann");

        var supervisorRow = (await LoadRowsAsync()).Single(row => row.UserId == SupervisorOfAnn);
        supervisorRow.ReminderCount.ShouldBe(1);

        await using (var ackContext = NewContext())
        {
            var acknowledged = await new AcknowledgeProactiveMessageCommandHandler(
                    new ProactiveTriggerDispatchRepository(ackContext, _timeProvider))
                .Handle(new AcknowledgeProactiveMessageCommand { Id = supervisorRow.Id, UserId = SupervisorOfAnn }, CancellationToken.None);
            acknowledged.ShouldBeTrue();
        }

        _timeProvider.Now = T0.AddHours(5);
        (await RunSweepAsync()).Reminded.ShouldBe(1, "Only the Admin's row is still unacknowledged.");
        (await LoadRowsAsync()).Single(row => row.UserId == SupervisorOfAnn).ReminderCount.ShouldBe(1);
    }

    [Test]
    public async Task AggregateFinding_SecondTickForTheSameCondition_IsDedupedPerPlanner()
    {
        var scan = Event(("Ann", AnnId), ("Bob", BobId));
        await GivenReportedConditionAsync(scan);

        await using (var firstContext = NewContext())
        {
            await NewTriggerService(firstContext).OnEventAsync(scan, CancellationToken.None);
        }

        await using (var secondContext = NewContext())
        {
            var outcome = await NewTriggerService(secondContext).OnEventAsync(scan, CancellationToken.None);
            outcome.Persisted.ShouldBe(0);
            outcome.Deduped.ShouldBe(2);
        }

        (await LoadRowsAsync()).Count.ShouldBe(2);
    }

    private static ClientMissingCoreDataSummaryTriggerEvent Event(params (string Name, Guid Id)[] clients) =>
        new(clients.Select(client => new ProactiveAffectedClient(client.Id, client.Name)).ToList(), MissingField);

    private static IReadOnlySet<string> Set(params string[] userIds) =>
        new HashSet<string>(userIds, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Params(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json!)!;

    private AgentTriggerService NewTriggerService(DataBaseContext context)
    {
        var offlineMessengerNotifier = Substitute.For<IOfflineMessengerNotifier>();
        offlineMessengerNotifier
            .TrySendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(OfflineMessengerDeliveryResult.NoContact);

        return new AgentTriggerService(
            new AgentTriggerRateLimiter(_timeProvider),
            _preferences,
            _notifications,
            new ProactiveTriggerDispatchRepository(context, _timeProvider),
            new AgentConditionRepository(context),
            Substitute.For<IUserActivityTracker>(),
            _audienceResolver,
            offlineMessengerNotifier,
            Substitute.For<IProactiveMessengerTextComposer>(),
            _timeProvider,
            NullLogger<AgentTriggerService>.Instance);
    }

    private async Task<AgentCondition> GivenReportedConditionAsync(IAgentTriggerEvent triggerEvent)
    {
        await using var context = NewContext();
        var ledger = NewLedger(context);

        var (condition, isNew) = await ledger.UpsertDetectedAsync(
            triggerEvent.Kind,
            AgentConditionLedgerPolicy.FingerprintFor(triggerEvent),
            triggerEvent.EntityId,
            AgentConditionLedgerPolicy.LedgerGroupIdsFor(triggerEvent),
            triggerEvent.Severity,
            JsonSerializer.Serialize(triggerEvent.Payload),
            CancellationToken.None);
        isNew.ShouldBeTrue();

        (await ledger.TryTransitionAsync(
            condition.Id, AgentConditionStatus.Detected, AgentConditionStatus.Reported,
            cancellationToken: CancellationToken.None)).ShouldBeTrue();

        return condition;
    }

    private async Task RefreshConditionPayloadAsync(IAgentTriggerEvent triggerEvent)
    {
        await using var context = NewContext();
        var (_, isNew) = await NewLedger(context).UpsertDetectedAsync(
            triggerEvent.Kind,
            AgentConditionLedgerPolicy.FingerprintFor(triggerEvent),
            triggerEvent.EntityId,
            AgentConditionLedgerPolicy.LedgerGroupIdsFor(triggerEvent),
            triggerEvent.Severity,
            JsonSerializer.Serialize(triggerEvent.Payload),
            CancellationToken.None);
        isNew.ShouldBeFalse();
    }

    private AgentConditionLedgerService NewLedger(DataBaseContext context) =>
        new(new AgentConditionRepository(context), _timeProvider, NullLogger<AgentConditionLedgerService>.Instance);

    private async Task<IReadOnlyList<Klacks.Api.Application.DTOs.Assistant.ProactiveInboxMessageDto>> ReadInboxPageAsync(string userId)
    {
        await using var context = NewContext();
        var handler = new GetProactiveMessagesQueryHandler(
            new ProactiveTriggerDispatchRepository(context, _timeProvider),
            new AgentConditionRepository(context),
            _audienceResolver);

        var page = await handler.Handle(new GetProactiveMessagesQuery { UserId = userId }, CancellationToken.None);
        return page.Where(message => message.Kind == AgentTriggerKinds.ClientMissingCoreData).ToList();
    }

    private async Task<Klacks.Api.Application.DTOs.Assistant.ProactiveInboxMessageDto> ReadInboxAsync(string userId) =>
        (await ReadInboxPageAsync(userId)).Single();

    private async Task<ProactiveReminderSweepResult> RunSweepAsync()
    {
        await using var context = NewContext();
        return await new ProactiveReminderService(
                new ProactiveTriggerDispatchRepository(context, _timeProvider),
                new AgentConditionRepository(context),
                _preferences,
                _notifications,
                Substitute.For<IUserActivityTracker>(),
                _audienceResolver,
                _timeProvider,
                NullLogger<ProactiveReminderService>.Instance)
            .RunAsync(CancellationToken.None);
    }

    private static async Task<List<ProactiveTriggerDispatchRow>> LoadRowsAsync()
    {
        await using var context = NewContext();
        return await context.AgentTriggerDispatches
            .AsNoTracking()
            .Where(d => d.DedupKey.StartsWith(TestPrefix))
            .ToListAsync();
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
        var fingerprintPrefix = AgentConditionLedgerPolicy.FingerprintFor(AgentTriggerKinds.ClientMissingCoreData, TestPrefix);

        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_trigger_dispatches WHERE starts_with(dedup_key, {0})",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_condition_events WHERE condition_id IN "
            + "(SELECT id FROM agent_conditions WHERE starts_with(fingerprint, {0}))",
            fingerprintPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE starts_with(fingerprint, {0})",
            fingerprintPrefix);
    }
}
