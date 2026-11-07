// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A client-scoped thorough recalculation over an event window that spans two pay periods must persist one
/// client_period_hours row per real pay period the window touches, with that period's boundaries and hours, never one row
/// over the event window. Driven through ThoroughRecalculationBackgroundService's queue with the real PeriodHoursService
/// against PostgreSQL (IT database klacks); the Work and Break macros are substituted. Only the client-scoped path runs
/// here: the all-clients path would write rows for every client of the shared database.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class ThoroughRecalculationPeriodHoursPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "THOROUGH_";
    private const string ClientSuffix = "CLIENT";
    private const decimal FirstPeriodWorkTime = 8m;
    private const decimal SecondPeriodWorkTime = 6m;
    private const int DaysIntoTheSecondPeriod = 3;
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    private Guid _clientId;
    private (DateOnly Start, DateOnly End) _firstPeriod;
    private (DateOnly Start, DateOnly End) _secondPeriod;
    private DateOnly _secondPeriodWorkDay;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, ClientSuffix);
        var shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);

        await using (var boundaries = new WorkWriteChainStack())
        {
            _firstPeriod = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(WorkChainTestData.Day);
            _secondPeriod = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(
                _firstPeriod.End.AddDays(DaysIntoTheSecondPeriod));
        }

        _secondPeriodWorkDay = _secondPeriod.Start.AddDays(DaysIntoTheSecondPeriod);
        await WorkChainTestData.SeedWorkOnDayAsync(_clientId, shiftId, WorkChainTestData.Day, FirstPeriodWorkTime);
        await WorkChainTestData.SeedWorkOnDayAsync(_clientId, shiftId, _secondPeriodWorkDay, SecondPeriodWorkTime);
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task AnEventWindowOverTwoPayPeriods_PersistsOneRowPerPayPeriod_WithItsOwnBoundariesAndHours()
    {
        await RunThoroughRecalculationAsync(WorkChainTestData.Day, _secondPeriodWorkDay);

        var rows = (await ChainStateSnapshot.ReadAsync(_clientId)).PeriodHours.OrderBy(p => p.StartDate).ToList();
        rows.Count.ShouldBe(2);
        rows[0].StartDate.ShouldBe(_firstPeriod.Start);
        rows[0].EndDate.ShouldBe(_firstPeriod.End);
        rows[0].Hours.ShouldBe(FirstPeriodWorkTime);
        rows[1].StartDate.ShouldBe(_secondPeriod.Start);
        rows[1].EndDate.ShouldBe(_secondPeriod.End);
        rows[1].Hours.ShouldBe(SecondPeriodWorkTime);
    }

    private async Task RunThoroughRecalculationAsync(DateOnly windowStart, DateOnly windowEnd)
    {
        await using var stack = new WorkWriteChainStack();
        var completed = new TaskCompletionSource();
        stack.WorkNotifications
            .NotifyThoroughRecalculationCompleted(Arg.Any<ThoroughRecalculationCompletedDto>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => completed.TrySetResult());

        var services = new ServiceCollection();
        services.AddSingleton<DataBaseContext>(stack.Context);
        services.AddSingleton<IPeriodHoursService>(stack.PeriodHoursService);
        services.AddSingleton(stack.WorkNotifications);
        services.AddSingleton(Substitute.For<IWorkMacroService>());
        services.AddSingleton(Substitute.For<IBreakMacroService>());
        services.AddSingleton(Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>());
        await using var provider = services.BuildServiceProvider();

        using var service = new ThoroughRecalculationBackgroundService(
            provider, NullLogger<ThoroughRecalculationBackgroundService>.Instance);
        service.QueueRecalculation(windowStart, windowEnd, null, null, [_clientId]).ShouldBeTrue();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var finished = await Task.WhenAny(completed.Task, Task.Delay(CompletionTimeout));
            finished.ShouldBe(completed.Task, "the thorough recalculation did not complete within the timeout");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
