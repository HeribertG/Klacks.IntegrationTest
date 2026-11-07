// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Bulk deleting Works must not leave a stale client_period_hours row behind. Every period-hours reader prefers a cached
/// row over the live sum (PeriodHoursService.GetPeriodHoursAsync, WorkRepository.GetPeriodHoursForClients), so a row that
/// still counts the deleted Works shows too many hours after a reload. Runs against PostgreSQL (IT database klacks)
/// through WorkWriteChainStack; the state is read in a fresh stack.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class WorkBulkDeletePeriodHoursPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "BULKDELETE_";
    private const string ClientSuffix = "CLIENT";
    private const decimal MorningWorkTime = 8m;
    private const decimal EveningWorkTime = 2m;
    private const decimal StaleHours = 77m;
    private const decimal StaleSurcharges = 7m;

    private Guid _clientId;
    private Guid _morningWorkId;
    private (DateOnly Start, DateOnly End) _period;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, ClientSuffix);
        var shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);
        _morningWorkId = await WorkChainTestData.SeedWorkAsync(
            _clientId, shiftId, WorkChainTestData.MorningStart, WorkChainTestData.MorningEnd, MorningWorkTime);
        await WorkChainTestData.SeedWorkAsync(
            _clientId, shiftId, WorkChainTestData.EveningStart, WorkChainTestData.EveningEnd, EveningWorkTime);

        await using (var boundaries = new WorkWriteChainStack())
        {
            _period = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(WorkChainTestData.Day);
        }

        await WorkChainTestData.SeedPeriodHoursAsync(_clientId, _period, StaleHours, StaleSurcharges);
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task BulkDelete_LeavesNoStalePeriodHours_SoAReloadShowsOnlyTheRemainingWorks()
    {
        await using (var stack = new WorkWriteChainStack())
        {
            var response = await stack.NewBulkDeleteHandler().Handle(
                new BulkDeleteWorksCommand(new BulkDeleteWorksRequest { WorkIds = [_morningWorkId] }),
                CancellationToken.None);

            response.DeletedIds.ShouldBe([_morningWorkId]);
        }

        await using var reload = new WorkWriteChainStack();
        var shown = await reload.PeriodHoursService.GetPeriodHoursAsync([_clientId], _period.Start, _period.End);

        shown[_clientId].Hours.ShouldBe(EveningWorkTime);
        shown[_clientId].Surcharges.ShouldBe(0m);
    }
}
