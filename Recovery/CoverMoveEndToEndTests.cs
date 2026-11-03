// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A real move through cover_absence against PostgreSQL, driven by the mediator of the in-process server: a work with a
/// sub-work, an unpaid sub-break, a correction and an expense goes to the stand-in, the shift macro recalculates everything
/// with the stand-in's contract and the period hours of both people follow. When the macro cannot run, the same cover stays
/// a replacement and the surcharges stay with the person who owns the work. A swap whose relocation hop falls back leaves
/// three people with consistent period hours and the same check findings as a swap that moved both works. Far-future dates
/// keep the rows away from other schedule data; every row hangs off an INTEGRATION_TEST_CME_ key and is removed by that
/// prefix only.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.ScheduleTimeline;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class CoverMoveEndToEndTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_CME_";
    private const string WorkingMacro = """
        import hour
        import nightrate

        DIM Bonus
        Bonus = hour * nightrate

        OUTPUT 1, Round(Bonus, 2)
        """;
    private const string BrokenMacro = "OUTPUT 1, (";
    private const string AbsenceGuid = "00000000-0000-0000-00e2-000000000001";
    private const string UnpaidAbsenceGuid = "00000000-0000-0000-00e2-000000000002";
    private const decimal AbsentRate = 0.1m;
    private const decimal StandInRate = 0.5m;
    private const decimal StaleSurcharges = 99m;

    private const string CollisionFinding = "collision";

    private static readonly DateOnly Day = new(2090, 9, 12);
    private static readonly DateOnly SecondDay = new(2090, 11, 14);
    private static readonly DateOnly StartedDay = new(2026, 9, 8);
    private DateOnly _seedDay = Day;
    private static readonly DateTime ValidFrom = new(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private Guid _groupId;
    private Guid _absentId;
    private Guid _standInId;
    private Guid _recipientId;
    private Guid _shiftId;
    private Guid _workId;
    private Guid _subWorkId;
    private Guid _subBreakId;
    private Guid _correctionId;
    private Guid _expenseId;
    private readonly List<Guid> _scenarioTokens = [];

    [SetUp]
    public async Task SeedSetUp()
    {
        await PurgeAsync();
        _scenarioTokens.Clear();
    }

    [TearDown]
    public async Task SeedTearDown()
    {
        foreach (var token in _scenarioTokens)
        {
            await PurgeScenarioTokenAsync(token);
        }

        await PurgeAsync();
    }

    [Test]
    public async Task AWholeCover_MovesTheWorkWithEverythingOnIt_AndRecalculatesForTheStandIn()
    {
        await SeedAsync(WorkingMacro);

        var outcome = await CoverAsync();

        var covered = outcome.Covered.ShouldHaveSingleItem();
        covered.Kind.ShouldBe(ReplacementRequestKind.Move);
        var token = outcome.Token;
        (await Context.WorkChange.AsNoTracking().CountAsync(wc =>
            wc.AnalyseToken == token && wc.ReplaceClientId == _standInId)).ShouldBe(0);

        var work = await Context.Work.AsNoTracking().SingleAsync(w => w.AnalyseToken == token && w.ClonedFromWorkId == _workId);
        work.ClientId.ShouldBe(_standInId);
        work.MovedFromClientId.ShouldBe(_absentId);
        work.Surcharges.ShouldBe((8m - 1m) * StandInRate, "the whole span less the unpaid sub-break, at the stand-in's rate");

        var subWork = await Context.Work.AsNoTracking().SingleAsync(w => w.AnalyseToken == token && w.ClonedFromWorkId == _subWorkId);
        subWork.ClientId.ShouldBe(_standInId);
        subWork.ParentWorkId.ShouldBe(work.Id);
        subWork.Surcharges.ShouldBe(1m * StandInRate);

        var subBreak = await Context.Break.AsNoTracking().SingleAsync(b => b.AnalyseToken == token && b.ParentWorkId == work.Id);
        subBreak.ClientId.ShouldBe(_standInId);
        subBreak.WorkTime.ShouldBe(-1m);
        subBreak.Surcharges.ShouldBe(-1m * StandInRate);

        var correction = await Context.WorkChange.AsNoTracking().SingleAsync(wc => wc.AnalyseToken == token && wc.WorkId == work.Id);
        correction.Surcharges.ShouldBe(1m * StandInRate);

        (await Context.Expenses.AsNoTracking().CountAsync(e => e.AnalyseToken == token && e.WorkId == work.Id)).ShouldBe(1);

        var standInHours = await PeriodHoursAsync(_standInId, token);
        standInHours.Hours.ShouldBe(8m + 1m - 1m + 1m, "work, sub-work, unpaid sub-break, correction");
        standInHours.Surcharges.ShouldBe(((8m - 1m) + 1m - 1m + 1m) * StandInRate);
        var absentHours = await PeriodHoursAsync(_absentId, token);
        absentHours.Hours.ShouldBe(8m, "only the absence stays with the absent person");
        absentHours.Surcharges.ShouldBe(0m);

        var realWork = await Context.Work.AsNoTracking().SingleAsync(w => w.Id == _workId);
        realWork.ClientId.ShouldBe(_absentId, "the real plan changes only on accept");
        realWork.Surcharges.ShouldBe(StaleSurcharges);

        var bookRow = await Context.ReplacementRequests.AsNoTracking().SingleAsync(r => r.AnalyseToken == token);
        bookRow.Kind.ShouldBe(ReplacementRequestKind.Move);
        bookRow.WorkId.ShouldBe(work.Id);
    }

    [Test]
    public async Task AnOpenPlace_IsRemovedOnAccept_WithItsChildrenAndExpenses_TheCreditStays()
    {
        await SeedAsync(WorkingMacro);
        await BlacklistAsync(_standInId, _shiftId);

        var outcome = await CoverAsync();

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.ShouldNotBeEmpty();
        (await Context.Work.AsNoTracking().CountAsync(w => w.Id == _workId)).ShouldBe(1, "the real plan changes only on accept");

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new AcceptAnalyseScenarioCommand(outcome.ScenarioId), CancellationToken.None);
        }

        (await Context.Work.AsNoTracking().CountAsync(w =>
            w.CurrentDate == Day && (w.ClientId == _absentId || w.ClientId == _standInId))).ShouldBe(0, "nothing of the service stands");
        (await Context.Expenses.AsNoTracking().CountAsync(e => e.Description == Prefix)).ShouldBe(0, "not billed, so no expense stands");
        var absence = await Context.Break.AsNoTracking().SingleAsync(b =>
            b.ClientId == _absentId && b.CurrentDate == Day && b.AnalyseToken == null && b.ParentWorkId == null);
        absence.WorkTime.ShouldBe(8m, "the credit was calculated before the service went");
        using var hoursScope = CreateScope();
        var periodHours = hoursScope.ServiceProvider.GetRequiredService<Klacks.Api.Domain.Interfaces.Schedules.IPeriodHoursService>();
        var (periodStart, periodEnd) = await periodHours.GetPeriodBoundariesAsync(Day);
        (await periodHours.CalculatePeriodHoursAsync(_absentId, periodStart, periodEnd, null)).Hours.ShouldBe(8m);
    }

    [Test]
    public async Task AnAcceptedAlternative_TakesTheMovedWork_AndTheAcceptStampsItsRow()
    {
        var day = NextTuesdayInTheRecordableWindow();
        await SeedAsync(WorkingMacro, day);
        _recipientId = Guid.NewGuid();
        var rule = await Context.SchedulingRules.AsNoTracking().FirstAsync(r => r.Name == Prefix + "RULE");
        AddEmployee(_recipientId, "Recipient", rule.Id, AbsentRate);
        await Context.SaveChangesAsync();

        var outcome = await CoverAsync(day);
        var proposal = outcome.Covered.ShouldHaveSingleItem();
        proposal.Kind.ShouldBe(ReplacementRequestKind.Move);
        var alternative = proposal.ReplacementClientId == _standInId ? _recipientId : _standInId;

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
                new RecordReplacementContactCommand(new RecordReplacementContactRequest(
                    _absentId, alternative, proposal.ShiftId, day, proposal.StartTime!.Value, proposal.EndTime!.Value,
                    _groupId, Guid.Parse(AbsenceGuid), outcome.Token, ReplacementRequestOutcome.Accepted)),
                CancellationToken.None);
        }

        var clone = await Context.Work.AsNoTracking().SingleAsync(w => w.AnalyseToken == outcome.Token && w.ClonedFromWorkId == _workId);
        clone.ClientId.ShouldBe(alternative);
        clone.MovedFromClientId.ShouldBe(_absentId);
        (await Context.WorkChange.AsNoTracking().CountAsync(wc =>
            wc.AnalyseToken == outcome.Token && wc.WorkId == clone.Id && wc.ReplaceClientId != null)).ShouldBe(0);

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new AcceptAnalyseScenarioCommand(outcome.ScenarioId), CancellationToken.None);
        }

        var row = await Context.ReplacementRequests.AsNoTracking().SingleAsync(r =>
            r.AnalyseToken == outcome.Token && r.CandidateClientId == alternative);
        row.Kind.ShouldBe(ReplacementRequestKind.Move);
        row.WorkId.ShouldBe(clone.Id);
        row.AppliedAtUtc.ShouldNotBeNull();
        (await Context.ReplacementRequests.AsNoTracking().CountAsync(r =>
            r.AnalyseToken == outcome.Token && r.CandidateClientId == proposal.ReplacementClientId)).ShouldBe(0);
    }

    [Test]
    public async Task AnAcceptanceInAFollowUpScenario_SwitchesTheMovedWorkOfTheOpenSource()
    {
        var day = NextTuesdayInTheRecordableWindow();
        await SeedAsync(WorkingMacro, day);
        var alternative = await AddAlternativeAsync();

        var source = await CoverAsync(day);
        var proposal = source.Covered.ShouldHaveSingleItem();
        var other = proposal.ReplacementClientId == _standInId ? alternative : _standInId;

        var followUp = await CoverOnScenarioAsync(day.AddDays(1), source.Token);
        await AcceptAlternativeAsync(followUp.Token, proposal, other, day);

        var switched = await Context.Work.AsNoTracking().SingleAsync(w =>
            w.AnalyseToken == followUp.Token && w.CurrentDate == day && w.MovedFromClientId == _absentId);
        switched.ClientId.ShouldBe(other);
    }

    [Test]
    public async Task AnAppliedMove_GoesBack_WithItsMarkerClearedAndNewMacros()
    {
        await SeedAsync(WorkingMacro);
        var (row, workId) = await AcceptTheMoveAsync();
        await EndTheAbsenceAsync();
        var moved = await Context.Work.AsNoTracking().SingleAsync(w => w.Id == workId);

        await ReverseAsync(row);

        var back = await Context.Work.AsNoTracking().SingleAsync(w => w.Id == workId);
        back.ClientId.ShouldBe(_absentId);
        back.MovedFromClientId.ShouldBeNull();
        back.Surcharges.ShouldBe(moved.Surcharges / StandInRate * AbsentRate, "the macro ran for the person it went back to");
        var stamped = await Context.ReplacementRequests.AsNoTracking().SingleAsync(r => r.Id == row);
        stamped.ReversedAtUtc.ShouldNotBeNull();
        stamped.Outcome.ShouldBe(ReplacementRequestOutcome.Proposed, "the recorded answer stays");
    }

    [Test]
    public async Task AMovedWork_ShowsItsOriginInTheSchedule_AndGoesBackByItsWork()
    {
        await SeedAsync(WorkingMacro);
        var (row, workId) = await AcceptTheMoveAsync();
        await EndTheAbsenceAsync();

        using (var scope = CreateScope())
        {
            var cells = await scope.ServiceProvider.GetRequiredService<Klacks.Api.Domain.Interfaces.Schedules.IScheduleEntriesService>()
                .GetScheduleEntriesAsync(new Klacks.Api.Domain.Models.Schedules.ScheduleEntriesCriteria(Day, Day, null, null), CancellationToken.None);
            cells.Single(c => c.SourceId == workId && c.EntryType == 0).MovedFromClientId.ShouldBe(_absentId);
            cells.Where(c => c.SourceId != workId).ShouldAllBe(c => c.MovedFromClientId == null);
        }

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new ReverseMovedWorkCommand(workId), CancellationToken.None);
        }

        var back = await Context.Work.AsNoTracking().SingleAsync(w => w.Id == workId);
        back.ClientId.ShouldBe(_absentId);
        back.MovedFromClientId.ShouldBeNull();
        (await Context.ReplacementRequests.AsNoTracking().SingleAsync(r => r.Id == row)).ReversedAtUtc.ShouldNotBeNull();

        using (var scope = CreateScope())
        {
            var refused = await Should.ThrowAsync<Klacks.Api.Domain.Exceptions.MoveReversalRefusedException>(() =>
                scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ReverseMovedWorkCommand(workId), CancellationToken.None));
            refused.ErrorCode.ShouldBe(Klacks.Api.Domain.Constants.MoveReversalRefusalCodes.AlreadyBack);
        }
    }

    [Test]
    public async Task AReassignToTheOrigin_CountsAsTheReversal()
    {
        await SeedAsync(WorkingMacro);
        var (row, workId) = await AcceptTheMoveAsync();

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new Klacks.Api.Application.Commands.Works.ReassignWorkClientCommand(workId, _absentId), CancellationToken.None);
        }

        (await Context.Work.AsNoTracking().SingleAsync(w => w.Id == workId)).MovedFromClientId.ShouldBeNull();
        (await Context.ReplacementRequests.AsNoTracking().SingleAsync(r => r.Id == row)).ReversedAtUtc.ShouldNotBeNull();
        await Should.ThrowAsync<Klacks.Api.Domain.Exceptions.InvalidRequestException>(() => ReverseAsync(row));
    }

    [Test]
    public async Task AMoveIsRefused_WhenTheWorkMovedOn()
    {
        await SeedAsync(WorkingMacro);
        var (row, workId) = await AcceptTheMoveAsync();
        var third = await AddAlternativeAsync();
        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new Klacks.Api.Application.Commands.Works.ReassignWorkClientCommand(workId, third), CancellationToken.None);
        }

        var refused = await Should.ThrowAsync<Klacks.Api.Domain.Exceptions.InvalidRequestException>(() => ReverseAsync(row));

        refused.Message.ShouldContain("moved on");
        (await Context.Work.AsNoTracking().SingleAsync(w => w.Id == workId)).ClientId.ShouldBe(third);
    }

    [Test]
    public async Task AMoveIsRefused_WhileItsOriginIsStillAbsent()
    {
        await SeedAsync(WorkingMacro);
        var (row, workId) = await AcceptTheMoveAsync();

        using var scope = CreateScope();
        var refused = await Should.ThrowAsync<Klacks.Api.Domain.Exceptions.MoveReversalRefusedException>(() =>
            scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ReverseMovedWorkCommand(workId), CancellationToken.None));

        refused.ErrorCode.ShouldBe(Klacks.Api.Domain.Constants.MoveReversalRefusalCodes.OriginBusy);
        (await Context.Work.AsNoTracking().SingleAsync(w => w.Id == workId)).MovedFromClientId.ShouldBe(_absentId);
        (await Context.ReplacementRequests.AsNoTracking().SingleAsync(r => r.Id == row)).ReversedAtUtc.ShouldBeNull();
    }

    private Task EndTheAbsenceAsync()
        => Context.Break
            .Where(b => b.ClientId == _absentId && b.AnalyseToken == null && b.ParentWorkId == null)
            .ExecuteUpdateAsync(set => set.SetProperty(b => b.IsDeleted, true));

    private async Task<(Guid Row, Guid WorkId)> AcceptTheMoveAsync()
    {
        var outcome = await CoverAsync();
        outcome.Covered.ShouldHaveSingleItem().Kind.ShouldBe(ReplacementRequestKind.Move);
        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new AcceptAnalyseScenarioCommand(outcome.ScenarioId), CancellationToken.None);
        }

        var row = await Context.ReplacementRequests.AsNoTracking().SingleAsync(r => r.AnalyseToken == outcome.Token);
        return (row.Id, row.WorkId!.Value);
    }

    private async Task ReverseAsync(Guid row)
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ReverseMoveCommand(row), CancellationToken.None);
    }

    private async Task<Guid> AddAlternativeAsync()
    {
        var alternative = Guid.NewGuid();
        var rule = await Context.SchedulingRules.AsNoTracking().FirstAsync(r => r.Name == Prefix + "RULE");
        AddEmployee(alternative, "Alternative", rule.Id, AbsentRate);
        await Context.SaveChangesAsync();
        return alternative;
    }

    private async Task<Klacks.Api.Application.DTOs.Schedules.CoverAbsenceOutcome> CoverOnScenarioAsync(DateOnly day, Guid sourceToken)
    {
        using var scope = CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new CoverAbsenceCommand(_absentId, day, _groupId, Guid.Parse(AbsenceGuid), AnalyseToken: sourceToken), CancellationToken.None);
        _scenarioTokens.Add(outcome.Token);
        return outcome;
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task AHop_ReturnsToTheOldStandIn_OnlyWhenThatPersonIsFree(bool holdsASecondService, bool returns)
    {
        var day = NextTuesdayInTheRecordableWindow();
        await SeedSwapPeopleAsync();
        var coverShiftId = await AddSwapShiftAsync(WorkingMacro, new TimeOnly(8, 0), new TimeOnly(16, 0));
        var hopShiftId = await AddSwapShiftAsync(WorkingMacro, new TimeOnly(14, 0), new TimeOnly(22, 0));
        var secondShiftId = await AddSwapShiftAsync(WorkingMacro, new TimeOnly(17, 0), new TimeOnly(20, 0));
        await BlacklistAsync(_recipientId, coverShiftId);
        await AddSwapWorksAsync(day, coverShiftId, hopShiftId);

        var outcome = await CoverAsync(day);
        var proposal = outcome.Covered.ShouldHaveSingleItem();
        (await SwapWorkAsync(outcome.Token, hopShiftId, day)).ClientId.ShouldBe(_recipientId);

        var alternative = Guid.NewGuid();
        var rule = await Context.SchedulingRules.AsNoTracking().FirstAsync(r => r.Name == Prefix + "RULE");
        AddEmployee(alternative, "Alternative", rule.Id, AbsentRate);
        if (holdsASecondService)
        {
            Context.Work.Add(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = _standInId,
                ShiftId = secondShiftId,
                CurrentDate = day,
                StartTime = new TimeOnly(17, 0),
                EndTime = new TimeOnly(20, 0),
                WorkTime = 3m,
                AnalyseToken = outcome.Token,
            });
        }

        await Context.SaveChangesAsync();
        await AcceptAlternativeAsync(outcome.Token, proposal, alternative, day);

        var hop = await SwapWorkAsync(outcome.Token, hopShiftId, day);
        hop.ClientId.ShouldBe(returns ? _standInId : _recipientId);
        hop.MovedFromClientId.ShouldBe(returns ? (Guid?)null : _standInId);
    }

    [Test]
    public async Task AMarkerOfAnEarlierAccept_ThroughAScenarioChain_IsNotAMoveOfThisScenario()
    {
        await SeedAsync(WorkingMacro);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _scenarioTokens.Add(first);
        _scenarioTokens.Add(second);
        var accepted = NewMovedWork(null, null);
        var inFirst = NewMovedWork(first, accepted.Id);
        var inSecond = NewMovedWork(second, inFirst.Id);
        var unsettled = NewMovedWork(second, null);
        Context.Work.AddRange(accepted, inFirst, inSecond, unsettled);
        await Context.SaveChangesAsync();

        using var scope = CreateScope();
        var moved = await scope.ServiceProvider.GetRequiredService<IWorkRepository>()
            .ListMovedByScenarioAsync(second, _absentId, Day, CancellationToken.None);

        moved.ShouldHaveSingleItem().Id.ShouldBe(unsettled.Id);
    }

    [Test]
    public async Task AReplacementOfAnEarlierAccept_ThroughAScenarioChain_IsNotARelocationOfThisScenario()
    {
        await SeedAsync(WorkingMacro);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _scenarioTokens.Add(first);
        _scenarioTokens.Add(second);
        var accepted = NewOwnedWork(null, null);
        var inFirst = NewOwnedWork(first, accepted.Id);
        var inSecond = NewOwnedWork(second, inFirst.Id);
        var unsettled = NewOwnedWork(second, null);
        Context.Work.AddRange(accepted, inFirst, inSecond, unsettled);
        Context.WorkChange.AddRange(
            NewRelocation(accepted.Id, null), NewRelocation(inFirst.Id, first),
            NewRelocation(inSecond.Id, second), NewRelocation(unsettled.Id, second));
        await Context.SaveChangesAsync();

        using var scope = CreateScope();
        var written = await scope.ServiceProvider.GetRequiredService<IWorkChangeRepository>()
            .ListReplacementsWrittenByScenarioAsync(second, _standInId, Day, CancellationToken.None);

        written.ShouldHaveSingleItem().WorkId.ShouldBe(unsettled.Id);
    }

    [Test]
    public async Task AMarkerOfAnEarlierAccept_IsNotAMoveOfThisScenario()
    {
        await SeedAsync(WorkingMacro);
        var token = Guid.NewGuid();
        _scenarioTokens.Add(token);
        var openSource = Guid.NewGuid();
        _scenarioTokens.Add(openSource);
        var accepted = NewMovedWork(null, null);
        var inherited = NewMovedWork(token, accepted.Id);
        var own = NewMovedWork(token, null);
        var sourceMove = NewMovedWork(openSource, null);
        var followUp = NewMovedWork(token, sourceMove.Id);
        Context.Work.AddRange(accepted, inherited, own, sourceMove, followUp);
        await Context.SaveChangesAsync();

        using var scope = CreateScope();
        var moved = await scope.ServiceProvider.GetRequiredService<IWorkRepository>()
            .ListMovedByScenarioAsync(token, _absentId, Day, CancellationToken.None);

        moved.Select(work => work.Id).ShouldBe(new[] { own.Id, followUp.Id }, ignoreOrder: true);
    }

    [Test]
    public async Task AnInheritedReplacement_IsNotARelocationOfThisScenario()
    {
        await SeedAsync(WorkingMacro);
        var token = Guid.NewGuid();
        _scenarioTokens.Add(token);
        var openSource = Guid.NewGuid();
        _scenarioTokens.Add(openSource);
        var accepted = NewOwnedWork(null, null);
        var inherited = NewOwnedWork(token, accepted.Id);
        var own = NewOwnedWork(token, null);
        var sourceWork = NewOwnedWork(openSource, null);
        var followUp = NewOwnedWork(token, sourceWork.Id);
        Context.Work.AddRange(accepted, inherited, own, sourceWork, followUp);
        Context.WorkChange.AddRange(
            NewRelocation(accepted.Id, null), NewRelocation(inherited.Id, token), NewRelocation(own.Id, token),
            NewRelocation(sourceWork.Id, openSource), NewRelocation(followUp.Id, token));
        await Context.SaveChangesAsync();

        using var scope = CreateScope();
        var written = await scope.ServiceProvider.GetRequiredService<IWorkChangeRepository>()
            .ListReplacementsWrittenByScenarioAsync(token, _standInId, Day, CancellationToken.None);

        written.Select(change => change.WorkId).ShouldBe(new[] { own.Id, followUp.Id }, ignoreOrder: true);
    }

    [TestCase(8, 16, 12, 18, false)]
    [TestCase(8, 16, 16, 18, true)]
    [TestCase(22, 6, 1, 3, false)]
    public async Task IsFree_SeesEveryWorkOfThePerson(int workStart, int workEnd, int windowStart, int windowEnd, bool free)
    {
        await SeedAsync(WorkingMacro);
        var token = Guid.NewGuid();
        _scenarioTokens.Add(token);
        var work = NewOwnedWork(token, null);
        work.StartTime = new TimeOnly(workStart, 0);
        work.EndTime = new TimeOnly(workEnd, 0);
        Context.Work.Add(work);
        await Context.SaveChangesAsync();
        var windowDay = workEnd < workStart ? Day.AddDays(1) : Day;

        (await IsFreeAsync(token, windowDay, windowStart, windowEnd)).ShouldBe(free);
    }

    [TestCase(WorkChangeType.CorrectionEnd, 16, 17, 16, 18, false)]
    [TestCase(WorkChangeType.CorrectionStart, 7, 8, 5, 8, false)]
    [TestCase(WorkChangeType.CorrectionEnd, 16, 17, 17, 18, true)]
    public async Task IsFree_CountsTheLeadAndTrailOfAHeldWork(
        WorkChangeType padding, int paddingStart, int paddingEnd, int windowStart, int windowEnd, bool free)
    {
        await SeedAsync(WorkingMacro);
        var token = Guid.NewGuid();
        _scenarioTokens.Add(token);
        var held = NewOwnedWork(token, null);
        Context.Work.Add(held);
        Context.WorkChange.Add(NewPadding(held.Id, padding, paddingStart, paddingEnd, token));
        await Context.SaveChangesAsync();

        (await IsFreeAsync(token, Day, windowStart, windowEnd)).ShouldBe(free);
    }

    [TestCase(WorkChangeType.CorrectionEnd, 18, 19, false)]
    [TestCase(WorkChangeType.CorrectionStart, 15, 17, false)]
    public async Task IsFree_CountsTheLeadAndTrailOfTheWorkItTakes(
        WorkChangeType padding, int paddingStart, int paddingEnd, bool free)
    {
        await SeedAsync(WorkingMacro);
        var token = Guid.NewGuid();
        _scenarioTokens.Add(token);
        var held = NewOwnedWork(token, null);
        held.StartTime = new TimeOnly(8, 0);
        held.EndTime = new TimeOnly(16, 0);
        var late = NewProbe(token, 17, 18);
        var early = NewOwnedWork(token, null);
        early.StartTime = new TimeOnly(18, 30);
        early.EndTime = new TimeOnly(20, 0);
        Context.Work.AddRange(padding == WorkChangeType.CorrectionEnd ? early : held, late);
        Context.WorkChange.Add(NewPadding(late.Id, padding, paddingStart, paddingEnd, token));
        await Context.SaveChangesAsync();

        using var scope = CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ICoverMoveFactsReader>()
                .IsFreeAsync(token, _standInId, late.Id, CancellationToken.None))
            .ShouldBe(free);
    }

    [Test]
    public async Task IsFree_SeesARealWorkWithoutAClone_NotOneTheScenarioMovedAway()
    {
        await SeedAsync(WorkingMacro);
        var token = Guid.NewGuid();
        _scenarioTokens.Add(token);
        var cloned = NewOwnedWork(null, null);
        var clone = NewOwnedWork(token, cloned.Id);
        clone.ClientId = _absentId;
        Context.Work.AddRange(cloned, clone);
        await Context.SaveChangesAsync();
        (await IsFreeAsync(token, Day, 8, 16)).ShouldBeTrue();

        Context.Work.Add(NewOwnedWork(null, null));
        await Context.SaveChangesAsync();

        (await IsFreeAsync(token, Day, 8, 16)).ShouldBeFalse();
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task IsFree_ABlockingAbsenceBlocks_AnOnCallOneDoesNot(bool onCall, bool free)
    {
        await SeedAsync(WorkingMacro);
        var token = Guid.NewGuid();
        _scenarioTokens.Add(token);
        var absence = await Context.Absence.SingleAsync(a => a.Id == Guid.Parse(AbsenceGuid));
        absence.IsOnCall = onCall;
        Context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _standInId,
            AbsenceId = absence.Id,
            CurrentDate = Day,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(0, 0),
            AnalyseToken = token,
        });
        await Context.SaveChangesAsync();

        (await IsFreeAsync(token, Day, 8, 16)).ShouldBe(free);
    }

    private async Task<bool> IsFreeAsync(Guid token, DateOnly day, int startHour, int endHour)
    {
        var probe = NewProbe(token, startHour, endHour);
        probe.CurrentDate = day;
        Context.Work.Add(probe);
        await Context.SaveChangesAsync();

        using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICoverMoveFactsReader>()
            .IsFreeAsync(token, _standInId, probe.Id, CancellationToken.None);
    }

    private Work NewProbe(Guid token, int startHour, int endHour)
    {
        var probe = NewOwnedWork(token, null);
        probe.ClientId = _absentId;
        probe.StartTime = new TimeOnly(startHour, 0);
        probe.EndTime = new TimeOnly(endHour, 0);
        return probe;
    }

    private static WorkChange NewPadding(Guid workId, WorkChangeType type, int startHour, int endHour, Guid token) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = workId,
        Type = type,
        StartTime = new TimeOnly(startHour, 0),
        EndTime = new TimeOnly(endHour, 0),
        ChangeTime = endHour - startHour,
        AnalyseToken = token,
    };

    private Work NewMovedWork(Guid? token, Guid? clonedFrom)
    {
        var work = NewOwnedWork(token, clonedFrom);
        work.MovedFromClientId = _absentId;
        return work;
    }

    private Work NewOwnedWork(Guid? token, Guid? clonedFrom) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _standInId,
        ShiftId = _shiftId,
        CurrentDate = Day,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        WorkTime = 8m,
        AnalyseToken = token,
        ClonedFromWorkId = clonedFrom,
    };

    private WorkChange NewRelocation(Guid workId, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = workId,
        Type = WorkChangeType.ReplacementWithin,
        ReplaceClientId = _absentId,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        Description = RecoveryMarkers.WorkChangeSource,
        AnalyseToken = token,
    };

    private async Task AcceptAlternativeAsync(
        Guid token, Klacks.Api.Application.DTOs.Schedules.CoveredSlot proposal, Guid alternative, DateOnly day)
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new RecordReplacementContactCommand(new RecordReplacementContactRequest(
                _absentId, alternative, proposal.ShiftId, day, proposal.StartTime!.Value, proposal.EndTime!.Value,
                _groupId, Guid.Parse(AbsenceGuid), token, ReplacementRequestOutcome.Accepted)),
            CancellationToken.None);
    }

    private static DateOnly NextTuesdayInTheRecordableWindow()
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        return day.AddDays(((int)DayOfWeek.Tuesday - (int)day.DayOfWeek + 7) % 7);
    }

    [Test]
    public async Task AStartedWorkNobodyCovers_StandsAfterADialogAccept_WithTheRedNotice()
    {
        await SeedAsync(WorkingMacro, StartedDay);
        await BlacklistAsync(_standInId, _shiftId);

        var outcome = await CoverAsync(StartedDay, ReplacementRequestSource.PlannerDialog);
        outcome.Covered.ShouldBeEmpty();

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new AcceptAnalyseScenarioCommand(outcome.ScenarioId), CancellationToken.None);
        }

        (await Context.Work.AsNoTracking().CountAsync(w => w.AnalyseToken == null && w.CurrentDate == StartedDay
            && w.ClientId == _absentId && w.ParentWorkId == null)).ShouldBe(1, "a started work never falls on its own");
        (await FindingsAsync(StartedDay, null)).ShouldContain(ScheduleValidationKeys.UncoveredAbsence);
    }

    [Test]
    public async Task AMovedWork_StaysFixed_ThroughAFollowUpScenario_AndAfterAccept()
    {
        await SeedAsync(WorkingMacro);
        var outcome = await CoverAsync();
        var moved = await Context.Work.AsNoTracking().SingleAsync(w => w.AnalyseToken == outcome.Token && w.ClonedFromWorkId == _workId);
        (await FixedWorksAsync(outcome.Token)).ShouldContain(moved.Id);

        var followUp = Guid.NewGuid();
        _scenarioTokens.Add(followUp);
        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Klacks.Api.Application.Interfaces.IAnalyseScenarioService>()
                .CloneScenarioDataWithMapsAsync(_groupId, Day, Day, followUp, null, outcome.Token, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<Klacks.Api.Domain.Interfaces.IUnitOfWork>().CompleteAsync();
        }

        var clone = await Context.Work.AsNoTracking().SingleAsync(w => w.AnalyseToken == followUp && w.ClientId == _standInId && w.ParentWorkId == null);
        clone.MovedFromClientId.ShouldBe(_absentId);
        (await FixedWorksAsync(followUp)).ShouldContain(clone.Id);

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new AcceptAnalyseScenarioCommand(outcome.ScenarioId), CancellationToken.None);
        }

        var real = await Context.Work.AsNoTracking().SingleAsync(w =>
            w.AnalyseToken == null && w.CurrentDate == Day && w.ClientId == _standInId && w.ParentWorkId == null);
        real.MovedFromClientId.ShouldBe(_absentId);
        (await FixedWorksAsync(null)).ShouldContain(real.Id);
    }

    private Task<HashSet<Guid>> FixedWorksAsync(Guid? token)
        => Klacks.Api.Infrastructure.Services.Schedules.CorrectedWorkQuery.LoadFixedWorkIdsAsync(
            Context, token, Day, Day, CancellationToken.None);

    [Test]
    public async Task ADirectAbsence_TakesTheWorkAway_AnOnCallOneDoesNot()
    {
        await SeedAsync(WorkingMacro);
        using var scope = CreateScope();
        var groups = Substitute.For<ISelectedGroupContextResolver>();
        groups.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { _groupId });
        var removal = ActivatorUtilities.CreateInstance<AbsenceWorkRemovalService>(scope.ServiceProvider, groups);
        var onCallId = Guid.Parse(UnpaidAbsenceGuid);
        await Context.Absence.Where(a => a.Id == onCallId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsOnCall, true));
        var day = new AbsenceSpan(_absentId, onCallId, Day, new TimeOnly(0, 0), new TimeOnly(23, 59));

        (await removal.FindAsync([day], null, CancellationToken.None)).ShouldBeEmpty();
        var sick = day with { AbsenceId = Guid.Parse(AbsenceGuid) };
        (await removal.FindAsync([sick], null, CancellationToken.None)).ShouldHaveSingleItem().WorkId.ShouldBe(_workId);

        var taken = await removal.FindAsync([sick], null, CancellationToken.None);
        await removal.WriteAndRemoveAsync(taken, () => Task.CompletedTask, CancellationToken.None);

        (await Context.Work.AsNoTracking().CountAsync(w => w.Id == _workId || w.Id == _subWorkId)).ShouldBe(0);
    }

    [Test]
    public async Task AMacroThatCannotRun_KeepsTheWorkWithItsOwner_AsAReplacement()
    {
        await SeedAsync(BrokenMacro);

        var outcome = await CoverAsync();

        var covered = outcome.Covered.ShouldHaveSingleItem();
        covered.Kind.ShouldBe(ReplacementRequestKind.Replacement);
        var token = outcome.Token;

        var work = await Context.Work.AsNoTracking().SingleAsync(w => w.AnalyseToken == token && w.ClonedFromWorkId == _workId);
        work.ClientId.ShouldBe(_absentId);
        work.MovedFromClientId.ShouldBeNull();
        work.Surcharges.ShouldBe(StaleSurcharges);

        var subWork = await Context.Work.AsNoTracking().SingleAsync(w => w.AnalyseToken == token && w.ClonedFromWorkId == _subWorkId);
        subWork.ClientId.ShouldBe(_absentId);
        (await Context.Break.AsNoTracking().SingleAsync(b => b.AnalyseToken == token && b.ParentWorkId == work.Id))
            .ClientId.ShouldBe(_absentId);

        (await Context.WorkChange.AsNoTracking().CountAsync(wc =>
            wc.AnalyseToken == token && wc.WorkId == work.Id && wc.ReplaceClientId == _standInId)).ShouldBe(1);

        var bookRow = await Context.ReplacementRequests.AsNoTracking().SingleAsync(r => r.AnalyseToken == token);
        bookRow.Kind.ShouldBe(ReplacementRequestKind.Replacement);
    }

    [Test]
    public async Task ASwapWhoseHopFallsBack_LeavesThreePeopleConsistent_WithTheFindingsOfAMovedSwap()
    {
        await SeedSwapPeopleAsync();
        var coverShiftId = await AddSwapShiftAsync(WorkingMacro, new TimeOnly(8, 0), new TimeOnly(16, 0));
        var brokenHopShiftId = await AddSwapShiftAsync(BrokenMacro, new TimeOnly(14, 0), new TimeOnly(22, 0));
        var workingHopShiftId = await AddSwapShiftAsync(WorkingMacro, new TimeOnly(14, 0), new TimeOnly(22, 0));
        await BlacklistAsync(_recipientId, coverShiftId);
        await AddSwapWorksAsync(Day, coverShiftId, brokenHopShiftId);
        await AddSwapWorksAsync(SecondDay, coverShiftId, workingHopShiftId);

        var mixed = await CoverAsync(Day);
        var moved = await CoverAsync(SecondDay);

        mixed.Covered.ShouldHaveSingleItem().Kind.ShouldBe(ReplacementRequestKind.Move);
        moved.Covered.ShouldHaveSingleItem().Kind.ShouldBe(ReplacementRequestKind.Move);

        var cover = await SwapWorkAsync(mixed.Token, coverShiftId, Day);
        cover.ClientId.ShouldBe(_standInId);
        cover.MovedFromClientId.ShouldBe(_absentId);
        cover.Surcharges.ShouldBe(8m * StandInRate);

        var hop = await SwapWorkAsync(mixed.Token, brokenHopShiftId, Day);
        hop.ClientId.ShouldBe(_standInId, "the hop fell back, so the work stays with its owner");
        hop.MovedFromClientId.ShouldBeNull();
        hop.Surcharges.ShouldBe(StaleSurcharges);
        (await Context.WorkChange.AsNoTracking().CountAsync(wc =>
            wc.AnalyseToken == mixed.Token && wc.WorkId == hop.Id && wc.ReplaceClientId == _recipientId)).ShouldBe(1);

        var movedHop = await SwapWorkAsync(moved.Token, workingHopShiftId, SecondDay);
        movedHop.ClientId.ShouldBe(_recipientId);
        movedHop.MovedFromClientId.ShouldBe(_standInId);

        (await PeriodHoursAsync(_absentId, mixed.Token)).Hours.ShouldBe(8m);
        (await PeriodHoursAsync(_standInId, mixed.Token)).Hours.ShouldBe(8m, "the moved work; the own work is handed over");
        (await PeriodHoursAsync(_recipientId, mixed.Token)).Hours.ShouldBe(8m, "the replacement");
        foreach (var person in new[] { _absentId, _standInId, _recipientId })
        {
            (await PeriodHoursAsync(person, moved.Token)).Hours.ShouldBe(
                (await PeriodHoursAsync(person, mixed.Token)).Hours, "the fallback must not change the hours of anyone");
        }

        var mixedFindings = await FindingsAsync(Day, mixed.Token);
        var movedFindings = await FindingsAsync(SecondDay, moved.Token);
        mixedFindings.ShouldBe(movedFindings, ignoreOrder: true);
        mixedFindings.ShouldNotContain(ScheduleValidationKeys.UncoveredAbsence);
    }

    private async Task<Work> SwapWorkAsync(Guid token, Guid sourceShiftId, DateOnly day)
    {
        var cloneShiftIds = await Context.Shift.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.AnalyseToken == token && s.ScenarioSourceShiftId == sourceShiftId)
            .Select(s => s.Id)
            .ToListAsync();
        return await Context.Work.AsNoTracking()
            .SingleAsync(w => w.AnalyseToken == token && w.CurrentDate == day && cloneShiftIds.Contains(w.ShiftId));
    }

    /// <summary>
    /// The live single check of the three people on the scenario day, as the planner's error list receives it. A collision
    /// counts as a finding of its own.
    /// </summary>
    private async Task<List<string>> FindingsAsync(DateOnly day, Guid? token)
    {
        var findings = new List<string>();
        foreach (var person in new[] { _absentId, _standInId, _recipientId })
        {
            using var scope = CreateScope();
            var notifications = Substitute.For<IWorkNotificationService>();
            notifications.When(n => n.NotifyCollisionsDetected(Arg.Any<CollisionListNotificationDto>()))
                .Do(call => findings.AddRange(
                    call.Arg<CollisionListNotificationDto>().Collisions.Select(_ => CollisionFinding)));
            notifications.When(n => n.NotifyScheduleValidationsDetected(Arg.Any<ScheduleValidationListNotificationDto>()))
                .Do(call => findings.AddRange(
                    call.Arg<ScheduleValidationListNotificationDto>().Entries.Select(entry => entry.Comment)));
            var check = ActivatorUtilities.CreateInstance<ScheduleTimelineSingleCheck>(scope.ServiceProvider, notifications);
            await check.RunAsync(person, day, token, CancellationToken.None);
        }

        return findings;
    }

    private async Task SeedSwapPeopleAsync()
    {
        _groupId = Guid.NewGuid();
        _absentId = Guid.NewGuid();
        _standInId = Guid.NewGuid();
        _recipientId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();

        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = ValidFrom });
        Context.SchedulingRules.Add(new SchedulingRule { Id = ruleId, Name = Prefix + "RULE", MinPauseHours = 1m });
        AddEmployee(_absentId, "Absent", ruleId, AbsentRate);
        AddEmployee(_standInId, "StandIn", ruleId, StandInRate);
        AddEmployee(_recipientId, "Recipient", ruleId, AbsentRate);
        Context.Absence.AddRange(NewAbsence(AbsenceGuid, "ITA", false), NewAbsence(UnpaidAbsenceGuid, "ITU", true));
        await Context.SaveChangesAsync();
    }

    private async Task<Guid> AddSwapShiftAsync(string macroContent, TimeOnly start, TimeOnly end)
    {
        var macroId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        Context.Set<Macro>().Add(new Macro { Id = macroId, Name = Prefix + "MACRO", Type = 0, Content = macroContent });
        Context.Shift.Add(NewShift(shiftId, macroId, start, end));
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = _groupId, ValidFrom = ValidFrom });
        await Context.SaveChangesAsync();
        return shiftId;
    }

    private async Task BlacklistAsync(Guid clientId, Guid shiftId)
    {
        Context.ClientShiftPreference.Add(new ClientShiftPreference
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            PreferenceType = ShiftPreferenceType.Blacklist,
        });
        await Context.SaveChangesAsync();
    }

    private async Task AddSwapWorksAsync(DateOnly day, Guid coverShiftId, Guid hopShiftId)
    {
        Context.Work.AddRange(
            new Work
            {
                Id = Guid.NewGuid(),
                ClientId = _absentId,
                ShiftId = coverShiftId,
                CurrentDate = day,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(16, 0),
                WorkTime = 8m,
                Surcharges = StaleSurcharges,
            },
            new Work
            {
                Id = Guid.NewGuid(),
                ClientId = _standInId,
                ShiftId = hopShiftId,
                CurrentDate = day,
                StartTime = new TimeOnly(14, 0),
                EndTime = new TimeOnly(22, 0),
                WorkTime = 8m,
                Surcharges = StaleSurcharges,
            });
        await Context.SaveChangesAsync();
    }

    private Task<Klacks.Api.Application.DTOs.Schedules.CoverAbsenceOutcome> CoverAsync() => CoverAsync(Day);

    private async Task<Klacks.Api.Application.DTOs.Schedules.CoverAbsenceOutcome> CoverAsync(
        DateOnly day, ReplacementRequestSource source = ReplacementRequestSource.RecoveryEngine)
    {
        using var scope = CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var outcome = await mediator.Send(
            new CoverAbsenceCommand(_absentId, day, _groupId, Guid.Parse(AbsenceGuid), Source: source), CancellationToken.None);
        _scenarioTokens.Add(outcome.Token);
        return outcome;
    }

    private async Task<ClientPeriodHours> PeriodHoursAsync(Guid clientId, Guid? token)
        => await Context.ClientPeriodHours.AsNoTracking().SingleAsync(p => p.ClientId == clientId && p.AnalyseToken == token);

    private async Task SeedAsync(string macroContent, DateOnly? day = null)
    {
        _seedDay = day ?? Day;
        _groupId = Guid.NewGuid();
        _absentId = Guid.NewGuid();
        _standInId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _shiftId = shiftId;
        var macroId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        _workId = Guid.NewGuid();
        _subWorkId = Guid.NewGuid();
        _subBreakId = Guid.NewGuid();
        _correctionId = Guid.NewGuid();
        _expenseId = Guid.NewGuid();

        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = ValidFrom });
        Context.SchedulingRules.Add(new SchedulingRule { Id = ruleId, Name = Prefix + "RULE", MinPauseHours = 1m });
        Context.Set<Macro>().Add(new Macro { Id = macroId, Name = Prefix + "MACRO", Type = 0, Content = macroContent });
        AddEmployee(_absentId, "Absent", ruleId, AbsentRate);
        AddEmployee(_standInId, "StandIn", ruleId, StandInRate);

        Context.Shift.Add(NewShift(shiftId, macroId, new TimeOnly(8, 0), new TimeOnly(16, 0)));
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = _groupId, ValidFrom = ValidFrom });

        Context.Absence.AddRange(NewAbsence(AbsenceGuid, "ITA", false), NewAbsence(UnpaidAbsenceGuid, "ITU", true));

        Context.Work.Add(new Work
        {
            Id = _workId,
            ClientId = _absentId,
            ShiftId = shiftId,
            CurrentDate = _seedDay,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            Surcharges = StaleSurcharges,
        });
        Context.Work.Add(new Work
        {
            Id = _subWorkId,
            ParentWorkId = _workId,
            ClientId = _absentId,
            ShiftId = shiftId,
            CurrentDate = _seedDay,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            WorkTime = 1m,
            Surcharges = StaleSurcharges,
        });
        Context.Break.Add(new Break
        {
            Id = _subBreakId,
            ParentWorkId = _workId,
            ClientId = _absentId,
            AbsenceId = Guid.Parse(UnpaidAbsenceGuid),
            CurrentDate = _seedDay,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            WorkTime = -1m,
            Surcharges = StaleSurcharges,
        });
        Context.WorkChange.Add(new WorkChange
        {
            Id = _correctionId,
            WorkId = _workId,
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = 1m,
            Surcharges = StaleSurcharges,
            Description = Prefix,
        });
        Context.Expenses.Add(new Expenses { Id = _expenseId, WorkId = _workId, Amount = 5m, Description = Prefix });

        await Context.SaveChangesAsync();
    }

    private static Shift NewShift(Guid shiftId, Guid macroId, TimeOnly start, TimeOnly end) => new()
    {
        Id = shiftId,
        Name = Prefix + "SHIFT",
        Abbreviation = "ITCME",
        StartShift = start,
        EndShift = end,
        WorkTime = 8m,
        FromDate = new DateOnly(2090, 1, 1),
        IsMonday = true,
        IsTuesday = true,
        IsWednesday = true,
        IsThursday = true,
        IsFriday = true,
        IsSaturday = true,
        IsSunday = true,
        Quantity = 1,
        Status = ShiftStatus.OriginalOrder,
        MacroId = macroId,
    };

    private void AddEmployee(Guid clientId, string name, Guid ruleId, decimal nightRate)
    {
        var contractId = Guid.NewGuid();
        Context.Client.Add(new Client { Id = clientId, FirstName = Prefix, Name = name, Type = EntityTypeEnum.Employee });
        Context.Contract.Add(new Contract
        {
            Id = contractId,
            Name = Prefix + name,
            ValidFrom = ValidFrom,
            GuaranteedHours = 100m,
            MaximumHours = 200m,
            FullTime = 40m,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
            SchedulingRuleId = ruleId,
            NightRate = nightRate,
        });
        Context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contractId,
            FromDate = new DateOnly(2090, 1, 1),
            IsActive = true,
        });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = _groupId, ValidFrom = ValidFrom });
    }

    private static Absence NewAbsence(string id, string abbreviation, bool isUnpaid)
    {
        var name = new Klacks.Api.Domain.Common.MultiLanguage();
        name.SetValue("en", Prefix + abbreviation);
        var shortName = new Klacks.Api.Domain.Common.MultiLanguage();
        shortName.SetValue("en", abbreviation);
        var description = new Klacks.Api.Domain.Common.MultiLanguage();
        description.SetValue("en", Prefix);
        return new Absence
        {
            Id = Guid.Parse(id),
            Name = name,
            Abbreviation = shortName,
            Description = description,
            Color = "#888888",
            IsUnpaid = isUnpaid,
        };
    }

    private async Task PurgeScenarioTokenAsync(Guid token)
    {
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM client_shift_preference WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM expenses WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE analyse_token = {token})");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM break WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work WHERE analyse_token = {token} AND parent_work_id IS NOT NULL");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM replacement_requests WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM client_period_hours WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM group_item WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM shift WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM analyse_scenarios WHERE token = {token}");
    }

    private async Task PurgeAsync()
    {
        var sql = $@"
            DELETE FROM expenses WHERE description LIKE '{Prefix}%';
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%'));
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%') AND parent_work_id IS NOT NULL;
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM client_period_hours WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM replacement_requests WHERE absent_client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM analyse_scenarios WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM client_shift_preference WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM shift WHERE name LIKE '{Prefix}%';
            DELETE FROM macro WHERE name LIKE '{Prefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%';
            DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM contract WHERE name LIKE '{Prefix}%';
            DELETE FROM scheduling_rules WHERE name LIKE '{Prefix}%';
            DELETE FROM break WHERE absence_id IN ('{AbsenceGuid}', '{UnpaidAbsenceGuid}');
            DELETE FROM absence WHERE id IN ('{AbsenceGuid}', '{UnpaidAbsenceGuid}');
            DELETE FROM client WHERE first_name LIKE '{Prefix}%';
        ";
        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
