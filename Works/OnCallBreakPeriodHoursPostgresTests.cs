// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner decision 2026-11-07: an on-call absence (Absence.IsOnCall) stands for on-call duty outside the company and
/// counts neither hours nor surcharges toward the period hours; only the actual assignment on that day counts. An
/// ordinary absence keeps counting. Runs against PostgreSQL (IT database klacks) through WorkWriteChainStack, so the
/// break predicate is proven to translate on Npgsql: the persisted client_period_hours row written by the Work create
/// chain (PeriodHoursService) and the cache-miss fallback of WorkRepository.GetPeriodHoursForClients. The fixture seeds
/// its own absence types (prefixed names), never the seeded Pikett type.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class OnCallBreakPeriodHoursPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "ONCALL_";
    private const string ClientSuffix = "CLIENT";
    private const string OnCallSuffix = "ONCALL";
    private const string VacationSuffix = "VACATION";
    private const decimal AbsenceHours = 8m;
    private const decimal AbsenceSurcharges = 2m;
    private const decimal CallOutHours = 4m;

    private Guid _clientId;
    private Guid _shiftId;
    private (DateOnly Start, DateOnly End) _period;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, ClientSuffix);
        _shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);

        await using var boundaries = new WorkWriteChainStack();
        _period = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(WorkChainTestData.Day);
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task PostingTheCallOutOnAnOnCallDay_PersistsOnlyTheCallOutHours()
    {
        var onCall = await WorkChainTestData.SeedAbsenceAsync(Prefix, OnCallSuffix, isOnCall: true);
        await WorkChainTestData.SeedBreakAsync(_clientId, onCall, AbsenceHours, AbsenceSurcharges);

        await using (var stack = new WorkWriteChainStack())
        {
            var result = await stack.NewPostHandler().Handle(
                new PostCommand<WorkResource>(CallOutResource()), CancellationToken.None);

            result.ShouldNotBeNull();
            result.PeriodHours!.Hours.ShouldBe(CallOutHours);
        }

        var persisted = (await ChainStateSnapshot.ReadAsync(_clientId)).PeriodHoursOf(_clientId);
        persisted.StartDate.ShouldBe(_period.Start);
        persisted.EndDate.ShouldBe(_period.End);
        persisted.Hours.ShouldBe(CallOutHours, "the 8 h on-call absence adds nothing, only the 4 h call-out counts");
        persisted.Surcharges.ShouldBe(0m, "the surcharge of the on-call absence does not count either");
    }

    [Test]
    public async Task PostingAWorkNextToAVacation_StillCountsTheVacation()
    {
        var vacation = await WorkChainTestData.SeedAbsenceAsync(Prefix, VacationSuffix, isOnCall: false);
        await WorkChainTestData.SeedBreakAsync(_clientId, vacation, AbsenceHours, AbsenceSurcharges);

        await using (var stack = new WorkWriteChainStack())
        {
            await stack.NewPostHandler().Handle(new PostCommand<WorkResource>(CallOutResource()), CancellationToken.None);
        }

        var persisted = (await ChainStateSnapshot.ReadAsync(_clientId)).PeriodHoursOf(_clientId);
        persisted.Hours.ShouldBe(AbsenceHours + CallOutHours);
        persisted.Surcharges.ShouldBe(AbsenceSurcharges);
    }

    [Test]
    public async Task CacheMissFallbackOfTheWorkRepository_CountsOnlyTheCallOut()
    {
        var onCall = await WorkChainTestData.SeedAbsenceAsync(Prefix, OnCallSuffix, isOnCall: true);
        var vacation = await WorkChainTestData.SeedAbsenceAsync(Prefix, VacationSuffix, isOnCall: false);
        await WorkChainTestData.SeedBreakAsync(_clientId, onCall, AbsenceHours, AbsenceSurcharges);
        await WorkChainTestData.SeedBreakAsync(_clientId, vacation, AbsenceHours, AbsenceSurcharges);
        await WorkChainTestData.SeedWorkAsync(
            _clientId, _shiftId, WorkChainTestData.MorningStart, WorkChainTestData.MorningEnd, CallOutHours);

        await using var stack = new WorkWriteChainStack();
        var fallback = await stack.WorkRepository.GetPeriodHoursForClients([_clientId], _period.Start, _period.End);
        var live = await stack.PeriodHoursService.GetPeriodHoursAsync([_clientId], _period.Start, _period.End);

        foreach (var result in new[] { fallback, live })
        {
            result[_clientId].Hours.ShouldBe(AbsenceHours + CallOutHours, "vacation counts, the on-call absence does not");
            result[_clientId].Surcharges.ShouldBe(AbsenceSurcharges);
        }
    }

    private WorkResource CallOutResource() => new()
    {
        Id = Guid.Empty,
        ClientId = _clientId,
        ShiftId = _shiftId,
        CurrentDate = WorkChainTestData.Day,
        StartTime = WorkChainTestData.EveningStart,
        EndTime = WorkChainTestData.EveningEnd.AddHours(2),
        WorkTime = CallOutHours
    };
}
