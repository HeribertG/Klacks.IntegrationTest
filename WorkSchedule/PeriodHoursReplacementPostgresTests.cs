// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Period-hours attribution of a ReplacementWithin against real PostgreSQL: the service calculation and the cache-miss
/// fallback of the row header must translate their WorkChange queries and hand the hours from the employee who owns the
/// work to the substitute, also when the substitute is only reachable through ReplaceClientId. Far-future dates keep the
/// rows away from other schedule data; every row hangs off an INTEGRATION_TEST_-prefixed client or shift and is removed by
/// that prefix only.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
[Category("RealDatabase")]
public class PeriodHoursReplacementPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PERIODHOURSREPLACEMENT_";
    private const decimal WorkHours = 8m;
    private const decimal HandedOverHours = 6m;
    private static readonly DateOnly PeriodFrom = new(2098, 8, 1);
    private static readonly DateOnly PeriodUntil = new(2098, 8, 31);
    private static readonly DateOnly WorkDate = new(2098, 8, 5);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext(ignorePendingModelChanges: true);
        await context.Database.MigrateAsync();
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp() => _context = NewContext();

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ReplacementWithin_HandsTheHoursOverOnServiceAndFallback()
    {
        var original = await CreateClientAsync("ORIGINAL");
        var substitute = await CreateClientAsync("SUBSTITUTE");
        var shift = await CreateShiftAsync();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = original.Id,
            ShiftId = shift.Id,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = WorkHours,
        };
        await _context.Work.AddAsync(work);
        await _context.WorkChange.AddAsync(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementWithin,
            ChangeTime = HandedOverHours,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(15, 0),
            ReplaceClientId = substitute.Id,
            Description = TestPrefix,
        });
        await _context.SaveChangesAsync();

        var contractData = Substitute.For<IClientContractDataProvider>();
        contractData
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());
        var service = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contractData,
            Substitute.For<IWeekConfiguration>());
        var repository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            contractData);
        var clientIds = new List<Guid> { original.Id, substitute.Id };

        var calculated = await service.GetPeriodHoursAsync(clientIds, PeriodFrom, PeriodUntil);
        var fallback = await repository.GetPeriodHoursForClients(clientIds, PeriodFrom, PeriodUntil);

        calculated[original.Id].Hours.ShouldBe(WorkHours - HandedOverHours);
        calculated[substitute.Id].Hours.ShouldBe(HandedOverHours);
        fallback[original.Id].Hours.ShouldBe(WorkHours - HandedOverHours);
        fallback[substitute.Id].Hours.ShouldBe(HandedOverHours);

        var fallbackForSubstituteAlone = await repository.GetPeriodHoursForClients([substitute.Id], PeriodFrom, PeriodUntil);
        fallbackForSubstituteAlone[substitute.Id].Hours.ShouldBe(HandedOverHours);
    }

    private DataBaseContext NewContext(bool ignorePendingModelChanges = false)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w =>
            {
                if (ignorePendingModelChanges)
                {
                    w.Ignore(RelationalEventId.PendingModelChangesWarning);
                }
            })
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false,
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Shift> CreateShiftAsync()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TST",
            Description = "Period-hours replacement test",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2098, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = WorkHours,
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }
}
