// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// What the Work write chain left committed for a set of clients, read in a fresh retrying context so nothing the
/// handler's context still tracks can leak in: every Work (soft-deleted ones included), every live client_period_hours
/// row and every schedule_change row (soft-deleted ones included).
/// </summary>
/// <param name="Works">The clients' Works, query filters ignored</param>
/// <param name="PeriodHours">The clients' live period hour rows</param>
/// <param name="ScheduleChanges">The clients' schedule change markers, query filters ignored</param>

using Klacks.Api.Domain.Models.Schedules;
using Microsoft.EntityFrameworkCore;

namespace Klacks.IntegrationTest.Works;

public sealed record ChainStateSnapshot(
    IReadOnlyList<Work> Works,
    IReadOnlyList<ClientPeriodHours> PeriodHours,
    IReadOnlyList<ScheduleChange> ScheduleChanges)
{
    public static async Task<ChainStateSnapshot> ReadAsync(params Guid[] clientIds)
    {
        await using var fresh = WorkChainTestData.NewRetryingContext();
        var works = await fresh.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => clientIds.Contains(w.ClientId))
            .ToListAsync();
        var periodHours = await fresh.ClientPeriodHours.AsNoTracking()
            .Where(p => clientIds.Contains(p.ClientId))
            .ToListAsync();
        var scheduleChanges = await fresh.ScheduleChange.IgnoreQueryFilters().AsNoTracking()
            .Where(sc => clientIds.Contains(sc.ClientId))
            .ToListAsync();
        return new ChainStateSnapshot(works, periodHours, scheduleChanges);
    }

    public Work WorkById(Guid workId) => Works.Single(w => w.Id == workId);

    public IReadOnlyList<Work> LiveWorksOf(Guid clientId) => Works.Where(w => w.ClientId == clientId && !w.IsDeleted).ToList();

    public ClientPeriodHours PeriodHoursOf(Guid clientId) => PeriodHours.Single(p => p.ClientId == clientId);

    public IReadOnlyList<ScheduleChange> LiveScheduleChangesOf(Guid clientId)
        => ScheduleChanges.Where(sc => sc.ClientId == clientId && !sc.IsDeleted).ToList();
}
