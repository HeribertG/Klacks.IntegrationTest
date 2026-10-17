using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.ShiftSchedule;

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
        _logger = Substitute.For<ILogger<ShiftScheduleService>>();

        _service = new ShiftScheduleService(_context, new Klacks.Api.Infrastructure.Repositories.Associations.ShiftRequiredQualificationRepository(_context, NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<Klacks.Api.Domain.Models.Associations.ShiftRequiredQualification>>()), _logger);

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
        _context.ChangeTracker.Clear();

        var testShiftIds = await _context.Shift
            .Where(s => s.Name.StartsWith("INTEGRATION_TEST_"))
            .Select(s => s.Id)
            .ToListAsync();

        if (testShiftIds.Count != 0)
        {
            await _context.ContainerTemplateItem
                .IgnoreQueryFilters()
                .Where(cti => (cti.ShiftId.HasValue && testShiftIds.Contains(cti.ShiftId.Value))
                    || testShiftIds.Contains(cti.ContainerTemplate.ContainerId))
                .ExecuteDeleteAsync();
            await _context.ContainerTemplate
                .IgnoreQueryFilters()
                .Where(ct => testShiftIds.Contains(ct.ContainerId))
                .ExecuteDeleteAsync();

            var shiftGroupItems = await _context.GroupItem
                .Where(gi => gi.ShiftId.HasValue && testShiftIds.Contains(gi.ShiftId.Value))
                .ToListAsync();
            _context.GroupItem.RemoveRange(shiftGroupItems);

            var testShifts = await _context.Shift
                .Where(s => testShiftIds.Contains(s.Id))
                .ToListAsync();
            _context.Shift.RemoveRange(testShifts);
        }

        var testGroups = await _context.Group
            .Where(g => g.Name.StartsWith("INTEGRATION_TEST_GROUP_"))
            .ToListAsync();

        if (testGroups.Count != 0)
        {
            _context.Group.RemoveRange(testGroups);
        }

        await _context.SaveChangesAsync();
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
        DateOnly? untilDate = null,
        ShiftType shiftType = ShiftType.IsTask)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = $"INTEGRATION_TEST_{name}",
            Abbreviation = name[..Math.Min(3, name.Length)].ToUpper(),
            Status = ShiftStatus.OriginalShift,
            ShiftType = shiftType,
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var mondayResults = result.Where(r => r.ShiftId == mondayShift.Id).ToList();
        mondayResults.Count.ShouldBe(1);
        mondayResults.Single().DayOfWeek.ShouldBe(1);
        mondayResults.Single().Date.DayOfWeek.ShouldBe(DayOfWeek.Monday);
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var tuesdayResults = result.Where(r => r.ShiftId == tuesdayShift.Id).ToList();
        tuesdayResults.Count.ShouldBe(1);
        tuesdayResults.Single().DayOfWeek.ShouldBe(2);
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var wednesdayResults = result.Where(r => r.ShiftId == wednesdayShift.Id).ToList();
        wednesdayResults.Count.ShouldBe(1);
        wednesdayResults.Single().DayOfWeek.ShouldBe(3);
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var thursdayResults = result.Where(r => r.ShiftId == thursdayShift.Id).ToList();
        thursdayResults.Count.ShouldBe(1);
        thursdayResults.Single().DayOfWeek.ShouldBe(4);
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var fridayResults = result.Where(r => r.ShiftId == fridayShift.Id).ToList();
        fridayResults.Count.ShouldBe(1);
        fridayResults.Single().DayOfWeek.ShouldBe(5);
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var saturdayResults = result.Where(r => r.ShiftId == saturdayShift.Id).ToList();
        saturdayResults.Count.ShouldBe(1);
        saturdayResults.Single().DayOfWeek.ShouldBe(6);
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var sundayResults = result.Where(r => r.ShiftId == sundayShift.Id).ToList();
        sundayResults.Count.ShouldBe(1);
        sundayResults.Single().DayOfWeek.ShouldBe(7);
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == multiDayShift.Id).ToList();
        shiftResults.Count.ShouldBe(5);
        shiftResults.Select(r => r.DayOfWeek).ToArray().ShouldBeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == mondayOnlyShift.Id).ToList();
        shiftResults.ShouldBeEmpty();
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate, new List<DateOnly> { holidayDate }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == holidayShift.Id).ToList();
        shiftResults.Count.ShouldBe(1);
        shiftResults.Single().Date.ShouldBe(holidayDate);
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == holidayShift.Id).ToList();
        shiftResults.ShouldBeEmpty();
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
        var result = await _service.GetShiftScheduleQuery(
            startDate, endDate,
            new List<DateOnly> { holiday1, holiday2 }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == holidayShift.Id).ToList();
        shiftResults.Count.ShouldBe(2);
        shiftResults.Select(r => r.Date).ToArray().ShouldBeEquivalentTo(new[] { holiday1, holiday2 });
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
        var result = await _service.GetShiftScheduleQuery(
            startDate, endDate,
            new List<DateOnly> { monday }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == mondayShift.Id).ToList();
        shiftResults.ShouldBeEmpty();
    }

    #endregion

    #region Holiday Option Tests (owner rule 2026-10-17)

    // Owner rule, example Mon-Wed ticked:
    // none  -> Mon-Wed, not when that day is a public holiday
    // FT    -> Mon-Wed plus every public holiday, whatever the weekday
    // +FT   -> exactly the ticked days, also when one is a public holiday; holidays on unticked days do not count

    [Test]
    public async Task GetShiftSchedule_NoHolidayOption_Skips_Holiday_On_Ticked_Day()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var shift = await CreateTestShift("HolidayRuleNone", isMonday: true, isTuesday: true, isWednesday: true);

        // Act
        var dates = await GetScheduledDatesAsync(shift.Id, week);

        // Assert
        dates.ShouldBe(new[] { week.Tuesday, week.Wednesday });
    }

    [Test]
    public async Task GetShiftSchedule_IsHoliday_Adds_Every_Holiday_To_Ticked_Days()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var shift = await CreateTestShift("HolidayRuleFt", isMonday: true, isTuesday: true, isWednesday: true, isHoliday: true);

        // Act
        var dates = await GetScheduledDatesAsync(shift.Id, week);

        // Assert
        dates.ShouldBe(new[] { week.Monday, week.Tuesday, week.Wednesday, week.Friday });
    }

    [Test]
    public async Task GetShiftSchedule_IsHoliday_Without_Ticked_Days_Runs_On_Holidays_Only()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var shift = await CreateTestShift("HolidayRuleFtOnly", isHoliday: true);

        // Act
        var dates = await GetScheduledDatesAsync(shift.Id, week);

        // Assert
        dates.ShouldBe(new[] { week.Monday, week.Friday });
    }

    [Test]
    public async Task GetShiftSchedule_IsWeekdayAndHoliday_Keeps_Holiday_On_Ticked_Day_Only()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var shift = await CreateTestShift("HolidayRulePlusFt", isMonday: true, isTuesday: true, isWednesday: true, isWeekdayAndHoliday: true);

        // Act
        var dates = await GetScheduledDatesAsync(shift.Id, week);

        // Assert
        dates.ShouldBe(new[] { week.Monday, week.Tuesday, week.Wednesday });
    }

    [Test]
    public async Task GetShiftSchedule_IsWeekdayAndHoliday_Without_Ticked_Days_Never_Runs()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var shift = await CreateTestShift("HolidayRulePlusFtNoTicks", isWeekdayAndHoliday: true);

        // Act
        var dates = await GetScheduledDatesAsync(shift.Id, week);

        // Assert
        dates.ShouldBeEmpty();
    }

    [Test]
    public async Task GetShiftSchedule_AllDaysTicked_IsWeekdayAndHoliday_And_IsHoliday_Run_Every_Day_Once()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var nurseShift = await CreateAllDaysShift("HolidayRuleNursePlusFt", isHoliday: false, isWeekdayAndHoliday: true);
        var holidayShift = await CreateAllDaysShift("HolidayRuleNurseFt", isHoliday: true, isWeekdayAndHoliday: false);
        var weekendHoliday = week.Monday.AddDays(5);

        // Act
        var result = await _service.GetShiftScheduleQuery(
            week.Monday, week.Sunday,
            new List<DateOnly> { week.Monday, weekendHoliday }).ToListAsync();

        // Assert
        var everyDay = Enumerable.Range(0, 7).Select(offset => week.Monday.AddDays(offset)).ToArray();
        foreach (var shiftId in new[] { nurseShift.Id, holidayShift.Id })
        {
            result.Where(r => r.ShiftId == shiftId).Select(r => r.Date).OrderBy(d => d).ToArray().ShouldBe(everyDay);
        }
    }

    [Test]
    public async Task GetShiftSchedule_IsHoliday_Respects_UntilDate_On_Holiday()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var shift = await CreateTestShift(
            "HolidayRuleFtExpired",
            isMonday: true,
            isHoliday: true,
            fromDate: week.Monday.AddDays(-30),
            untilDate: week.Monday.AddDays(-1));

        // Act
        var dates = await GetScheduledDatesAsync(shift.Id, week);

        // Assert
        dates.ShouldBeEmpty();
    }

    [Test]
    public async Task GetShiftSchedule_ContainerWithIsWeekdayAndHoliday_Uses_HolidayTemplate_Only_On_Ticked_Days()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var container = await CreateTestShift(
            "HolidayRuleContainerPlusFt",
            isMonday: true, isTuesday: true, isWednesday: true,
            isWeekdayAndHoliday: true,
            shiftType: ShiftType.IsContainer);
        var weekdayTask = await CreateAllDaysShift("HolidayRuleContainerPlusFtWeekdayTask", isHoliday: false, isWeekdayAndHoliday: true);
        var holidayTask = await CreateAllDaysShift("HolidayRuleContainerPlusFtHolidayTask", isHoliday: false, isWeekdayAndHoliday: true);

        foreach (var weekday in TickedStorageWeekdays)
        {
            await CreateContainerTemplateAsync(container.Id, weekday, isHoliday: false, isWeekdayAndHoliday: false, weekdayTask.Id);
            await CreateContainerTemplateAsync(container.Id, weekday, isHoliday: true, isWeekdayAndHoliday: true, holidayTask.Id);
        }

        // Act
        var result = await GetHolidayRuleWeekScheduleAsync(week);

        // Assert
        InTemplateDates(result, weekdayTask.Id).ShouldBe(new[] { week.Tuesday, week.Wednesday });
        InTemplateDates(result, holidayTask.Id).ShouldBe(new[] { week.Monday });
    }

    [Test]
    public async Task GetShiftSchedule_ContainerWithIsHoliday_Uses_HolidayTemplate_On_Every_Holiday()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var container = await CreateTestShift(
            "HolidayRuleContainerFt",
            isMonday: true, isTuesday: true, isWednesday: true,
            isHoliday: true,
            shiftType: ShiftType.IsContainer);
        var weekdayTask = await CreateAllDaysShift("HolidayRuleContainerFtWeekdayTask", isHoliday: false, isWeekdayAndHoliday: true);
        var holidayTask = await CreateAllDaysShift("HolidayRuleContainerFtHolidayTask", isHoliday: false, isWeekdayAndHoliday: true);

        foreach (var weekday in TickedStorageWeekdays)
        {
            await CreateContainerTemplateAsync(container.Id, weekday, isHoliday: false, isWeekdayAndHoliday: false, weekdayTask.Id);
            await CreateContainerTemplateAsync(container.Id, weekday, isHoliday: true, isWeekdayAndHoliday: false, holidayTask.Id);
        }

        // Act
        var result = await GetHolidayRuleWeekScheduleAsync(week);

        // Assert
        InTemplateDates(result, weekdayTask.Id).ShouldBe(new[] { week.Tuesday, week.Wednesday });
        InTemplateDates(result, holidayTask.Id).ShouldBe(new[] { week.Monday, week.Friday });
    }

    [Test]
    public async Task GetShiftSchedule_Container_Ignores_SoftDeleted_TemplateItem()
    {
        // Arrange
        var week = HolidayRuleWeek.Next();
        var container = await CreateTestShift(
            "HolidayRuleContainerDeletedItem",
            isMonday: true, isTuesday: true, isWednesday: true,
            shiftType: ShiftType.IsContainer);
        var task = await CreateAllDaysShift("HolidayRuleContainerDeletedItemTask", isHoliday: false, isWeekdayAndHoliday: true);
        var item = await CreateContainerTemplateAsync(container.Id, TickedStorageWeekdays[1], isHoliday: false, isWeekdayAndHoliday: false, task.Id);

        _context.ContainerTemplateItem.Remove(item);
        await _context.SaveChangesAsync();

        // Act
        var result = await GetHolidayRuleWeekScheduleAsync(week);

        // Assert
        InTemplateDates(result, task.Id).ShouldBeEmpty();
    }

    private static readonly int[] TickedStorageWeekdays = [1, 2, 3];

    private sealed record HolidayRuleWeek(DateOnly Monday)
    {
        public DateOnly Tuesday => Monday.AddDays(1);
        public DateOnly Wednesday => Monday.AddDays(2);
        public DateOnly Friday => Monday.AddDays(4);
        public DateOnly Sunday => Monday.AddDays(6);
        public List<DateOnly> Holidays => [Monday, Friday];

        public static HolidayRuleWeek Next() => new(GetNextWeekday(DayOfWeek.Monday));
    }

    private Task<List<ShiftDayAssignment>> GetHolidayRuleWeekScheduleAsync(HolidayRuleWeek week) =>
        _service.GetShiftScheduleQuery(week.Monday, week.Sunday, week.Holidays).ToListAsync();

    private async Task<DateOnly[]> GetScheduledDatesAsync(Guid shiftId, HolidayRuleWeek week)
    {
        var result = await GetHolidayRuleWeekScheduleAsync(week);
        return result.Where(r => r.ShiftId == shiftId).Select(r => r.Date).OrderBy(d => d).ToArray();
    }

    private static DateOnly[] InTemplateDates(IEnumerable<ShiftDayAssignment> result, Guid shiftId) =>
        result.Where(r => r.ShiftId == shiftId && r.IsInTemplateContainer).Select(r => r.Date).OrderBy(d => d).ToArray();

    private Task<Shift> CreateAllDaysShift(string name, bool isHoliday, bool isWeekdayAndHoliday) =>
        CreateTestShift(
            name,
            isMonday: true, isTuesday: true, isWednesday: true, isThursday: true,
            isFriday: true, isSaturday: true, isSunday: true,
            isHoliday: isHoliday,
            isWeekdayAndHoliday: isWeekdayAndHoliday);

    private async Task<ContainerTemplateItem> CreateContainerTemplateAsync(
        Guid containerId, int storageWeekday, bool isHoliday, bool isWeekdayAndHoliday, Guid taskShiftId)
    {
        var template = new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = containerId,
            Weekday = storageWeekday,
            IsHoliday = isHoliday,
            IsWeekdayAndHoliday = isWeekdayAndHoliday,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(16, 0)
        };
        var item = new ContainerTemplateItem
        {
            Id = Guid.NewGuid(),
            ContainerTemplateId = template.Id,
            ShiftId = taskShiftId
        };

        await _context.ContainerTemplate.AddAsync(template);
        await _context.ContainerTemplateItem.AddAsync(item);
        await _context.SaveChangesAsync();
        return item;
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == futureShift.Id).ToList();
        shiftResults.ShouldBeEmpty();
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
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == expiredShift.Id).ToList();
        shiftResults.ShouldBeEmpty();
    }

    #endregion

    #region CuttingAfterMidnight Tests

    [Test]
    public async Task GetShiftSchedule_CuttingAfterMidnight_Should_Show_On_Next_Day()
    {
        // Arrange
        // Frontend shifts weekdays forward when cuttingAfterMidnight = true
        // So a "Sunday night shift" is saved with isMonday = true
        var nightShift = await CreateTestShift(
            "NightShiftSunday",
            isMonday: true,
            cuttingAfterMidnight: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == nightShift.Id).ToList();
        shiftResults.Count.ShouldBe(1);
        shiftResults.Single().Date.DayOfWeek.ShouldBe(DayOfWeek.Monday);
    }

    [Test]
    public async Task GetShiftSchedule_CuttingAfterMidnight_Should_Not_Show_On_Same_Day()
    {
        // Arrange
        // Frontend shifts weekdays forward when cuttingAfterMidnight = true
        // So a "Sunday night shift" is saved with isMonday = true, not isSunday
        var nightShift = await CreateTestShift(
            "NightShiftSundayNoSameDay",
            isMonday: true,
            cuttingAfterMidnight: true);

        var sunday = GetNextWeekday(DayOfWeek.Sunday);
        var startDate = sunday;
        var endDate = sunday;

        // Act
        var result = await _service.GetShiftScheduleQuery(startDate, endDate).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == nightShift.Id).ToList();
        shiftResults.ShouldBeEmpty();
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

    private async Task<Group> CreateTestGroup(string name, Guid? parentId = null)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = $"INTEGRATION_TEST_GROUP_{name}",
            Description = $"Test group {name}",
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            Parent = parentId,
            IsDeleted = false
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();
        return group;
    }

    private async Task AssignShiftToGroup(Guid shiftId, Guid groupId)
    {
        var groupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            GroupId = groupId,
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            IsDeleted = false
        };

        await _context.GroupItem.AddAsync(groupItem);
        await _context.SaveChangesAsync();
    }

    #endregion

    #region Group Filter Tests

    [Test]
    public async Task GetShiftSchedule_WithGroupFilter_Should_Return_Shift_Without_Group_When_ShowUngrouped()
    {
        // Arrange
        var group = await CreateTestGroup("FilterTest1");
        var shiftWithoutGroup = await CreateTestShift("NoGroupShift", isMonday: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { group.Id },
            showUngroupedShifts: true).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftWithoutGroup.Id).ToList();
        shiftResults.Count.ShouldBe(1, "Shifts without group should be returned when showUngroupedShifts is true");
    }

    [Test]
    public async Task GetShiftSchedule_WithGroupFilter_Should_Return_Shift_In_Selected_Group()
    {
        // Arrange
        var group = await CreateTestGroup("FilterTest2");
        var shiftInGroup = await CreateTestShift("InGroupShift", isMonday: true);
        await AssignShiftToGroup(shiftInGroup.Id, group.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { group.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftInGroup.Id).ToList();
        shiftResults.Count.ShouldBe(1, "Shift in selected group should be returned");
    }

    [Test]
    public async Task GetShiftSchedule_WithGroupFilter_Should_Not_Return_Shift_In_Different_Group()
    {
        // Arrange
        var groupA = await CreateTestGroup("FilterTestA");
        var groupB = await CreateTestGroup("FilterTestB");
        var shiftInGroupA = await CreateTestShift("InGroupAShift", isMonday: true);
        await AssignShiftToGroup(shiftInGroupA.Id, groupA.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { groupB.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftInGroupA.Id).ToList();
        shiftResults.ShouldBeEmpty("Shift in different group should not be returned");
    }

    [Test]
    public async Task GetShiftSchedule_WithGroupFilter_Should_Return_Shift_In_Child_Group()
    {
        // Arrange
        var parentGroup = await CreateTestGroup("ParentGroup");
        var childGroup = await CreateTestGroup("ChildGroup", parentGroup.Id);
        var shiftInChildGroup = await CreateTestShift("InChildGroupShift", isMonday: true);
        await AssignShiftToGroup(shiftInChildGroup.Id, childGroup.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { parentGroup.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftInChildGroup.Id).ToList();
        shiftResults.Count.ShouldBe(1, "Shift in child group should be returned when parent is selected");
    }

    [Test]
    public async Task GetShiftSchedule_WithGroupFilter_Should_Return_Shift_In_Grandchild_Group()
    {
        // Arrange
        var grandparentGroup = await CreateTestGroup("GrandparentGroup");
        var parentGroup = await CreateTestGroup("ParentGroupNested", grandparentGroup.Id);
        var childGroup = await CreateTestGroup("ChildGroupNested", parentGroup.Id);
        var shiftInGrandchildGroup = await CreateTestShift("InGrandchildGroupShift", isMonday: true);
        await AssignShiftToGroup(shiftInGrandchildGroup.Id, childGroup.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { grandparentGroup.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftInGrandchildGroup.Id).ToList();
        shiftResults.Count.ShouldBe(1, "Shift in grandchild group should be returned when grandparent is selected");
    }

    [Test]
    public async Task GetShiftSchedule_WithGroupFilter_Should_Not_Return_Shift_When_Child_Group_Selected()
    {
        // Arrange
        var parentGroup = await CreateTestGroup("ParentGroupReverse");
        var childGroup = await CreateTestGroup("ChildGroupReverse", parentGroup.Id);
        var shiftInParentGroup = await CreateTestShift("InParentGroupShift", isMonday: true);
        await AssignShiftToGroup(shiftInParentGroup.Id, parentGroup.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { childGroup.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftInParentGroup.Id).ToList();
        shiftResults.ShouldBeEmpty("Shift in parent group should not be returned when child is selected");
    }

    [Test]
    public async Task GetShiftSchedule_WithoutGroupFilter_Should_Return_All_Shifts()
    {
        // Arrange
        var group = await CreateTestGroup("FilterTestAll");
        var shiftWithGroup = await CreateTestShift("WithGroupShift", isMonday: true);
        var shiftWithoutGroup = await CreateTestShift("WithoutGroupShift2", isMonday: true);
        await AssignShiftToGroup(shiftWithGroup.Id, group.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            null).ToListAsync();

        // Assert
        var shiftWithGroupResults = result.Where(r => r.ShiftId == shiftWithGroup.Id).ToList();
        var shiftWithoutGroupResults = result.Where(r => r.ShiftId == shiftWithoutGroup.Id).ToList();
        shiftWithGroupResults.Count.ShouldBe(1, "Shift with group should be returned when no filter");
        shiftWithoutGroupResults.Count.ShouldBe(1, "Shift without group should be returned when no filter");
    }

    [Test]
    public async Task GetShiftSchedule_WithGroupFilter_Should_Return_Shift_With_Multiple_Groups_If_One_Matches()
    {
        // Arrange
        var groupA = await CreateTestGroup("MultiGroupA");
        var groupB = await CreateTestGroup("MultiGroupB");
        var shiftWithMultipleGroups = await CreateTestShift("MultiGroupShift", isMonday: true);
        await AssignShiftToGroup(shiftWithMultipleGroups.Id, groupA.Id);
        await AssignShiftToGroup(shiftWithMultipleGroups.Id, groupB.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { groupA.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftWithMultipleGroups.Id).ToList();
        shiftResults.Count.ShouldBe(1, "Shift with multiple groups should be returned if one matches");
    }

    #endregion

    #region Visibility Filter Tests (Non-Admin with GroupVisibility)

    [Test]
    public async Task GetShiftSchedule_WithVisibleGroups_Should_Return_Shift_Without_Group_When_ShowUngrouped()
    {
        // Arrange
        var visibleGroup = await CreateTestGroup("VisibleGroup1");
        var shiftWithoutGroup = await CreateTestShift("NoGroupVisibility", isMonday: true);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { visibleGroup.Id },
            showUngroupedShifts: true).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftWithoutGroup.Id).ToList();
        shiftResults.Count.ShouldBe(1, "Shifts without group should be visible when showUngroupedShifts is true");
    }

    [Test]
    public async Task GetShiftSchedule_WithVisibleGroups_Should_Return_Shift_In_Visible_Group()
    {
        // Arrange
        var visibleGroup = await CreateTestGroup("VisibleGroup2");
        var shiftInVisibleGroup = await CreateTestShift("InVisibleGroup", isMonday: true);
        await AssignShiftToGroup(shiftInVisibleGroup.Id, visibleGroup.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { visibleGroup.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftInVisibleGroup.Id).ToList();
        shiftResults.Count.ShouldBe(1, "Shift in visible group should be returned");
    }

    [Test]
    public async Task GetShiftSchedule_WithVisibleGroups_Should_Not_Return_Shift_In_Non_Visible_Group()
    {
        // Arrange
        var visibleGroup = await CreateTestGroup("VisibleGroupOnly");
        var nonVisibleGroup = await CreateTestGroup("NonVisibleGroup");
        var shiftInNonVisibleGroup = await CreateTestShift("InNonVisibleGroup", isMonday: true);
        await AssignShiftToGroup(shiftInNonVisibleGroup.Id, nonVisibleGroup.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { visibleGroup.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftInNonVisibleGroup.Id).ToList();
        shiftResults.ShouldBeEmpty("Shift in non-visible group should not be returned");
    }

    [Test]
    public async Task GetShiftSchedule_WithVisibleGroups_Should_Return_Shift_In_Child_Of_Visible_Group()
    {
        // Arrange
        var visibleParentGroup = await CreateTestGroup("VisibleParent");
        var childGroup = await CreateTestGroup("ChildOfVisible", visibleParentGroup.Id);
        var shiftInChildGroup = await CreateTestShift("InChildOfVisible", isMonday: true);
        await AssignShiftToGroup(shiftInChildGroup.Id, childGroup.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { visibleParentGroup.Id }).ToListAsync();

        // Assert
        var shiftResults = result.Where(r => r.ShiftId == shiftInChildGroup.Id).ToList();
        shiftResults.Count.ShouldBe(1, "Shift in child of visible group should be returned");
    }

    [Test]
    public async Task GetShiftSchedule_WithMultipleVisibleGroups_Should_Return_Shifts_In_Any_Visible_Group()
    {
        // Arrange
        var visibleGroup1 = await CreateTestGroup("MultiVisible1");
        var visibleGroup2 = await CreateTestGroup("MultiVisible2");
        var nonVisibleGroup = await CreateTestGroup("MultiNonVisible");

        var shiftInGroup1 = await CreateTestShift("InMultiVisible1", isMonday: true);
        var shiftInGroup2 = await CreateTestShift("InMultiVisible2", isMonday: true);
        var shiftInNonVisible = await CreateTestShift("InMultiNonVisible", isMonday: true);

        await AssignShiftToGroup(shiftInGroup1.Id, visibleGroup1.Id);
        await AssignShiftToGroup(shiftInGroup2.Id, visibleGroup2.Id);
        await AssignShiftToGroup(shiftInNonVisible.Id, nonVisibleGroup.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { visibleGroup1.Id, visibleGroup2.Id }).ToListAsync();

        // Assert
        result.Where(r => r.ShiftId == shiftInGroup1.Id).Count().ShouldBe(1);
        result.Where(r => r.ShiftId == shiftInGroup2.Id).Count().ShouldBe(1);
        result.Where(r => r.ShiftId == shiftInNonVisible.Id).ShouldBeEmpty();
    }

    [Test]
    public async Task GetShiftSchedule_WithEmptyVisibleGroups_Should_Return_All_Shifts()
    {
        // Arrange
        var group = await CreateTestGroup("AdminTestGroup");
        var shiftWithGroup = await CreateTestShift("AdminTestWithGroup", isMonday: true);
        var shiftWithoutGroup = await CreateTestShift("AdminTestWithoutGroup", isMonday: true);
        await AssignShiftToGroup(shiftWithGroup.Id, group.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid>()).ToListAsync();

        // Assert
        result.Where(r => r.ShiftId == shiftWithGroup.Id).Count().ShouldBe(1, "Admin should see shift with group");
        result.Where(r => r.ShiftId == shiftWithoutGroup.Id).Count().ShouldBe(1, "Admin should see shift without group");
    }

    [Test]
    public async Task GetShiftSchedule_WithSingleVisibleGroup_OnlyReturnsShiftsInThatGroup()
    {
        // Arrange
        var targetGroup = await CreateTestGroup("TargetGroup");
        var otherGroup = await CreateTestGroup("OtherGroup");

        var shiftInTarget = await CreateTestShift("InTargetGroup", isMonday: true);
        var shiftInOther = await CreateTestShift("InOtherGroup", isMonday: true);

        await AssignShiftToGroup(shiftInTarget.Id, targetGroup.Id);
        await AssignShiftToGroup(shiftInOther.Id, otherGroup.Id);

        var monday = GetNextWeekday(DayOfWeek.Monday);
        var startDate = monday;
        var endDate = monday;

        // Act
        var result = await _service.GetShiftScheduleQuery(
            startDate,
            endDate,
            null,
            new List<Guid> { targetGroup.Id }).ToListAsync();

        // Assert
        result.Where(r => r.ShiftId == shiftInTarget.Id).Count().ShouldBe(1, "Shift in target group should be returned");
        result.Where(r => r.ShiftId == shiftInOther.Id).ShouldBeEmpty("Shift in other group should not be returned");
    }

    #endregion
}
