// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The approval chain's fail-closed end, against the real database and the real conditional updates —
/// which is the point of doing it here: the unit tests run over an in-memory double because the EF
/// in-memory provider has no ExecuteUpdateAsync, so "the exhaust and the stage cancellation land in one
/// transaction" and "the stage guard also looks at the chain's status" are claims only Postgres can
/// actually settle.
///
/// The failure being pinned: after a force-exhaust, stages used to be left Pending/Notified, so a late
/// acknowledgement won the stage compare-and-swap, lost the chain compare-and-swap, and returned success
/// without ever reaching the approval stamp. The user was told their approval landed; nothing ran, and
/// no trace of the discrepancy existed anywhere.
///
/// The exhaust is driven through IEscalationChainService.ForceExhaustAsync, NOT through
/// EscalationChainBackgroundService.RunCycleAsync, deliberately: that sweep exhausts every overdue
/// Running chain and supersedes every chain whose absence report was deleted, database-wide. In the
/// shared integration-test database that would mutate rows this fixture did not create, which no
/// cleanup can undo. ForceExhaustAsync is the exact code path the sweep calls, scoped to one chain; the
/// sweep's own ordering is covered by EscalationChainBackgroundServiceSweepOrderTests.
///
/// The parallel test states the invariant directly rather than a sequence: for a given condition it must
/// never be true that the chain ended Exhausted AND the condition carries an approval stamp. Each racing
/// task gets its own DataBaseContext, because one context is not thread-safe and sharing it would test
/// the harness instead of the database.
///
/// Cleanup deletes ONLY rows this fixture created, reached from its own fingerprint prefix.
/// </summary>

using System.Security.Claims;
using System.Text.Json;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.IntegrationTest.Assistant.Proactive;

[TestFixture]
[Category("RealDatabase")]
public class ProactiveApprovalChainExhaustScenarioTests
{
    private const string TestPrefix = "INTEGRATION_TEST_APPROVAL_EXHAUST_";
    private const string Kind = TestPrefix + "empty_container_like";
    private const int WindowMinutes = 30;
    private const int RosterSize = 2;
    private const int RaceRounds = 10;
    private const string ExhaustReason = "deadline passed before every stage could be resolved";

    private static readonly DateTime FarPastUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StartedAtUtc = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task DeadlinePassed_ExhaustingTheChain_CancelsEveryStageInTheSameTransaction()
    {
        var conditionId = Guid.NewGuid();
        var firstApprover = Guid.NewGuid();
        await GivenReportedConditionAsync(conditionId);

        var timeProvider = new SettableTimeProvider(StartedAtUtc);
        var chainId = await StartApprovalChainAsync(conditionId, firstApprover, timeProvider);

        timeProvider.Now = PastTheDeadline();
        await using (var sweepContext = NewContext())
        {
            (await NewChainService(sweepContext, timeProvider).ForceExhaustAsync(chainId, ExhaustReason))
                .ShouldBeTrue();
        }

        await using var verify = NewContext();
        var chain = await verify.EscalationChains.AsNoTracking().SingleAsync(c => c.Id == chainId);
        chain.Status.ShouldBe(EscalationChainStatus.Exhausted);

        var stages = await verify.EscalationStages.AsNoTracking()
            .Where(s => s.EscalationChainId == chainId)
            .ToListAsync();
        stages.Count.ShouldBe(RosterSize);
        stages.ShouldAllBe(s => s.Status == EscalationStageStatus.Cancelled);
    }

    [Test]
    public async Task AcknowledgeAfterTheExhaust_Answers409_AndStampsNoApproval()
    {
        var conditionId = Guid.NewGuid();
        var firstApprover = Guid.NewGuid();
        await GivenReportedConditionAsync(conditionId);

        var timeProvider = new SettableTimeProvider(StartedAtUtc);
        var chainId = await StartApprovalChainAsync(conditionId, firstApprover, timeProvider);

        timeProvider.Now = PastTheDeadline();
        await using (var sweepContext = NewContext())
        {
            await NewChainService(sweepContext, timeProvider).ForceExhaustAsync(chainId, ExhaustReason);
        }

        await using (var acknowledgeContext = NewContext())
        {
            var controller = NewController(acknowledgeContext, timeProvider, firstApprover);
            var result = await controller.Acknowledge(chainId);

            result.Result.ShouldBeOfType<ConflictObjectResult>();
        }

        await using var verify = NewContext();
        var condition = await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == conditionId);
        condition.ApprovedByUserId.ShouldBeNull("An exhausted chain must never release an approval.");
        condition.ApprovedAtUtc.ShouldBeNull();

