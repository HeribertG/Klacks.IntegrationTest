// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The client period export reads the period hours only from the client_period_hours cache (ClientPeriodExportDataLoader,
/// no live fallback), so recalculating one pay period (PeriodHoursService.RecalculatePeriodHoursAsync) must keep the
/// client's rows of other, non-overlapping periods: a write in the following period must not make the closed earlier
/// period lose its period hours in the export. A row that overlaps the recalculated period with other boundaries is
/// still removed. Runs against PostgreSQL (IT database klacks).
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Exports;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class ClientPeriodExportPeriodHoursPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "EXPORT_";
    private const string ClientSuffix = "CLIENT";
    private const decimal ClosedWorkTime = 8m;
    private const decimal NextPeriodWorkTime = 6m;
    private const int DaysAfterThePeriodEnd = 3;

    private Guid _clientId;
    private (DateOnly Start, DateOnly End) _closedPeriod;
    private (DateOnly Start, DateOnly End) _nextPeriod;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, ClientSuffix);
        var shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);

        await using (var boundaries = new WorkWriteChainStack())
        {
            _closedPeriod = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(WorkChainTestData.Day);
            _nextPeriod = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(
                _closedPeriod.End.AddDays(DaysAfterThePeriodEnd));
        }

        await SeedWorkAsync(shiftId, WorkChainTestData.Day, ClosedWorkTime, WorkLockLevel.Closed);
        await SeedWorkAsync(shiftId, _nextPeriod.Start.AddDays(DaysAfterThePeriodEnd), NextPeriodWorkTime, WorkLockLevel.None);
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task AWriteInTheNextPeriod_KeepsThePeriodHoursOfTheClosedPeriodInTheExport()
    {
        await using (var stack = new WorkWriteChainStack())
        {
            await stack.PeriodHoursService.RecalculatePeriodHoursAsync(_clientId, _closedPeriod.Start, _closedPeriod.End);
            await stack.PeriodHoursService.RecalculatePeriodHoursAsync(_clientId, _nextPeriod.Start, _nextPeriod.End);
        }

        await using var context = WorkChainTestData.NewRetryingContext();
        var export = await new ClientPeriodExportDataLoader(context).LoadAsync(_closedPeriod.Start, _closedPeriod.End);

        var group = export.Clients.Single(c => c.ClientId == _clientId);
        group.WorkEntries.Count.ShouldBe(1);
        var periodHours = group.PeriodHours.ShouldHaveSingleItem();
        periodHours.StartDate.ShouldBe(_closedPeriod.Start);
        periodHours.EndDate.ShouldBe(_closedPeriod.End);
        periodHours.Hours.ShouldBe(ClosedWorkTime);
    }

    [Test]
    public async Task ARecalculation_StillRemovesARowThatOverlapsThePeriodWithOtherBoundaries()
    {
        var miscut = (Start: _closedPeriod.Start.AddDays(-DaysAfterThePeriodEnd), End: _closedPeriod.End.AddDays(DaysAfterThePeriodEnd));
        await WorkChainTestData.SeedPeriodHoursAsync(_clientId, miscut, ClosedWorkTime + NextPeriodWorkTime, 0m);

        await using (var stack = new WorkWriteChainStack())
        {
            await stack.PeriodHoursService.RecalculatePeriodHoursAsync(_clientId, _closedPeriod.Start, _closedPeriod.End);
        }

        var row = (await ChainStateSnapshot.ReadAsync(_clientId)).PeriodHours.ShouldHaveSingleItem();
        row.StartDate.ShouldBe(_closedPeriod.Start);
        row.EndDate.ShouldBe(_closedPeriod.End);
        row.Hours.ShouldBe(ClosedWorkTime);
    }

    private async Task SeedWorkAsync(Guid shiftId, DateOnly day, decimal workTime, WorkLockLevel lockLevel)
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            ShiftId = shiftId,
            CurrentDate = day,
            StartTime = WorkChainTestData.MorningStart,
            EndTime = WorkChainTestData.MorningEnd,
            WorkTime = workTime,
            LockLevel = lockLevel
        });
        await context.SaveChangesAsync();
    }
}
