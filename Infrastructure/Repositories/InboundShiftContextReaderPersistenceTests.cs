// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for InboundShiftContextReader against the real PostgreSQL schema. The unit tests run on
/// EF InMemory, which does not enforce the foreign keys and joins the way PostgreSQL does. These tests pin
/// what the real SQL returns: a normal plan row is found with its shift name, and container sub-rows
/// (ParentWorkId set), scenario rows (AnalyseToken set), soft-deleted work rows, rows whose Shift is
/// soft-deleted (inner join under the shift query filter), other clients and dates outside the window are
/// all excluded. The cap is applied after the (workday, start time) ordering, which the clarification shift
/// selector relies on. Every fixture row carries the INTEGRATION_TEST_ prefix on the client and shift name and
/// cleanup is scoped by that prefix only.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Inbound;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class InboundShiftContextReaderPersistenceTests
{
    private const string TestPrefix = "INTEGRATION_TEST_SHIFTCTX_";
    private const string DefaultConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    private const int DefaultMaxCount = 10;

    private static readonly DateOnly Day = new(2031, 3, 10);

    private DataBaseContext _context = null!;
    private InboundShiftContextReader _reader = null!;
    private Guid _clientId;
    private Guid _otherClientId;
    private Guid _normalShiftId;
    private Guid _deletedShiftId;

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? DefaultConnectionString;
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _reader = new InboundShiftContextReader(_context);

        await CleanupAsync();

        _clientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();
        _normalShiftId = Guid.NewGuid();
        _deletedShiftId = Guid.NewGuid();

        _context.Client.Add(NewClient(_clientId, "Subject"));
        _context.Client.Add(NewClient(_otherClientId, "Other"));
        _context.Shift.Add(NewShift(_normalShiftId, "Normal"));
        _context.Shift.Add(NewShift(_deletedShiftId, "Deleted"));
        await _context.SaveChangesAsync();

        await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.Id == _deletedShiftId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsDeleted, true));
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    private async Task CleanupAsync()
    {
                await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE parent_work_id IS NOT NULL AND client_id IN (SELECT id FROM client WHERE starts_with(name, {0}))",
            TestPrefix);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, {0}))",
            TestPrefix);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE starts_with(name, {0})", TestPrefix);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client WHERE starts_with(name, {0})", TestPrefix);
    }

    private static Client NewClient(Guid id, string suffix) => new()
    {
        Id = id,
        Name = TestPrefix + suffix,
        FirstName = "Integration"
    };

    private static Shift NewShift(Guid id, string suffix) => new()
    {
        Id = id,
        Name = TestPrefix + suffix,
        StartShift = new TimeOnly(6, 0),
        EndShift = new TimeOnly(14, 0)
    };

    private Work NewWork(
        Guid clientId,
        DateOnly date,
        int startHour,
        Guid? shiftId = null,
        Guid? analyseToken = null,
        bool isDeleted = false,
        Guid? parentWorkId = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = shiftId ?? _normalShiftId,
        CurrentDate = date,
        StartTime = new TimeOnly(startHour, 0),
        EndTime = new TimeOnly(startHour + 1, 0),
        WorkTime = 60,
        AnalyseToken = analyseToken,
        IsDeleted = isDeleted,
        ParentWorkId = parentWorkId
    };

    private async Task<IReadOnlyList<int>> StartHoursAsync(int maxCount = DefaultMaxCount)
    {
        _context.ChangeTracker.Clear();
        var shifts = await _reader.GetShiftsAsync(_clientId, Day, Day.AddDays(1), maxCount);
        return shifts.Select(s => s.StartTime.Hour).ToList();
    }

    private async Task AssertStoredRowCountAsync(int expected)
    {
        var stored = await _context.Work.IgnoreQueryFilters().CountAsync(w => w.ClientId == _clientId);
        stored.ShouldBe(expected, "every fixture row must really exist, otherwise an exclusion assertion proves nothing");
    }

    [Test]
    public async Task NormalPlanRow_IsReturnedWithTheShiftName()
    {
        _context.Work.Add(NewWork(_clientId, Day, 8));
        await _context.SaveChangesAsync();

        var shifts = await _reader.GetShiftsAsync(_clientId, Day, Day.AddDays(1), DefaultMaxCount);

        shifts.Count.ShouldBe(1);
        shifts[0].Date.ShouldBe(Day);
        shifts[0].StartTime.ShouldBe(new TimeOnly(8, 0));
        shifts[0].EndTime.ShouldBe(new TimeOnly(9, 0));
        shifts[0].ShiftName.ShouldBe(TestPrefix + "Normal");
    }

    [Test]
    public async Task ContainerSubRow_IsExcluded_WhileTheNormalRowStaysVisible()
    {
        var parent = NewWork(_otherClientId, Day, 5);
        _context.Work.Add(parent);
        _context.Work.Add(NewWork(_clientId, Day, 7, parentWorkId: parent.Id));
        _context.Work.Add(NewWork(_clientId, Day, 9));
        await _context.SaveChangesAsync();

        await AssertStoredRowCountAsync(2);
        (await StartHoursAsync()).ShouldBe([9]);
    }

    [Test]
    public async Task WorkWhoseShiftIsSoftDeleted_IsExcluded_WhileTheNormalRowStaysVisible()
    {
        _context.Work.Add(NewWork(_clientId, Day, 10));
        _context.Work.Add(NewWork(_clientId, Day, 11, _deletedShiftId));
        await _context.SaveChangesAsync();

        await AssertStoredRowCountAsync(2);
        (await StartHoursAsync()).ShouldBe([10]);
    }

    [Test]
    public async Task ScenarioRow_IsExcluded_WhileTheNormalRowStaysVisible()
    {
        _context.Work.Add(NewWork(_clientId, Day, 12, analyseToken: Guid.NewGuid()));
        _context.Work.Add(NewWork(_clientId, Day, 13));
        await _context.SaveChangesAsync();

        await AssertStoredRowCountAsync(2);
        (await StartHoursAsync()).ShouldBe([13]);
    }

    [Test]
    public async Task SoftDeletedWork_OtherClient_AndDatesOutsideTheWindow_AreExcluded()
    {
        _context.Work.Add(NewWork(_clientId, Day, 6, isDeleted: true));
        _context.Work.Add(NewWork(_otherClientId, Day, 7));
        _context.Work.Add(NewWork(_clientId, Day.AddDays(-1), 8));
        _context.Work.Add(NewWork(_clientId, Day.AddDays(2), 9));
        _context.Work.Add(NewWork(_clientId, Day.AddDays(1), 14));
        await _context.SaveChangesAsync();

        await AssertStoredRowCountAsync(4);
        (await StartHoursAsync()).ShouldBe([14]);
    }

    [Test]
    public async Task Cap_IsAppliedAfterTheDateAndStartTimeOrdering()
    {
        var hours = Enumerable.Range(6, 6).ToList();
        foreach (var hour in hours.AsEnumerable().Reverse())
        {
            _context.Work.Add(NewWork(_clientId, Day.AddDays(1), hour));
        }

        foreach (var hour in hours.AsEnumerable().Reverse())
        {
            _context.Work.Add(NewWork(_clientId, Day, hour));
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var shifts = await _reader.GetShiftsAsync(_clientId, Day, Day.AddDays(1), DefaultMaxCount);

        shifts.Count.ShouldBe(DefaultMaxCount);
        shifts.Select(s => (s.Date, s.StartTime.Hour)).ShouldBe(
        [
            (Day, 6), (Day, 7), (Day, 8), (Day, 9), (Day, 10), (Day, 11),
            (Day.AddDays(1), 6), (Day.AddDays(1), 7), (Day.AddDays(1), 8), (Day.AddDays(1), 9)
        ]);
    }
}