        var approvedEvents = await verify.AgentConditionEvents.AsNoTracking()
            .CountAsync(e => e.ConditionId == conditionId && e.EventType == AgentConditionEventTypes.Approved);
        approvedEvents.ShouldBe(0);
    }

    [Test]
    public async Task ExhaustRacingAnAcknowledge_NeverEndsExhaustedAndStamped()
    {
        for (var round = 0; round < RaceRounds; round++)
        {
            var conditionId = Guid.NewGuid();
            var firstApprover = Guid.NewGuid();
            await GivenReportedConditionAsync(conditionId);

            var timeProvider = new SettableTimeProvider(StartedAtUtc);
            var chainId = await StartApprovalChainAsync(conditionId, firstApprover, timeProvider);

            await using var exhaustContext = NewContext();
            await using var acknowledgeContext = NewContext();
            var exhaustService = NewChainService(exhaustContext, timeProvider);
            var acknowledgeService = NewChainService(acknowledgeContext, timeProvider);

            await Task.WhenAll(
                Task.Run(() => exhaustService.ForceExhaustAsync(chainId, ExhaustReason)),
                Task.Run(() => acknowledgeService.AcknowledgeChainAsync(chainId, firstApprover.ToString())));

            await using var verify = NewContext();
            var chain = await verify.EscalationChains.AsNoTracking().SingleAsync(c => c.Id == chainId);
            var condition = await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == conditionId);

            (chain.Status == EscalationChainStatus.Exhausted && condition.ApprovedByUserId != null).ShouldBeFalse(
                $"Round {round}: chain {chain.Status} with approver {condition.ApprovedByUserId} - an exhausted "
                + "chain that carries an approval stamp is the state nothing downstream can act on.");
        }
    }

    private static DateTime PastTheDeadline() =>
        StartedAtUtc.AddMinutes((WindowMinutes * RosterSize) + 1);

    private static async Task<Guid> StartApprovalChainAsync(
        Guid conditionId, Guid firstApprover, SettableTimeProvider timeProvider)
    {
        await using var context = NewContext();
        var service = NewChainService(context, timeProvider);

        var roster = new List<EscalationRosterCandidate>
        {
            new(firstApprover.ToString(), TestPrefix + "first"),
            new(Guid.NewGuid().ToString(), TestPrefix + "second")
        };

        var deadlineUtc = ProactiveApprovalDeadline.Compute(timeProvider.Now, roster.Count, WindowMinutes);
        var chainId = await service.StartConditionApprovalChainAsync(
            new StartConditionApprovalChainRequest(conditionId, GroupId: null, roster, deadlineUtc));

        chainId.ShouldNotBeNull();
        return chainId!.Value;
    }

    private static EscalationChainService NewChainService(DataBaseContext context, TimeProvider timeProvider)
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        var notifier = Substitute.For<IEscalationNotifier>();
        notifier
            .NotifyStageAsync(
                Arg.Any<EscalationChain>(), Arg.Any<EscalationStage>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new EscalationNotificationResult(
                OfflineMessengerDeliveryOutcome.Sent, Guid.NewGuid(), EscalationDeliveryChannels.Inbox));

        return new EscalationChainService(
            new EscalationChainRepository(context),
            Substitute.For<IEscalationRosterService>(),
            notifier,
            settingsReader,
            new AgentConditionLedgerService(
                new AgentConditionRepository(context), timeProvider, NullLogger<AgentConditionLedgerService>.Instance),
            timeProvider,
            NullLogger<EscalationChainService>.Instance);
    }

    private static EscalationChainsController NewController(
        DataBaseContext context, TimeProvider timeProvider, Guid currentUserId)
    {
        var handler = new AcknowledgeEscalationChainCommandHandler(
            NewChainService(context, timeProvider), new EscalationChainRepository(context));

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString()) }, "Test");

        return new EscalationChainsController(new DirectAcknowledgeMediator(handler))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private static async Task GivenReportedConditionAsync(Guid conditionId)
    {
        var triggerEvent = new EmptyContainerTriggerEvent(
            conditionId,
            TestPrefix + "container",
            DateOnly.FromDateTime(StartedAtUtc),
            null,
            [],
            new ContainerScheduleSnapshot(
                new TimeOnly(6, 0), new TimeOnly(14, 0), [3, 5], IsHoliday: false, IsWeekdayAndHoliday: false),
            IsPeriodActive: true);

        var condition = new AgentCondition
        {
            Id = conditionId,
            TriggerKind = Kind,
            Fingerprint = TestPrefix + conditionId,
            EntityId = conditionId,
            GroupId = null,
            Severity = AgentTriggerSeverity.High,
            Status = AgentConditionStatus.Reported,
            DetectedAtUtc = FarPastUtc,
            LastSeenAtUtc = FarPastUtc,
            PayloadJson = JsonSerializer.Serialize(triggerEvent.Payload)
        };

        await using var context = NewContext();
        context.AgentConditions.Add(condition);
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
        const string ConditionScope = "(SELECT id FROM agent_conditions WHERE fingerprint LIKE {0})";

        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM escalation_stages WHERE escalation_chain_id IN "
            + "(SELECT id FROM escalation_chains WHERE condition_id IN " + ConditionScope + ")",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM escalation_chains WHERE condition_id IN " + ConditionScope,
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_condition_events WHERE condition_id IN " + ConditionScope,
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE fingerprint LIKE {0}",
            TestPrefix + "%");
    }

    /// <summary>
    /// Routes the one command this fixture sends straight to the real handler, so the assertion is on the
    /// controller's own status-code mapping over a live outcome. A hand-written stub rather than a
    /// substitute: IMediator has a single generic method, and a lambda returning a Task through
    /// NSubstitute's Returns overloads is the kind of resolution that silently returns null instead.
    /// </summary>
    private sealed class DirectAcknowledgeMediator : IMediator
    {
        private readonly AcknowledgeEscalationChainCommandHandler _handler;

        public DirectAcknowledgeMediator(AcknowledgeEscalationChainCommandHandler handler)
        {
            _handler = handler;
        }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var result = await _handler.Handle((AcknowledgeEscalationChainCommand)request, cancellationToken);
            return (TResponse)(object)result;
        }
    }
}
