// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration test for the C4 apply re-point (2026-06-19): CloneScenarioDataWithMapsAsync must return
/// a work id map (original → clone) that covers the real works, and re-pointing a cloned work's ClientId
/// in place must persist on real Postgres. These are the load-bearing assumptions of
/// HarmonizerApplyService's re-point path, which updates cloned works in place (so their
/// WorkChange/Expense/sub-Break children survive on accept) instead of delete+recreate. Far-future dates
/// (2099) isolate the group-less clone to the seeded rows.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using Work = Klacks.Api.Domain.Models.Schedules.Work;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class HarmonizerApplyRepointCloneTests
{
    private const string TestPrefix = "INTEGRATION_TEST_REPOINT_";
    private static readonly DateOnly PeriodFrom = new(2099, 8, 6);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 10);
    private static readonly DateOnly InPeriodDate = new(2099, 8, 8);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        _context = NewContext();
        _service = new AnalyseScenarioService(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var c = new Client { Id = Guid.NewGuid(), Name = TestPrefix + suffix, FirstName = "Test", Company = string.Empty, LegalEntity = false };
        await _context.Set<Client>().AddAsync(c);
        await _context.SaveChangesAsync();
        return c;
    }

    private async Task<Shift> CreateShiftAsync(string suffix)
    {
        var s = new Shift
        {
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, Abbreviation = "TST", Description = "Re-point test",
            Status = ShiftStatus.OriginalShift, FromDate = new DateOnly(2099, 1, 1), UntilDate = null,
            StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            IsSaturday = true, IsSunday = true,
            ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 8m,
            AnalyseToken = null, ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(s);
        await _context.SaveChangesAsync();
        return s;
    }

    private async Task<Work> CreateWorkAsync(Guid clientId, Guid shiftId, DateOnly date)
    {
        var w = new Work
        {
            Id = Guid.NewGuid(), ClientId = clientId, ShiftId = shiftId, CurrentDate = date,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), WorkTime = 8m,
            LockLevel = WorkLockLevel.None, AnalyseToken = null
        };
        await _context.Work.AddAsync(w);
        await _context.SaveChangesAsync();
        return w;
    }

    [Test, Explicit("Read/write C4 re-point clone against the real test DB (port 5434); cleans up after itself.")]
    public async Task CloneWithMaps_ReturnsWorkIdMap_AndInPlaceRepointPersists()
    {
        var clientA = await CreateClientAsync("A");
        var clientB = await CreateClientAsync("B");
        var shift = await CreateShiftAsync("S");
        var work = await CreateWorkAsync(clientA.Id, shift.Id, InPeriodDate);

        var token = Guid.NewGuid();
        var (_, workIdMap) = await _service.CloneScenarioDataWithMapsAsync(
            null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        // The new return must map the real work id to a freshly cloned work — without it the apply
        // re-point would have no targets and soft-delete every movable work.
        workIdMap.ShouldContainKey(work.Id);
        var cloneId = workIdMap[work.Id];
        cloneId.ShouldNotBe(work.Id);

        var clone = await _context.Work.IgnoreQueryFilters().FirstAsync(w => w.Id == cloneId);
        clone.AnalyseToken.ShouldBe(token);
        clone.ClientId.ShouldBe(clientA.Id);
        clone.IsDeleted.ShouldBeFalse();

        // The re-point changes only the ClientId in place (no delete+recreate), so children survive.
        clone.ClientId = clientB.Id;
        await _context.SaveChangesAsync();

        var reloaded = await _context.Work.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.Id == cloneId);
        reloaded.ClientId.ShouldBe(clientB.Id);
        reloaded.IsDeleted.ShouldBeFalse();
        reloaded.AnalyseToken.ShouldBe(token);
    }
}
