// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Fails every save that would write a client_period_hours row (Added or Modified in the change tracker) with a raw,
/// non-transient DbUpdateException, the way an untranslated failure of PeriodHoursService's own SaveChanges reaches the
/// handler. Saves without such a row pass unchanged, so the Work commits before it run as in production.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Klacks.IntegrationTest.Works;

public sealed class ClientPeriodHoursSaveFailureInterceptor : SaveChangesInterceptor
{
    public const string FailureMessage = "Injected client_period_hours save failure";

    public int FailureCount { get; private set; }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowWhenPeriodHoursAreWritten(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ThrowWhenPeriodHoursAreWritten(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void ThrowWhenPeriodHoursAreWritten(DbContext? context)
    {
        var writesPeriodHours = context != null && context.ChangeTracker.Entries<ClientPeriodHours>()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified);
        if (!writesPeriodHours)
        {
            return;
        }

        FailureCount++;
        throw new DbUpdateException(FailureMessage);
    }
}
