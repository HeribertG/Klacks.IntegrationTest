// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the Work create chain against PostgreSQL (IT database klacks) with a retrying context like
/// production (WorkWriteChainStack): commit of the Work, the overtime successor commit (CommittingOvertimeCascadeFake
/// on a seeded later Work of the same day), the schedule change marker, the timeline queue and the period hours,
/// followed by the handler notifications. A client_period_hours row with known stale values is seeded for the period
/// the service itself resolves, so a skipped recalculation stays visible. When the chain breaks after the first commit,
/// the create still succeeds: the Work stays committed, the response carries the live period hours, the stale cached
/// row is dropped instead of left behind, no PeriodHoursUpdated is pushed and exactly one thorough recalculation of the
/// pay period is queued for the client. All state is read in a fresh context.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Notifications;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class WorkPostCommitChainPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "POST_";
    private const string ClientSuffix = "CLIENT";
    private const decimal PostedWorkTime = 8m;
    private const decimal SuccessorWorkTime = 2m;
    private const decimal StaleHours = 77m;
    private const decimal StaleSurcharges = 7m;
    private const int SuccessorAndPostedWorkCount = 2;

    private Guid _clientId;
    private Guid _shiftId;
    private Guid _successorId;
    private (DateOnly Start, DateOnly End) _period;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, ClientSuffix);
        _shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);
        _successorId = await WorkChainTestData.SeedWorkAsync(
            _clientId, _shiftId, WorkChainTestData.EveningStart, WorkChainTestData.EveningEnd, SuccessorWorkTime);

        await using (var boundaries = new WorkWriteChainStack())
        {
            _period = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(WorkChainTestData.Day);
        }

        await WorkChainTestData.SeedPeriodHoursAsync(_clientId, _period, StaleHours, StaleSurcharges);
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task Post_HappyPath_PersistsWorkSuccessorMarkerPeriodHoursAndScheduleChange_AndPushesPeriodHoursOnce()
    {
        await using var stack = new WorkWriteChainStack();
        var cascade = UseCommittingCascade(stack);

        var result = await stack.NewPostHandler().Handle(new PostCommand<WorkResource>(NewResource()), CancellationToken.None);

        var expectedHours = PostedWorkTime + SuccessorWorkTime;
        result.ShouldNotBeNull();
        result.PeriodHours!.Hours.ShouldBe(expectedHours);
        cascade.CallCount.ShouldBe(1);

        var state = await ChainStateSnapshot.ReadAsync(_clientId);
        state.LiveWorksOf(_clientId).Count.ShouldBe(SuccessorAndPostedWorkCount);
        state.WorkById(result.Id).WorkTime.ShouldBe(PostedWorkTime);
        state.WorkById(_successorId).Surcharges.ShouldBe(CommittingOvertimeCascadeFake.SurchargeMarker);
        var periodHours = state.PeriodHoursOf(_clientId);
        periodHours.Hours.ShouldBe(expectedHours);
        periodHours.Surcharges.ShouldBe(CommittingOvertimeCascadeFake.SurchargeMarker);
        state.LiveScheduleChangesOf(_clientId).Select(sc => sc.ChangeDate).ShouldBe([WorkChainTestData.Day]);

        await stack.WorkNotifications.Received(1).NotifyPeriodHoursUpdated(
            Arg.Is<PeriodHoursNotificationDto>(n => n.ClientId == _clientId && n.Hours == expectedHours));
        PeriodHoursPushCount(stack).ShouldBe(1);
        await stack.WorkNotifications.Received(1).NotifyScheduleChangeTracked(
            Arg.Is<ScheduleChangeNotificationDto>(n => n.ClientId == _clientId && n.ChangeDate == WorkChainTestData.Day));
        await stack.WorkNotifications.Received(1).NotifyWorkCreated(
            Arg.Is<WorkNotificationDto>(n => n.ClientId == _clientId));
        await stack.ShiftStatsNotifications.Received(1).NotifyShiftStatsUpdated(
            Arg.Is<ShiftStatsNotificationDto>(n => n.ShiftId == _shiftId));
        stack.Timeline.Received(1).QueueCheck(_clientId, WorkChainTestData.Day, null);
    }

    [Test]
    public async Task Post_WhenReprocessThrowsBeforeItsCommit_KeepsTheWork_DiscardsTheMarker_AnswersWithLivePeriodHours_DropsTheStaleRow_AndQueuesTheRecalculation()
    {
        await using var stack = new WorkWriteChainStack();
        UseCommittingCascade(stack, throwBeforeCommit: true);

        var result = await stack.NewPostHandler().Handle(new PostCommand<WorkResource>(NewResource()), CancellationToken.None);

        result.ShouldNotBeNull();
        var periodHoursAnswer = result.PeriodHours.ShouldNotBeNull();
        periodHoursAnswer.Hours.ShouldBe(PostedWorkTime + SuccessorWorkTime);
        periodHoursAnswer.Surcharges.ShouldBe(0m);
        var state = await ChainStateSnapshot.ReadAsync(_clientId);
        var live = state.LiveWorksOf(_clientId);
        live.Count.ShouldBe(SuccessorAndPostedWorkCount);
        live.Single(w => w.Id != _successorId).WorkTime.ShouldBe(PostedWorkTime);
        state.WorkById(_successorId).Surcharges.ShouldBe(0m);
        ThenNoCachedPeriodHoursRowIsLeft(state);
        state.ScheduleChanges.ShouldBeEmpty();
        await ThenTheCreateWasNotifiedOnce_WithoutAPeriodHoursPush(stack);
        stack.WorkNotifications.ReceivedCalls().Count().ShouldBe(1);
        stack.Timeline.Received(1).QueueCheck(_clientId, WorkChainTestData.Day, null);
        ThenExactlyThePeriodWasQueued(stack);
    }

    [Test]
    public async Task Post_WhenThePeriodHoursSaveFails_KeepsWorkMarkerAndScheduleChange_AnswersWithLivePeriodHours_DropsTheStaleRow_AndQueuesTheRecalculation()
    {
        var interceptor = new ClientPeriodHoursSaveFailureInterceptor();
        await using var stack = new WorkWriteChainStack(interceptor);
        UseCommittingCascade(stack);

        var result = await stack.NewPostHandler().Handle(new PostCommand<WorkResource>(NewResource()), CancellationToken.None);

        result.ShouldNotBeNull();
        var periodHoursAnswer = result.PeriodHours.ShouldNotBeNull();
        periodHoursAnswer.Hours.ShouldBe(PostedWorkTime + SuccessorWorkTime);
        periodHoursAnswer.Surcharges.ShouldBe(CommittingOvertimeCascadeFake.SurchargeMarker);
        interceptor.FailureCount.ShouldBe(1);
        var state = await ChainStateSnapshot.ReadAsync(_clientId);
        state.LiveWorksOf(_clientId).Count.ShouldBe(SuccessorAndPostedWorkCount);
        state.WorkById(_successorId).Surcharges.ShouldBe(CommittingOvertimeCascadeFake.SurchargeMarker);
        ThenNoCachedPeriodHoursRowIsLeft(state);
        state.LiveScheduleChangesOf(_clientId).Select(sc => sc.ChangeDate).ShouldBe([WorkChainTestData.Day]);

        await stack.WorkNotifications.Received(1).NotifyScheduleChangeTracked(
            Arg.Is<ScheduleChangeNotificationDto>(n => n.ClientId == _clientId && n.ChangeDate == WorkChainTestData.Day));
        await ThenTheCreateWasNotifiedOnce_WithoutAPeriodHoursPush(stack);
        stack.WorkNotifications.ReceivedCalls().Count().ShouldBe(2);
        stack.Timeline.Received(1).QueueCheck(_clientId, WorkChainTestData.Day, null);
        ThenExactlyThePeriodWasQueued(stack);
    }

    private CommittingOvertimeCascadeFake UseCommittingCascade(WorkWriteChainStack stack, bool throwBeforeCommit = false)
    {
        var cascade = new CommittingOvertimeCascadeFake(stack.Context, stack.UnitOfWork, _successorId, throwBeforeCommit);
        stack.OvertimeCascade = cascade;
        return cascade;
    }

    private void ThenNoCachedPeriodHoursRowIsLeft(ChainStateSnapshot state)
        => state.PeriodHours.Where(p => p.ClientId == _clientId).ShouldBeEmpty();

    private async Task ThenTheCreateWasNotifiedOnce_WithoutAPeriodHoursPush(WorkWriteChainStack stack)
    {
        await stack.WorkNotifications.Received(1).NotifyWorkCreated(
            Arg.Is<WorkNotificationDto>(n => n.ClientId == _clientId));
        await stack.ShiftStatsNotifications.Received(1).NotifyShiftStatsUpdated(
            Arg.Is<ShiftStatsNotificationDto>(n => n.ShiftId == _shiftId));
        stack.ShiftStatsNotifications.ReceivedCalls().Count().ShouldBe(1);
        PeriodHoursPushCount(stack).ShouldBe(0);
    }

    private void ThenExactlyThePeriodWasQueued(WorkWriteChainStack stack)
    {
        stack.ThoroughRecalculationQueue.Received(1).QueueRecalculation(
            _period.Start,
            _period.End,
            Arg.Is<Guid?>(group => group == null),
            Arg.Is<Guid?>(token => token == null),
            Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.SequenceEqual(new[] { _clientId })));
        stack.ThoroughRecalculationQueue.ReceivedCalls().Count().ShouldBe(1);
    }

    private static int PeriodHoursPushCount(WorkWriteChainStack stack)
        => stack.WorkNotifications.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(stack.WorkNotifications.NotifyPeriodHoursUpdated));


    private WorkResource NewResource() => new()
    {
        Id = Guid.Empty,
        ClientId = _clientId,
        ShiftId = _shiftId,
        CurrentDate = WorkChainTestData.Day,
        StartTime = WorkChainTestData.MorningStart,
        EndTime = WorkChainTestData.MorningEnd,
        WorkTime = PostedWorkTime
    };
}
