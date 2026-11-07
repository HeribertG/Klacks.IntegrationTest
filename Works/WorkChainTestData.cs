// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Seed and cleanup for the Work write chain tests against the shared IT database klacks. Every client and shift name
/// starts with the fixture prefix (INTEGRATION_TEST_WORKCHAIN_...), every date lies in 2099, and the cleanup hard-deletes
/// only the Works, breaks, schedule changes, period hours and memberships of clients carrying that prefix, then those
/// clients, the prefixed shifts and the absence types whose German name carries the prefix. Each seed runs in its own
/// retrying context so no interceptor of a test context sees it.
/// </summary>
/// <param name="prefix">Fixture prefix; must start with INTEGRATION_TEST_WORKCHAIN_</param>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Klacks.IntegrationTest.Works;

public static class WorkChainTestData
{
    public const string PrefixRoot = "INTEGRATION_TEST_WORKCHAIN_";
    public const int MaxRetries = 3;

    public static readonly DateOnly Day = new(2099, 3, 10);
    public static readonly TimeOnly MorningStart = new(8, 0);
    public static readonly TimeOnly MorningEnd = new(16, 0);
    public static readonly TimeOnly EveningStart = new(18, 0);
    public static readonly TimeOnly EveningEnd = new(20, 0);

    public static DataBaseContext NewRetryingContext(params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(SupervisorVisibilityScenario.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(MaxRetries))
            .UseSnakeCaseNamingConvention();
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new DataBaseContext(builder.Options, Substitute.For<IHttpContextAccessor>());
    }

    public static async Task<Guid> SeedClientAsync(string prefix, string suffix)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = prefix + suffix, FirstName = suffix };
        await SaveAsync(context => context.Client.Add(client));
        return client.Id;
    }

    public static async Task<Guid> SeedShiftAsync(string prefix)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = prefix + nameof(Shift),
            StartShift = MorningStart,
            EndShift = MorningEnd
        };
        await SaveAsync(context => context.Shift.Add(shift));
        return shift.Id;
    }

    public static async Task<Guid> SeedWorkAsync(
        Guid clientId, Guid shiftId, TimeOnly start, TimeOnly end, decimal workTime, decimal surcharges = 0m)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = Day,
            StartTime = start,
            EndTime = end,
            WorkTime = workTime,
            Surcharges = surcharges
        };
        await SaveAsync(context => context.Work.Add(work));
        return work.Id;
    }

    public static async Task<Guid> SeedWorkOnDayAsync(Guid clientId, Guid shiftId, DateOnly day, decimal workTime)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = day,
            StartTime = MorningStart,
            EndTime = MorningEnd,
            WorkTime = workTime
        };
        await SaveAsync(context => context.Work.Add(work));
        return work.Id;
    }

    public static async Task<Guid> SeedAbsenceAsync(string prefix, string suffix, bool isOnCall)
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = prefix + suffix, En = prefix + suffix },
            Abbreviation = new MultiLanguage { De = suffix },
            Description = new MultiLanguage(),
            IsOnCall = isOnCall
        };
        await SaveAsync(context => context.Absence.Add(absence));
        return absence.Id;
    }

    public static async Task<Guid> SeedBreakAsync(Guid clientId, Guid absenceId, decimal workTime, decimal surcharges)
    {
        var breakEntry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = Day,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            WorkTime = workTime,
            Surcharges = surcharges
        };
        await SaveAsync(context => context.Break.Add(breakEntry));
        return breakEntry.Id;
    }

    public static async Task SoftDeleteWorkAsync(Guid workId)
    {
        await using var context = NewRetryingContext();
        var work = await context.Work.SingleAsync(w => w.Id == workId);
        context.Work.Remove(work);
        await context.SaveChangesAsync();
    }

    public static async Task SeedPeriodHoursAsync(
        Guid clientId, (DateOnly Start, DateOnly End) period, decimal hours, decimal surcharges)
    {
        var row = new ClientPeriodHours
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            StartDate = period.Start,
            EndDate = period.End,
            Hours = hours,
            Surcharges = surcharges,
            CalculatedAt = DateTime.UtcNow
        };
        await SaveAsync(context => context.ClientPeriodHours.Add(row));
    }

    public static async Task CleanupAsync(string prefix)
    {
        if (!prefix.StartsWith(PrefixRoot, StringComparison.Ordinal) || prefix.Length <= PrefixRoot.Length)
        {
            throw new ArgumentException($"The fixture prefix '{prefix}' must extend {PrefixRoot}.", nameof(prefix));
        }

        var pattern = prefix.Replace("'", "''");
        var prefixedClients = $"SELECT id FROM client WHERE starts_with(name, '{pattern}')";
        var sql = $@"
            DELETE FROM schedule_change WHERE client_id IN ({prefixedClients});
            DELETE FROM client_period_hours WHERE client_id IN ({prefixedClients});
            DELETE FROM break WHERE client_id IN ({prefixedClients});
            DELETE FROM work WHERE client_id IN ({prefixedClients});
            DELETE FROM membership WHERE client_id IN ({prefixedClients});
            DELETE FROM client WHERE starts_with(name, '{pattern}');
            DELETE FROM shift WHERE starts_with(name, '{pattern}');
            DELETE FROM absence WHERE starts_with(name->>'de', '{pattern}');
        ";
        await using var context = NewRetryingContext();
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task SaveAsync(Action<DataBaseContext> stage)
    {
        await using var context = NewRetryingContext();
        stage(context);
        await context.SaveChangesAsync();
    }
}
