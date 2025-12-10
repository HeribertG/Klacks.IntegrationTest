using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace IntegrationTest.ShiftSchedule;

[TestFixture]
public class GetShiftScheduleTests
{
    private DataBaseContext _context = null!;
    private ShiftScheduleService _service = null!;
    private ILogger<ShiftScheduleService> _logger = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks1;Username=postgres;Password=admin";
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _logger = Substitute.For<ILogger<ShiftScheduleService>>();

        _service = new ShiftScheduleService(_context, _logger);

        await CleanupTestData();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestData();
        _context?.Dispose();
    }

    private async Task CleanupTestData()
    {
        var testShifts = await _context.Shift
            .Where(s => s.Name.StartsWith("TEST_"))
            .ToListAsync();

        if (testShifts.Count != 0)
        {
            _context.Shift.RemoveRange(testShifts);
            await _context.SaveChangesAsync();
        }
    }

    private async Task<Shift> CreateTestShift(
        string name,
        bool isMonday = false,
        bool isTuesday = false,
        bool isWednesday = false,
        bool isThursday = false,
        bool isFriday = false,
        bool isSaturday = false,
        bool isSunday = false,
        bool isHoliday = false,
        bool isWeekdayAndHoliday = false,
        bool cuttingAfterMidnight = false,
        DateOnly? fromDate = null,
        DateOnly? untilDate = null)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = $"TEST_{name}",
            Abbreviation = name[..Math.Min(3, name.Length)].ToUpper(),
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = fromDate ?? DateOnly.FromDateTime(DateTime.Now.AddYears(-1)),
            UntilDate = untilDate,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 8.0m,
            IsMonday = isMonday,
            IsTuesday = isTuesday,
            IsWednesday = isWednesday,
            IsThursday = isThursday,
            IsFriday = isFriday,
            IsSaturday = isSaturday,
            IsSunday = isSunday,
            IsHoliday = isHoliday,
            IsWeekdayAndHoliday = isWeekdayAndHoliday,
            CuttingAfterMidnight = cuttingAfterMidnight,
            IsSporadic = false,
            IsTimeRange = false,
            IsDeleted = false
        };

        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    #region Weekday Tests

    [Test]
    public async Task GetShiftSchedule_Should_Return_Monday_Shift_On_Mondays()
    {
        // Arrange
        var mondayShift = await CreateTestShift("MondayOnly", isMonday: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday.AddDays(6);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var mondayResults = result.Where(r => r.ShiftId == mondayShift.Id).ToList();
        mondayResults.Should().HaveCount(1);
        mondayResults.Single().DayOfWeek.Should().Be(1);
        mondayResults.Single().Date.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_Tuesday_Shift_On_Tuesdays()
    {
        // Arrange
        var tuesdayShift = await CreateTestShift("TuesdayOnly", isTuesday: true);

        var tuesday = GetNextWeekday(DayOfWeek.Tuesday);
        var startDate = tuesday.AddDays(-1);
        var endDate = tuesday.AddDays(1);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var tuesdayResults = result.Where(r => r.ShiftId == tuesdayShift.Id).ToList();
        tuesdayResults.Should().HaveCount(1);
        tuesdayResults.Single().DayOfWeek.Should().Be(2);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_Wednesday_Shift_On_Wednesdays()
    {
        // Arrange
        var wednesdayShift = await CreateTestShift("WednesdayOnly", isWednesday: true);

        var wednesday = GetNextWeekday(DayOfWeek.Wednesday);
        var startDate = wednesday.AddDays(-1);
        var endDate = wednesday.AddDays(1);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var wednesdayResults = result.Where(r => r.ShiftId == wednesdayShift.Id).ToList();
        wednesdayResults.Should().HaveCount(1);
        wednesdayResults.Single().DayOfWeek.Should().Be(3);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_Thursday_Shift_On_Thursdays()
    {
        // Arrange
        var thursdayShift = await CreateTestShift("ThursdayOnly", isThursday: true);

        var thursday = GetNextWeekday(DayOfWeek.Thursday);
        var startDate = thursday.AddDays(-1);
        var endDate = thursday.AddDays(1);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var thursdayResults = result.Where(r => r.ShiftId == thursdayShift.Id).ToList();
        thursdayResults.Should().HaveCount(1);
        thursdayResults.Single().DayOfWeek.Should().Be(4);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_Friday_Shift_On_Fridays()
    {
        // Arrange
        var fridayShift = await CreateTestShift("FridayOnly", isFriday: true);

        var friday = GetNextWeekday(DayOfWeek.Friday);
        var startDate = friday.AddDays(-1);
        var endDate = friday.AddDays(1);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var fridayResults = result.Where(r => r.ShiftId == fridayShift.Id).ToList();
        fridayResults.Should().HaveCount(1);
        fridayResults.Single().DayOfWeek.Should().Be(5);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_Saturday_Shift_On_Saturdays()
    {
        // Arrange
        var saturdayShift = await CreateTestShift("SaturdayOnly", isSaturday: true);

        var saturday = GetNextWeekday(DayOfWeek.Saturday);
        var startDate = saturday.AddDays(-1);
        var endDate = saturday.AddDays(1);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var saturdayResults = result.Where(r => r.ShiftId == saturdayShift.Id).ToList();
        saturdayResults.Should().HaveCount(1);
        saturdayResults.Single().DayOfWeek.Should().Be(6);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_Sunday_Shift_On_Sundays()
    {
        // Arrange
        var sundayShift = await CreateTestShift("SundayOnly", isSunday: true);

        var sunday = GetNextWeekday(DayOfWeek.Sunday);
        var startDate = sunday.AddDays(-1);
        var endDate = sunday.AddDays(1);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var sundayResults = result.Where(r => r.ShiftId == sundayShift.Id).ToList();
        sundayResults.Should().HaveCount(1);
        sundayResults.Single().DayOfWeek.Should().Be(7);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_Multiple_Days_For_Multi_Day_Shift()
    {
        // Arrange
        var multiDayShift = await CreateTestShift(
            "MonToFri",
            isMonday: true,
            isTuesday: true,
            isWednesday: true,
            isThursday: true,
            isFriday: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday.AddDays(6);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == multiDayShift.Id).ToList();
        shiftResults.Should().HaveCount(5);
        shiftResults.Select(r => r.DayOfWeek).Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    [Test]
    public async Task GetShiftSchedule_Should_Not_Return_Shift_On_Unconfigured_Days()
    {
        // Arrange
        var mondayOnlyShift = await CreateTestShift("MondayOnlyStrict", isMonday: true);

        var tuesday = GetNextWeekday(DayOfWeek.Tuesday);
        var startDate = tuesday;
        var endDate = tuesday.AddDays(5);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == mondayOnlyShift.Id).ToList();
        shiftResults.Should().BeEmpty();
    }

    #endregion

    #region IsHoliday Tests

    [Test]
    public async Task GetShiftSchedule_Should_Return_IsHoliday_Shift_Only_On_Holidays()
    {
        // Arrange
        var holidayShift = await CreateTestShift("HolidayOnly", isHoliday: true);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var holidayDate = today.AddDays(1);
        var startDate = today;
        var endDate = today.AddDays(7);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate, new List<DateOnly> { holidayDate });

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == holidayShift.Id).ToList();
        shiftResults.Should().HaveCount(1);
        shiftResults.Single().Date.Should().Be(holidayDate);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Not_Return_IsHoliday_Shift_On_Regular_Days()
    {
        // Arrange
        var holidayShift = await CreateTestShift("HolidayOnlyNoMatch", isHoliday: true);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var startDate = today;
        var endDate = today.AddDays(7);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == holidayShift.Id).ToList();
        shiftResults.Should().BeEmpty();
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_IsHoliday_Shift_On_Multiple_Holidays()
    {
        // Arrange
        var holidayShift = await CreateTestShift("MultiHoliday", isHoliday: true);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var holiday1 = today.AddDays(2);
        var holiday2 = today.AddDays(5);
        var startDate = today;
        var endDate = today.AddDays(7);

        // Act
        var result = await _service.GetShiftScheduleAsync(
            startDate, endDate,
            new List<DateOnly> { holiday1, holiday2 });

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == holidayShift.Id).ToList();
        shiftResults.Should().HaveCount(2);
        shiftResults.Select(r => r.Date).Should().BeEquivalentTo(new[] { holiday1, holiday2 });
    }

    [Test]
    public async Task GetShiftSchedule_Regular_Weekday_Shift_Should_Not_Appear_On_Holiday()
    {
        // Arrange
        var monday = GetNextWeekday(DayOfWeek.Monday);
        var mondayShift = await CreateTestShift("MondayNoHoliday", isMonday: true);

        var startDate = monday.AddDays(-1);
        var endDate = monday.AddDays(1);

        // Act
        var result = await _service.GetShiftScheduleAsync(
            startDate, endDate,
            new List<DateOnly> { monday });

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == mondayShift.Id).ToList();
        shiftResults.Should().BeEmpty();
    }

    #endregion

    #region IsWeekdayAndHoliday Tests

    [Test]
    public async Task GetShiftSchedule_Should_Return_IsWeekdayAndHoliday_Shift_On_Weekdays()
    {
        // Arrange
        var weekdayAndHolidayShift = await CreateTestShift("WeekdayAndHoliday", isWeekdayAndHoliday: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday.AddDays(4);

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == weekdayAndHolidayShift.Id).ToList();
        shiftResults.Should().HaveCount(5);
        shiftResults.Select(r => r.DayOfWeek).Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_IsWeekdayAndHoliday_Shift_On_Holidays()
    {
        // Arrange
        var weekdayAndHolidayShift = await CreateTestShift("WeekdayAndHolidayOnHoliday", isWeekdayAndHoliday: true);

        var saturday = GetNextWeekday(DayOfWeek.Saturday);
        var startDate = saturday;
        var endDate = saturday.AddDays(1);

        // Act
        var result = await _service.GetShiftScheduleAsync(
            startDate, endDate,
            new List<DateOnly> { saturday });

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == weekdayAndHolidayShift.Id).ToList();
        shiftResults.Should().HaveCount(1);
        shiftResults.Single().Date.Should().Be(saturday);
    }

    [Test]
    public async Task GetShiftSchedule_Should_Not_Return_IsWeekdayAndHoliday_Shift_On_Weekend_Without_Holiday()
    {
        // Arrange
        var weekdayAndHolidayShift = await CreateTestShift("WeekdayAndHolidayNoWeekend", isWeekdayAndHoliday: true);

        var saturday = GetNextWeekday(DayOfWeek.Saturday);
        var sunday = saturday.AddDays(1);
        var startDate = saturday;
        var endDate = sunday;

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == weekdayAndHolidayShift.Id).ToList();
        shiftResults.Should().BeEmpty();
    }

    [Test]
    public async Task GetShiftSchedule_Should_Return_IsWeekdayAndHoliday_Both_Weekday_And_Holiday()
    {
        // Arrange
        var weekdayAndHolidayShift = await CreateTestShift("WeekdayAndHolidayBoth", isWeekdayAndHoliday: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var saturday = monday.AddDays(5);
        var startDate = monday;
        var endDate = saturday;

        // Act
        var result = await _service.GetShiftScheduleAsync(
            startDate, endDate,
            new List<DateOnly> { saturday });

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == weekdayAndHolidayShift.Id).ToList();
        shiftResults.Should().HaveCount(6);
        shiftResults.Where(r => r.DayOfWeek >= 1 && r.DayOfWeek <= 5).Should().HaveCount(5);
        shiftResults.Where(r => r.Date == saturday).Should().HaveCount(1);
    }

    [Test]
    public async Task GetShiftSchedule_IsWeekdayAndHoliday_Should_Not_Duplicate_On_Weekday_Holiday()
    {
        // Arrange
        var weekdayAndHolidayShift = await CreateTestShift("WeekdayAndHolidayNoDup", isWeekdayAndHoliday: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleAsync(
            startDate, endDate,
            new List<DateOnly> { monday });

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == weekdayAndHolidayShift.Id).ToList();
        shiftResults.Should().HaveCount(1);
    }

    #endregion

    #region Combined IsHoliday and IsWeekdayAndHoliday Tests

    [Test]
    public async Task GetShiftSchedule_IsHoliday_And_IsWeekdayAndHoliday_Should_Both_Appear_On_Holiday()
    {
        // Arrange
        var holidayOnlyShift = await CreateTestShift("HolidayOnlyTest", isHoliday: true);
        var weekdayAndHolidayShift = await CreateTestShift("WeekdayAndHolidayTest", isWeekdayAndHoliday: true);

        var saturday = GetNextWeekday(DayOfWeek.Saturday);
        var startDate = saturday;
        var endDate = saturday;

        // Act
        var result = await _service.GetShiftScheduleAsync(
            startDate, endDate,
            new List<DateOnly> { saturday });

        // Assert
        result.Where(r => r.ShiftId == holidayOnlyShift.Id).Should().HaveCount(1);
        result.Where(r => r.ShiftId == weekdayAndHolidayShift.Id).Should().HaveCount(1);
    }

    [Test]
    public async Task GetShiftSchedule_Only_IsWeekdayAndHoliday_Should_Appear_On_Regular_Weekday()
    {
        // Arrange
        var holidayOnlyShift = await CreateTestShift("HolidayOnlyNoWeekday", isHoliday: true);
        var weekdayAndHolidayShift = await CreateTestShift("WeekdayAndHolidayWeekday", isWeekdayAndHoliday: true);

        var tuesday = GetNextWeekday(DayOfWeek.Tuesday);
        var startDate = tuesday;
        var endDate = tuesday;

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        result.Where(r => r.ShiftId == holidayOnlyShift.Id).Should().BeEmpty();
        result.Where(r => r.ShiftId == weekdayAndHolidayShift.Id).Should().HaveCount(1);
    }

    [Test]
    public async Task GetShiftSchedule_Only_IsHoliday_Should_Not_Appear_On_Regular_Weekday()
    {
        // Arrange
        var holidayOnlyShift = await CreateTestShift("HolidayOnlyStrictWeekday", isHoliday: true);

        var wednesday = GetNextWeekday(DayOfWeek.Wednesday);
        var startDate = wednesday;
        var endDate = wednesday;

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        result.Where(r => r.ShiftId == holidayOnlyShift.Id).Should().BeEmpty();
    }

    #endregion

    #region Validity Period Tests

    [Test]
    public async Task GetShiftSchedule_Should_Respect_FromDate()
    {
        // Arrange
        var futureMonday = GetNextWeekday(DayOfWeek.Monday).AddDays(7);
        var futureShift = await CreateTestShift(
            "FutureStart",
            isMonday: true,
            fromDate: futureMonday);

        var currentMonday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = currentMonday;
        var endDate = currentMonday;

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == futureShift.Id).ToList();
        shiftResults.Should().BeEmpty();
    }

    [Test]
    public async Task GetShiftSchedule_Should_Respect_UntilDate()
    {
        // Arrange
        var pastMonday = GetNextWeekday(DayOfWeek.Monday).AddDays(-14);
        var expiredShift = await CreateTestShift(
            "ExpiredShift",
            isMonday: true,
            fromDate: pastMonday.AddDays(-30),
            untilDate: pastMonday);

        var currentMonday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = currentMonday;
        var endDate = currentMonday;

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == expiredShift.Id).ToList();
        shiftResults.Should().BeEmpty();
    }

    #endregion

    #region CuttingAfterMidnight Tests

    [Test]
    public async Task GetShiftSchedule_CuttingAfterMidnight_Should_Show_On_Next_Day()
    {
        // Arrange
        var nightShift = await CreateTestShift(
            "NightShiftSunday",
            isSunday: true,
            cuttingAfterMidnight: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == nightShift.Id).ToList();
        shiftResults.Should().HaveCount(1);
        shiftResults.Single().Date.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Test]
    public async Task GetShiftSchedule_CuttingAfterMidnight_Should_Not_Show_On_Same_Day()
    {
        // Arrange
        var nightShift = await CreateTestShift(
            "NightShiftSundayNoSameDay",
            isSunday: true,
            cuttingAfterMidnight: true);

        var sunday = GetNextWeekday(DayOfWeek.Sunday);
        var startDate = sunday;
        var endDate = sunday;

        // Act
        var result = await _service.GetShiftScheduleAsync(startDate, endDate);

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == nightShift.Id).ToList();
        shiftResults.Should().BeEmpty();
    }

    #endregion

    #region Helper Methods

    private static DateOnly GetNextWeekday(DayOfWeek targetDay)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var daysUntilTarget = ((int)targetDay - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilTarget == 0)
            daysUntilTarget = 7;
        return today.AddDays(daysUntilTarget);
    }

    #endregion
}
