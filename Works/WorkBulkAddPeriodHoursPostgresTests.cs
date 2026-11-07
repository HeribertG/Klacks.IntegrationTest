// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A bulk add whose Works of one client fall into two pay periods must persist one client_period_hours row per real pay
/// period, with exactly that period's boundaries and hours. The payroll export reads only these cached rows
/// (ClientPeriodExportDataLoader), so one row over the union of both periods would show the summed hours with wrong
/// boundaries in the export of each period (the export lists closed Works only, so the test closes them after the bulk
/// add). Runs against PostgreSQL (IT database klacks) through WorkWriteChainStack.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class WorkBulkAddPeriodHoursPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "BULKADD_";
    private const string ClientSuffix = "CLIENT";
    private const decimal FirstPeriodWorkTime = 8m;
    private const decimal SecondPeriodWorkTime = 6m;
    private const int DaysIntoTheSecondPeriod = 3;

    private Guid _clientId;
    private Guid _shiftId;
    private (DateOnly Start, DateOnly End) _firstPeriod;
    private (DateOnly Start, DateOnly End) _secondPeriod;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, ClientSuffix);
        _shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);

        await using var boundaries = new WorkWriteChainStack();
        _firstPeriod = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(WorkChainTestData.Day);
        _secondPeriod = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(
            _firstPeriod.End.AddDays(DaysIntoTheSecondPeriod));
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task WorksInTwoPayPeriods_PersistOneRowPerPayPeriod_WithItsOwnBoundariesAndHours()
    {
        await BulkAddOneWorkPerPeriodAsync();

        var rows = (await ChainStateSnapshot.ReadAsync(_clientId)).PeriodHours.OrderBy(p => p.StartDate).ToList();
        rows.Count.ShouldBe(2);
        rows[0].StartDate.ShouldBe(_firstPeriod.Start);
        rows[0].EndDate.ShouldBe(_firstPeriod.End);
        rows[0].Hours.ShouldBe(FirstPeriodWorkTime);
        rows[1].StartDate.ShouldBe(_secondPeriod.Start);
        rows[1].EndDate.ShouldBe(_secondPeriod.End);
        rows[1].Hours.ShouldBe(SecondPeriodWorkTime);
    }

    [Test]
    public async Task WorksInTwoPayPeriods_TheExportOfEachPeriodShowsOnlyItsOwnHours()
    {
        await BulkAddOneWorkPerPeriodAsync();
        await CloseTheClientsWorksAsync();

        await AssertExportShowsAsync(_firstPeriod, FirstPeriodWorkTime);
        await AssertExportShowsAsync(_secondPeriod, SecondPeriodWorkTime);
    }

    private async Task BulkAddOneWorkPerPeriodAsync()
    {
        await using var stack = new WorkWriteChainStack();
        var request = new BulkAddWorksRequest
        {
            PeriodStart = _firstPeriod.Start,
            PeriodEnd = _secondPeriod.End,
            Works =
            [
                NewItem(WorkChainTestData.Day, FirstPeriodWorkTime),
                NewItem(_secondPeriod.Start.AddDays(DaysIntoTheSecondPeriod), SecondPeriodWorkTime)
            ]
        };

        var response = await stack.NewBulkAddHandler().Handle(new BulkAddWorksCommand(request), CancellationToken.None);

        response.SuccessCount.ShouldBe(2);
    }

    private async Task CloseTheClientsWorksAsync()
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        var works = await context.Work.Where(w => w.ClientId == _clientId).ToListAsync();
        foreach (var work in works)
        {
            work.LockLevel = WorkLockLevel.Closed;
        }

        await context.SaveChangesAsync();
    }

    private async Task AssertExportShowsAsync((DateOnly Start, DateOnly End) period, decimal hours)
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        var export = await new ClientPeriodExportDataLoader(context).LoadAsync(period.Start, period.End);

        var periodHours = export.Clients.Single(c => c.ClientId == _clientId).PeriodHours.ShouldHaveSingleItem();
        periodHours.StartDate.ShouldBe(period.Start);
        periodHours.EndDate.ShouldBe(period.End);
        periodHours.Hours.ShouldBe(hours);
    }

    private BulkWorkItem NewItem(DateOnly day, decimal workTime) => new()
    {
        ClientId = _clientId,
        ShiftId = _shiftId,
        CurrentDate = day,
        WorkTime = workTime,
        StartTime = WorkChainTestData.MorningStart,
        EndTime = WorkChainTestData.MorningEnd
    };
}
