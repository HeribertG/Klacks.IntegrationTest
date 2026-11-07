// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Switching an absence type to on-call recomputes the cached period hours over the span of its real-plan absences
/// (OnCallSwitchPeriodHoursRefresher). Runs against PostgreSQL (IT database klacks): the span query of BreakRepository
/// ignores scenario absences, container sub-breaks and soft-deleted absences, and the refreshed client_period_hours row
/// no longer counts the absence. Fixture data carries the prefix and lies in 2099.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class OnCallSwitchPeriodHoursRefreshPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "ONCALLSWITCH_";
    private const string ClientSuffix = "CLIENT";
    private const string TypeSuffix = "STANDBY";
    private const decimal AbsenceHours = 8m;
    private const decimal AbsenceSurcharges = 2m;

    private static readonly DateOnly ScenarioDay = new(2099, 9, 1);
    private static readonly DateOnly DeletedDay = new(2099, 1, 5);
    private static readonly DateOnly SubBreakDay = new(2099, 11, 2);

    private Guid _clientId;
    private Guid _absenceId;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, ClientSuffix);
        _absenceId = await WorkChainTestData.SeedAbsenceAsync(Prefix, TypeSuffix, isOnCall: false);
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task TheSpan_CoversOnlyRealPlanAbsencesOfTheType_OnPostgres()
    {
        await WorkChainTestData.SeedBreakAsync(_clientId, _absenceId, AbsenceHours, AbsenceSurcharges);
        await SeedOtherBreaksAsync();

        await using var context = WorkChainTestData.NewRetryingContext();
        var repository = new BreakRepository(context, Substitute.For<ILogger<Break>>());

        var span = await repository.GetRealPlanSpanOfAbsenceTypeAsync(_absenceId);
        var none = await repository.GetRealPlanSpanOfAbsenceTypeAsync(Guid.NewGuid());

        span.ShouldBe((WorkChainTestData.Day, WorkChainTestData.Day));
        none.ShouldBeNull();
    }

    [Test]
    public async Task SwitchingTheTypeToOnCall_RefreshesTheCachedRow_WithoutTheAbsenceHours()
    {
        await WorkChainTestData.SeedBreakAsync(_clientId, _absenceId, AbsenceHours, AbsenceSurcharges);
        (DateOnly Start, DateOnly End) period;
        await using (var stack = new WorkWriteChainStack())
        {
            period = await stack.PeriodHoursService.GetPeriodBoundariesAsync(WorkChainTestData.Day);
        }

        await WorkChainTestData.SeedPeriodHoursAsync(_clientId, period, AbsenceHours, AbsenceSurcharges);
        await SwitchToOnCallAsync();

        await using (var stack = new WorkWriteChainStack())
        {
            var refresher = new OnCallSwitchPeriodHoursRefresher(
                new BreakRepository(stack.Context, Substitute.For<ILogger<Break>>()), stack.PeriodHoursService,
                Substitute.For<ILogger<OnCallSwitchPeriodHoursRefresher>>());
            await refresher.RefreshAsync(_absenceId);
        }

        var persisted = (await ChainStateSnapshot.ReadAsync(_clientId)).PeriodHoursOf(_clientId);
        persisted.StartDate.ShouldBe(period.Start);
        persisted.Hours.ShouldBe(0m, "an on-call absence counts no hours");
        persisted.Surcharges.ShouldBe(0m);
    }

    private async Task SwitchToOnCallAsync()
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        var absence = await context.Absence.SingleAsync(a => a.Id == _absenceId);
        absence.IsOnCall = true;
        await context.SaveChangesAsync();
    }

    private async Task SeedOtherBreaksAsync()
    {
        var shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);
        var containerId = await WorkChainTestData.SeedWorkOnDayAsync(_clientId, shiftId, SubBreakDay, AbsenceHours);
        await using var context = WorkChainTestData.NewRetryingContext();
        context.Break.Add(NewBreak(ScenarioDay, analyseToken: Guid.NewGuid(), parentWorkId: null));
        context.Break.Add(NewBreak(SubBreakDay, analyseToken: null, parentWorkId: containerId));
        var deleted = NewBreak(DeletedDay, analyseToken: null, parentWorkId: null);
        deleted.IsDeleted = true;
        deleted.DeletedTime = DateTime.UtcNow;
        context.Break.Add(deleted);
        await context.SaveChangesAsync();
    }

    private Break NewBreak(DateOnly day, Guid? analyseToken, Guid? parentWorkId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _clientId,
        AbsenceId = _absenceId,
        CurrentDate = day,
        StartTime = TimeOnly.MinValue,
        EndTime = TimeOnly.MinValue,
        WorkTime = AbsenceHours,
        AnalyseToken = analyseToken,
        ParentWorkId = parentWorkId
    };
}
