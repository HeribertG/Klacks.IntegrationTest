// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.ShiftSchedule;

[TestFixture]
public class SporadicShiftStatusTests
{
    private const string TestPrefix = "TEST_SPORADIC_";

    private DataBaseContext _context = null!;
    private ShiftScheduleService _service = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        var logger = Substitute.For<ILogger<ShiftScheduleService>>();
        _service = new ShiftScheduleService(_context, logger);

        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        _context.Dispose();
    }

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Shift> CreateSporadicShiftAsync(
        string suffix,
        ShiftSporadic scope,
        DateOnly? fromDate = null,
        DateOnly? untilDate = null,
        int sumEmployees = 1,
        int quantity = 1)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "SPO",
            Description = "Sporadic test shift",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = fromDate ?? new DateOnly(2026, 1, 1),
            UntilDate = untilDate,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            WorkTime = 8m,
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            IsSaturday = true,
            IsSunday = true,
            IsSporadic = true,
            SporadicScope = scope,
            Quantity = quantity,
            SumEmployees = sumEmployees
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Work> AddWorkAsync(Guid shiftId, Guid clientId, DateOnly date)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            ClientId = clientId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m
        };
        await _context.Set<Work>().AddAsync(work);
        await _context.SaveChangesAsync();
        return work;
    }

    [Test]
    public async Task GetShiftSchedule_With_MonthScope_Marks_BookingDate_As_Booked_And_Range_As_Blocked()
    {
        var shift = await CreateSporadicShiftAsync("Month1", ShiftSporadic.Month);
        var client = await CreateClientAsync("MonthClient");
        var bookingDate = new DateOnly(2026, 5, 15);
        await AddWorkAsync(shift.Id, client.Id, bookingDate);

        var startDate = new DateOnly(2026, 5, 1);
        var endDate = new DateOnly(2026, 5, 31);

        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        var shiftRows = result.Where(r => r.ShiftId == shift.Id).OrderBy(r => r.Date).ToList();
        shiftRows.Count.ShouldBe(31, "Shift is active every day; full month should be returned");

        var bookedRow = shiftRows.Single(r => r.Date == bookingDate);
        bookedRow.SporadicStatus.ShouldBe((short)1, "Booking date must be flagged as booked");

        var blocked = shiftRows.Where(r => r.Date != bookingDate).ToList();
        blocked.ShouldAllBe(r => r.SporadicStatus == 2, "All other days in the month must be flagged as blocked");
    }

    [Test]
    public async Task GetShiftSchedule_With_MonthScope_Does_Not_Block_Other_Months()
    {
        var shift = await CreateSporadicShiftAsync("Month2", ShiftSporadic.Month);
        var client = await CreateClientAsync("MonthClient2");
        await AddWorkAsync(shift.Id, client.Id, new DateOnly(2026, 5, 15));

        var result = await _service.GetShiftScheduleQuery(
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30)).ToListAsync();

        var shiftRows = result.Where(r => r.ShiftId == shift.Id).ToList();
        shiftRows.Count.ShouldBe(30);
        shiftRows.ShouldAllBe(r => r.SporadicStatus == 0, "June must not be blocked by a May booking when scope = Month");
    }

    [Test]
    public async Task GetShiftSchedule_With_WeekScope_Blocks_Iso_Week_Only()
    {
        var shift = await CreateSporadicShiftAsync("Week1", ShiftSporadic.Week);
        var client = await CreateClientAsync("WeekClient");
        // 2026-05-13 is a Wednesday; ISO week runs Mon 2026-05-11 .. Sun 2026-05-17.
        var bookingDate = new DateOnly(2026, 5, 13);
        await AddWorkAsync(shift.Id, client.Id, bookingDate);

        var result = await _service.GetShiftScheduleQuery(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 24)).ToListAsync();

        var shiftRows = result.Where(r => r.ShiftId == shift.Id).ToDictionary(r => r.Date, r => r.SporadicStatus);

        shiftRows[new DateOnly(2026, 5, 11)].ShouldBe((short)2);
        shiftRows[new DateOnly(2026, 5, 12)].ShouldBe((short)2);
        shiftRows[new DateOnly(2026, 5, 13)].ShouldBe((short)1);
        shiftRows[new DateOnly(2026, 5, 14)].ShouldBe((short)2);
        shiftRows[new DateOnly(2026, 5, 17)].ShouldBe((short)2);

        shiftRows[new DateOnly(2026, 5, 10)].ShouldBe((short)0, "Day before ISO week must stay free");
        shiftRows[new DateOnly(2026, 5, 18)].ShouldBe((short)0, "Day after ISO week must stay free");
    }

    [Test]
    public async Task GetShiftSchedule_Without_Sporadic_Booking_Returns_None_Status()
    {
        var shift = await CreateSporadicShiftAsync("Idle", ShiftSporadic.Month);

        var result = await _service.GetShiftScheduleQuery(
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 7)).ToListAsync();

        var shiftRows = result.Where(r => r.ShiftId == shift.Id).ToList();
        shiftRows.ShouldNotBeEmpty();
        shiftRows.ShouldAllBe(r => r.SporadicStatus == 0);
    }

    [Test]
    public async Task GetShiftSchedule_MultiEmployee_DayNotFullYet_StaysOpen()
    {
        var shift = await CreateSporadicShiftAsync("MultiEmpA", ShiftSporadic.Week, sumEmployees: 2, quantity: 3);
        var clientA = await CreateClientAsync("MEa1");
        var bookingDate = new DateOnly(2026, 5, 13);
        await AddWorkAsync(shift.Id, clientA.Id, bookingDate);

        var result = await _service.GetShiftScheduleQuery(
            new DateOnly(2026, 5, 11),
            new DateOnly(2026, 5, 17)).ToListAsync();

        var byDate = result.Where(r => r.ShiftId == shift.Id).ToDictionary(r => r.Date, r => r.SporadicStatus);

        byDate[bookingDate].ShouldBe((short)0, "1/2 employees booked: day still open");
        byDate[new DateOnly(2026, 5, 12)].ShouldBe((short)0, "1 of 3 distinct days used: other days still open");
        byDate[new DateOnly(2026, 5, 17)].ShouldBe((short)0);
    }

    [Test]
    public async Task GetShiftSchedule_MultiEmployee_DayFull_MarksBookedAndKeepsOtherDaysOpen()
    {
        var shift = await CreateSporadicShiftAsync("MultiEmpB", ShiftSporadic.Week, sumEmployees: 2, quantity: 3);
        var clientA = await CreateClientAsync("MEb1");
        var clientB = await CreateClientAsync("MEb2");
        var bookingDate = new DateOnly(2026, 5, 13);
        await AddWorkAsync(shift.Id, clientA.Id, bookingDate);
        await AddWorkAsync(shift.Id, clientB.Id, bookingDate);

        var result = await _service.GetShiftScheduleQuery(
            new DateOnly(2026, 5, 11),
            new DateOnly(2026, 5, 17)).ToListAsync();

        var byDate = result.Where(r => r.ShiftId == shift.Id).ToDictionary(r => r.Date, r => r.SporadicStatus);

        byDate[bookingDate].ShouldBe((short)1, "2/2 employees: day is full -> booked");
        byDate[new DateOnly(2026, 5, 11)].ShouldBe((short)0, "1 of 3 quantity used: other days still open");
        byDate[new DateOnly(2026, 5, 17)].ShouldBe((short)0);
    }

    [Test]
    public async Task GetShiftSchedule_MultiQuantity_DistinctDaysReached_BlocksRemainingDays()
    {
        var shift = await CreateSporadicShiftAsync("MultiQty", ShiftSporadic.Week, sumEmployees: 2, quantity: 3);
        var clientA = await CreateClientAsync("MQa");
        var clientB = await CreateClientAsync("MQb");
        var clientC = await CreateClientAsync("MQc");

        var mon = new DateOnly(2026, 5, 11);
        var wed = new DateOnly(2026, 5, 13);
        var fri = new DateOnly(2026, 5, 15);
        await AddWorkAsync(shift.Id, clientA.Id, mon);
        await AddWorkAsync(shift.Id, clientB.Id, wed);
        await AddWorkAsync(shift.Id, clientC.Id, fri);

        var result = await _service.GetShiftScheduleQuery(
            new DateOnly(2026, 5, 11),
            new DateOnly(2026, 5, 17)).ToListAsync();

        var byDate = result.Where(r => r.ShiftId == shift.Id).ToDictionary(r => r.Date, r => r.SporadicStatus);

        byDate[mon].ShouldBe((short)0, "1/2 employees -> still open on a partially filled booked day");
        byDate[wed].ShouldBe((short)0);
        byDate[fri].ShouldBe((short)0);
        byDate[new DateOnly(2026, 5, 12)].ShouldBe((short)2, "3 distinct days reached: unbooked day must be blocked");
        byDate[new DateOnly(2026, 5, 14)].ShouldBe((short)2);
        byDate[new DateOnly(2026, 5, 16)].ShouldBe((short)2);
        byDate[new DateOnly(2026, 5, 17)].ShouldBe((short)2);
    }

    [Test]
    public async Task GetShiftSchedule_MultiSlot_AllSlotsExhausted_DaysFullAndOthersBlocked()
    {
        var shift = await CreateSporadicShiftAsync("MultiAll", ShiftSporadic.Week, sumEmployees: 2, quantity: 2);
        var c1 = await CreateClientAsync("MAa1");
        var c2 = await CreateClientAsync("MAa2");
        var c3 = await CreateClientAsync("MAb1");
        var c4 = await CreateClientAsync("MAb2");

        var mon = new DateOnly(2026, 5, 11);
        var thu = new DateOnly(2026, 5, 14);
        await AddWorkAsync(shift.Id, c1.Id, mon);
        await AddWorkAsync(shift.Id, c2.Id, mon);
        await AddWorkAsync(shift.Id, c3.Id, thu);
        await AddWorkAsync(shift.Id, c4.Id, thu);

        var result = await _service.GetShiftScheduleQuery(
            new DateOnly(2026, 5, 11),
            new DateOnly(2026, 5, 17)).ToListAsync();

        var byDate = result.Where(r => r.ShiftId == shift.Id).ToDictionary(r => r.Date, r => r.SporadicStatus);

        byDate[mon].ShouldBe((short)1);
        byDate[thu].ShouldBe((short)1);
        byDate[new DateOnly(2026, 5, 12)].ShouldBe((short)2);
        byDate[new DateOnly(2026, 5, 13)].ShouldBe((short)2);
        byDate[new DateOnly(2026, 5, 15)].ShouldBe((short)2);
        byDate[new DateOnly(2026, 5, 16)].ShouldBe((short)2);
        byDate[new DateOnly(2026, 5, 17)].ShouldBe((short)2);
    }
}
