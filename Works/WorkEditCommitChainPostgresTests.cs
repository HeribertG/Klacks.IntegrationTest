// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Happy-path characterization of the Work update, delete, restore and reassign chains against PostgreSQL (IT database
/// klacks) with a retrying context like production (WorkWriteChainStack): the write, the commit, the schedule change
/// marker and the recalculated period hours are committed, and the completion service pushes PeriodHoursUpdated exactly
/// once per affected client (the handlers push none themselves). The overtime cascade is a plain substitute here: no
/// successor is under test. A client_period_hours row with stale values is seeded for every client so the
/// recalculation is visible. All state is read in a fresh context.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class WorkEditCommitChainPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "EDIT_";
    private const string SourceClientSuffix = "CLIENT";
    private const string TargetClientSuffix = "TARGET";
    private const decimal SeededWorkTime = 8m;
    private const decimal EditedWorkTime = 10m;
    private const decimal StaleHours = 77m;
    private const decimal StaleSurcharges = 7m;

    private static readonly TimeOnly EditedEnd = new(18, 0);

    private Guid _clientId;
    private Guid _targetClientId;
    private Guid _shiftId;
    private Guid _workId;
    private (DateOnly Start, DateOnly End) _period;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, SourceClientSuffix);
        _targetClientId = await WorkChainTestData.SeedClientAsync(Prefix, TargetClientSuffix);
        _shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);
        _workId = await WorkChainTestData.SeedWorkAsync(
            _clientId, _shiftId, WorkChainTestData.MorningStart, WorkChainTestData.MorningEnd, SeededWorkTime);

        await using (var boundaries = new WorkWriteChainStack())
        {
            _period = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(WorkChainTestData.Day);
        }

        await WorkChainTestData.SeedPeriodHoursAsync(_clientId, _period, StaleHours, StaleSurcharges);
        await WorkChainTestData.SeedPeriodHoursAsync(_targetClientId, _period, StaleHours, StaleSurcharges);
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task Put_HappyPath_PersistsTheEditPeriodHoursAndScheduleChange_AndPushesPeriodHoursOnce()
    {
        await using var stack = new WorkWriteChainStack();
        var resource = new WorkResource
        {
            Id = _workId,
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = WorkChainTestData.Day,
            StartTime = WorkChainTestData.MorningStart,
            EndTime = EditedEnd,
            WorkTime = EditedWorkTime
        };

        var result = await stack.NewPutHandler().Handle(new PutCommand<WorkResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        var state = await ChainStateSnapshot.ReadAsync(_clientId);
        var stored = state.WorkById(_workId);
        stored.IsDeleted.ShouldBeFalse();
        stored.EndTime.ShouldBe(EditedEnd);
        stored.WorkTime.ShouldBe(EditedWorkTime);
        ThenPeriodHoursWereRecalculated(state, _clientId, EditedWorkTime);
        state.LiveScheduleChangesOf(_clientId).Select(sc => sc.ChangeDate).ShouldBe([WorkChainTestData.Day]);
        await ThenPeriodHoursWerePushedOnceFor(stack, (_clientId, EditedWorkTime));
        await stack.WorkNotifications.Received(1).NotifyWorkUpdated(Arg.Is<WorkNotificationDto>(n => n.WorkId == _workId));
    }

    [Test]
    public async Task Delete_HappyPath_SoftDeletesTheWork_RecalculatesPeriodHours_TracksTheDay_AndPushesPeriodHoursOnce()
    {
        await using var stack = new WorkWriteChainStack();

        var result = await stack.NewDeleteHandler().Handle(
            new DeleteWorkCommand(_workId, _period.Start, _period.End), CancellationToken.None);

        result.ShouldNotBeNull();
        var state = await ChainStateSnapshot.ReadAsync(_clientId);
        state.WorkById(_workId).IsDeleted.ShouldBeTrue();
        ThenPeriodHoursWereRecalculated(state, _clientId, 0m);
        state.LiveScheduleChangesOf(_clientId).Select(sc => sc.ChangeDate).ShouldBe([WorkChainTestData.Day]);
        await ThenPeriodHoursWerePushedOnceFor(stack, (_clientId, 0m));
        await stack.WorkNotifications.Received(1).NotifyWorkDeleted(Arg.Is<WorkNotificationDto>(n => n.WorkId == _workId));
    }

    [Test]
    public async Task Restore_HappyPath_BringsTheWorkBack_RecalculatesPeriodHours_TracksTheDay_AndPushesPeriodHoursOnce()
    {
        await WorkChainTestData.SoftDeleteWorkAsync(_workId);
        await using var stack = new WorkWriteChainStack();

        var result = await stack.NewRestoreHandler().Handle(new RestoreWorkCommand(_workId), CancellationToken.None);

        result.ShouldNotBeNull();
        var state = await ChainStateSnapshot.ReadAsync(_clientId);
        var stored = state.WorkById(_workId);
        stored.IsDeleted.ShouldBeFalse();
        stored.DeletedTime.ShouldBeNull();
        ThenPeriodHoursWereRecalculated(state, _clientId, SeededWorkTime);
        state.LiveScheduleChangesOf(_clientId).Select(sc => sc.ChangeDate).ShouldBe([WorkChainTestData.Day]);
        await ThenPeriodHoursWerePushedOnceFor(stack, (_clientId, SeededWorkTime));
        await stack.WorkNotifications.Received(1).NotifyWorkCreated(Arg.Is<WorkNotificationDto>(n => n.WorkId == _workId));
    }

    [Test]
    public async Task Reassign_HappyPath_HandsTheWorkOver_RecalculatesBothClients_TracksBothDays_AndPushesPeriodHoursOncePerClient()
    {
        await using var stack = new WorkWriteChainStack();

        var result = await stack.NewReassignHandler().Handle(
            new ReassignWorkClientCommand(_workId, _targetClientId), CancellationToken.None);

        result.ShouldNotBeNull();
        var state = await ChainStateSnapshot.ReadAsync(_clientId, _targetClientId);
        state.WorkById(_workId).ClientId.ShouldBe(_targetClientId);
        state.LiveWorksOf(_clientId).ShouldBeEmpty();
        ThenPeriodHoursWereRecalculated(state, _targetClientId, SeededWorkTime);
        ThenPeriodHoursWereRecalculated(state, _clientId, 0m);
        state.LiveScheduleChangesOf(_targetClientId).Select(sc => sc.ChangeDate).ShouldBe([WorkChainTestData.Day]);
        state.LiveScheduleChangesOf(_clientId).Select(sc => sc.ChangeDate).ShouldBe([WorkChainTestData.Day]);
        await ThenPeriodHoursWerePushedOnceFor(stack, (_targetClientId, SeededWorkTime), (_clientId, 0m));
        await stack.WorkNotifications.Received(1).NotifyWorkUpdated(
            Arg.Is<WorkNotificationDto>(n => n.WorkId == _workId && n.ClientId == _targetClientId));
    }

    private void ThenPeriodHoursWereRecalculated(ChainStateSnapshot state, Guid clientId, decimal expectedHours)
    {
        var periodHours = state.PeriodHoursOf(clientId);
        periodHours.StartDate.ShouldBe(_period.Start);
        periodHours.EndDate.ShouldBe(_period.End);
        periodHours.Hours.ShouldBe(expectedHours);
        periodHours.Surcharges.ShouldBe(0m);
    }

    private static async Task ThenPeriodHoursWerePushedOnceFor(
        WorkWriteChainStack stack, params (Guid ClientId, decimal Hours)[] expected)
    {
        foreach (var (clientId, hours) in expected)
        {
            await stack.WorkNotifications.Received(1).NotifyPeriodHoursUpdated(
                Arg.Is<PeriodHoursNotificationDto>(n => n.ClientId == clientId && n.Hours == hours));
        }

        stack.WorkNotifications.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(stack.WorkNotifications.NotifyPeriodHoursUpdated))
            .ShouldBe(expected.Length);
    }
}
