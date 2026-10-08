// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// PostgreSQL proof that the payroll completeness gate's queries translate and decide correctly on Npgsql: a person
/// with a group seal and an active membership is complete, a membership day no seal locks raises DayNotLocked, a
/// global seal locks a person without a group (and without one the blocker asks for a global close), a day approval
/// never counts, a work entry below Closed raises EntryNotClosed with the shift's group, and an export of an
/// overlapping period raises OverlappingExport. Every query is restricted to the test's own persons so real plan data
/// cannot influence the result.
/// </summary>
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Klacks.Api.Infrastructure.Services.Exports;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Exports;

[TestFixture]
[Category("RealDatabase")]
public class PayrollCompletenessGatePostgresTests : PayrollPostgresFixtureBase
{
    private PayrollCompletenessGate _gate = null!;

    [SetUp]
    public void SetUpGate()
    {
        _gate = new PayrollCompletenessGate(Context, new ExportLogItemRepository(Context));
    }

    [Test]
    public async Task MemberWithGroupSealOnEveryDay_IsComplete()
    {
        var person = AddPerson("Complete", GroupId);
        AddWork(person, Day1, 8m, WorkLockLevel.Closed);
        SealPeriod(GroupId);
        await Context.SaveChangesAsync();

        var result = await _gate.CheckAsync(Day1, Day3, [person]);

        result.IsComplete.ShouldBeTrue();
        result.Blockers.ShouldBeEmpty();
    }

    [Test]
    public async Task MembershipDayWithoutSeal_RaisesDayNotLocked_WithTheGroup()
    {
        var person = AddPerson("OpenDay", GroupId);
        AddWork(person, Day1, 8m, WorkLockLevel.Closed);
        AddSeal(Day1, GroupId);
        AddSeal(Day2, GroupId);
        await Context.SaveChangesAsync();

        var result = await _gate.CheckAsync(Day1, Day3, [person]);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.ClientId.ShouldBe(person);
        blocker.Date.ShouldBe(Day3);
        blocker.GroupId.ShouldBe(GroupId);
        blocker.RequiresGlobalClose.ShouldBeFalse();
    }

    [Test]
    public async Task GlobalSeal_LocksAPersonWithoutAGroup()
    {
        var person = AddPerson("Global");
        AddWork(person, Day1, 8m, WorkLockLevel.Closed);
        AddSeal(Day1, null);
        await Context.SaveChangesAsync();

        var result = await _gate.CheckAsync(Day1, Day3, [person]);

        result.IsComplete.ShouldBeTrue();
    }

    [Test]
    public async Task WorkOfAGroupedShiftWithoutSeal_RaisesDayNotLocked_WithTheShiftsGroup()
    {
        var person = AddPerson("NoSeal");
        AddWork(person, Day2, 8m, WorkLockLevel.Closed);
        await Context.SaveChangesAsync();

        var result = await _gate.CheckAsync(Day1, Day3, [person]);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.Date.ShouldBe(Day2);
        blocker.GroupId.ShouldBe(GroupId);
        blocker.RequiresGlobalClose.ShouldBeFalse();
    }

    [Test]
    public async Task WorkOfAnUngroupedShiftWithoutSeal_NeedsAGlobalClose()
    {
        var person = AddPerson("Ungrouped");
        var ungroupedShift = AddUngroupedShift();
        AddWork(person, Day2, 8m, WorkLockLevel.Closed, ungroupedShift);
        await Context.SaveChangesAsync();

        var result = await _gate.CheckAsync(Day1, Day3, [person]);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.GroupId.ShouldBeNull();
        blocker.RequiresGlobalClose.ShouldBeTrue();
    }

    [Test]
    public async Task DayApproval_NeverCountsAsAClosedSeal()
    {
        var person = AddPerson("Approved", GroupId);
        AddWork(person, Day1, 8m, WorkLockLevel.Closed);
        AddSeal(Day1, GroupId, WorkLockLevel.Approved);
        AddSeal(Day2, GroupId);
        AddSeal(Day3, GroupId);
        await Context.SaveChangesAsync();

        var result = await _gate.CheckAsync(Day1, Day3, [person]);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.DayNotLocked);
        blocker.Date.ShouldBe(Day1);
    }

    [Test]
    public async Task WorkBelowClosed_RaisesEntryNotClosed_WithTheGroupOfTheShift()
    {
        var person = AddPerson("NotClosed", GroupId);
        AddWork(person, Day1, 8m, WorkLockLevel.Approved);
        SealPeriod(GroupId);
        await Context.SaveChangesAsync();

        var result = await _gate.CheckAsync(Day1, Day3, [person]);

        var blocker = result.Blockers.ShouldHaveSingleItem();
        blocker.Reason.ShouldBe(PayrollExportBlockReason.EntryNotClosed);
        blocker.Date.ShouldBe(Day1);
        blocker.GroupId.ShouldBe(GroupId);
        blocker.EntryCount.ShouldBe(1);
        result.BlockerTotal.ShouldBe(1);
    }

    [Test]
    public async Task ExportOfAnOverlappingPeriod_RaisesOverlappingExport_TheExactPeriodDoesNot()
    {
        var person = AddPerson("Overlap", GroupId);
        AddWork(person, Day1, 8m, WorkLockLevel.Closed);
        SealPeriod(GroupId);
        var log = new ExportLog
        {
            Id = Guid.NewGuid(),
            Format = "datev-lug-bewegungsdaten",
            StartDate = Day1,
            EndDate = Day2,
            Language = "de",
            FileName = Prefix + "earlier.csv",
            ExportedAt = DateTime.UtcNow,
            ExportedBy = Actor,
        };
        TrackExportLog(log.Id);
        Context.ExportLog.Add(log);
        Context.ExportLogItem.Add(new ExportLogItem
        {
            Id = Guid.NewGuid(),
            ExportLogId = log.Id,
            ClientId = person,
            StartDate = Day1,
            EndDate = Day2,
            Format = log.Format,
            Revision = 1,
            ContentHash = new string('a', 64),
            EntryCount = 1,
        });
        await Context.SaveChangesAsync();

        var overlapping = await _gate.CheckAsync(Day1, Day3, [person]);
        var exact = await _gate.CheckAsync(Day1, Day2, [person]);

        overlapping.Blockers.ShouldHaveSingleItem().Reason.ShouldBe(PayrollExportBlockReason.OverlappingExport);
        exact.Blockers.ShouldNotContain(b => b.Reason == PayrollExportBlockReason.OverlappingExport);
    }

    [Test]
    public async Task OnlyThePersonsOfTheSelectionAreExamined()
    {
        var complete = AddPerson("SelectedComplete", GroupId);
        var open = AddPerson("OtherOpen", GroupId);
        AddWork(complete, Day1, 8m, WorkLockLevel.Closed);
        AddWork(open, Day1, 8m, WorkLockLevel.None);
        SealPeriod(GroupId);
        await Context.SaveChangesAsync();

        var selected = await _gate.CheckAsync(Day1, Day3, [complete]);
        var both = await _gate.CheckAsync(Day1, Day3, [complete, open]);

        selected.IsComplete.ShouldBeTrue();
        both.Blockers.ShouldHaveSingleItem().ClientId.ShouldBe(open);
    }
}
