using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class AllShiftMacroTypedOutputTests
{
    private DataBaseContext _context = null!;
    private MacroCompilationService _service = null!;
    private string _connectionString = null!;
    private Guid _macroId;

    private const decimal NightRate = 0.10m;
    private const decimal HolidayRate = 0.50m;
    private const decimal WE1Rate = 0.20m;
    private const decimal WE2Rate = 0.30m;
    private const decimal WE3Rate = 0.25m;

    private const int Monday = 1;
    private const int Friday = 5;
    private const int Saturday = 6;
    private const int Sunday = 7;

    private const string TypedAllShiftMacro = @"IMPORT Hour, FromHour, UntilHour
IMPORT Weekday, Holiday, HolidayNextDay
IMPORT NightRate, HolidayRate, WE1Rate, WE2Rate, WE3Rate
IMPORT WeekendDay1, WeekendDay2, WeekendDay3

FUNCTION SegBonusForType(StartTime, EndTime, HolidayFlag, WeekdayNum, WantType)
    DIM SegmentHours, NightHours, NonNightHours, Amount
    DIM NRate, DRate, NType, DType
    DIM HasHoliday, IsWE1, IsWE2, IsWE3

    SegmentHours = TimeToHours(EndTime) - TimeToHours(StartTime)
    IF SegmentHours < 0 THEN SegmentHours = SegmentHours + 24 ENDIF

    NightHours = TimeOverlap(""23:00"", ""06:00"", StartTime, EndTime)
    NonNightHours = SegmentHours - NightHours

    HasHoliday = HolidayFlag = 1
    IsWE1 = WeekdayNum = WeekendDay1
    IsWE2 = WeekdayNum = WeekendDay2
    IsWE3 = WeekdayNum = WeekendDay3

    NRate = 0
    NType = 0
    IF NightHours > 0 THEN
        NRate = NightRate
        NType = 10
    ENDIF
    IF HasHoliday AndAlso HolidayRate > NRate THEN
        NRate = HolidayRate
        NType = 14
    ENDIF
    IF IsWE1 AndAlso WE1Rate > NRate THEN
        NRate = WE1Rate
        NType = 11
    ENDIF
    IF IsWE2 AndAlso WE2Rate > NRate THEN
        NRate = WE2Rate
        NType = 12
    ENDIF
    IF IsWE3 AndAlso WE3Rate > NRate THEN
        NRate = WE3Rate
        NType = 13
    ENDIF

    DRate = 0
    DType = 0
    IF HasHoliday AndAlso HolidayRate > DRate THEN
        DRate = HolidayRate
        DType = 14
    ENDIF
    IF IsWE1 AndAlso WE1Rate > DRate THEN
        DRate = WE1Rate
        DType = 11
    ENDIF
    IF IsWE2 AndAlso WE2Rate > DRate THEN
        DRate = WE2Rate
        DType = 12
    ENDIF
    IF IsWE3 AndAlso WE3Rate > DRate THEN
        DRate = WE3Rate
        DType = 13
    ENDIF

    Amount = 0
    IF NType = WantType THEN Amount = Amount + NightHours * NRate ENDIF
    IF DType = WantType THEN Amount = Amount + NonNightHours * DRate ENDIF

    SegBonusForType = Amount
ENDFUNCTION

DIM TotalBonus, WeekdayNextDay
DIM BonusNight, BonusWeekend1, BonusWeekend2, BonusWeekend3, BonusHoliday

WeekdayNextDay = (Weekday MOD 7) + 1

IF TimeToHours(UntilHour) <= TimeToHours(FromHour) THEN
    BonusNight = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 10) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 10)
    BonusWeekend1 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 11) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 11)
    BonusWeekend2 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 12) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 12)
    BonusWeekend3 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 13) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 13)
    BonusHoliday = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 14) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 14)
ELSE
    BonusNight = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 10)
    BonusWeekend1 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 11)
    BonusWeekend2 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 12)
    BonusWeekend3 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 13)
    BonusHoliday = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 14)
ENDIF

TotalBonus = BonusNight + BonusWeekend1 + BonusWeekend2 + BonusWeekend3 + BonusHoliday

OUTPUT 1, Round(TotalBonus, 2)
OUTPUT 10, BonusNight
OUTPUT 11, BonusWeekend1
OUTPUT 12, BonusWeekend2
OUTPUT 13, BonusWeekend3
OUTPUT 14, BonusHoliday
";

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

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _macroId = Guid.NewGuid();
        _context.Set<Macro>().Add(new Macro
        {
            Id = _macroId,
            Name = "INTEGRATION_TEST_TypedAllShiftWE123",
            Type = 0,
            Content = TypedAllShiftMacro,
            IsDeleted = false
        });
        await _context.SaveChangesAsync();

        _service = new MacroCompilationService(
            new MacroManagementService(_context, Substitute.For<ILogger<MacroManagementService>>()),
            new MacroCache(),
            new MacroEngine(),
            Substitute.For<ILogger<MacroCompilationService>>());
    }

    [TearDown]
    public async Task TearDown()
    {
        var macro = await _context.Set<Macro>().FindAsync(_macroId);
        if (macro != null)
        {
            _context.Set<Macro>().Remove(macro);
            await _context.SaveChangesAsync();
        }
        _context.Dispose();
    }

    private MacroData BuildData(string from, string until, int weekday, bool holiday = false, bool holidayNext = false)
        => new()
        {
            Hour = 0,
            FromHour = from,
            UntilHour = until,
            Weekday = weekday,
            Holiday = holiday,
            HolidayNextDay = holidayNext,
            NightRate = NightRate,
            HolidayRate = HolidayRate,
            WE1Rate = WE1Rate,
            WE2Rate = WE2Rate,
            WE3Rate = WE3Rate,
            WeekendDay1 = Saturday,
            WeekendDay2 = Sunday,
            WeekendDay3 = Friday
        };

    private static void AssertInvariant(MacroExecutionResult result)
    {
        result.Success.ShouldBeTrue();
        result.ResultValue.HasValue.ShouldBeTrue();
        var typedSum = result.Surcharges.Sum(s => s.Amount);
        Math.Round(typedSum, 2).ShouldBe(result.ResultValue!.Value);
    }

    [Test]
    public async Task SaturdayDayShift_EmitsOnlyWeekend1()
    {
        var result = await _service.CompileAndExecuteAsync(_macroId, BuildData("08:00", "16:00", Saturday));

        AssertInvariant(result);
        result.ResultValue.ShouldBe(1.60m);
        result.Surcharges.Count.ShouldBe(1);
        result.Surcharges[0].Type.ShouldBe(SurchargeType.Weekend1);
        result.Surcharges[0].Amount.ShouldBe(1.60m);
    }

    [Test]
    public async Task SundayDayShift_EmitsOnlyWeekend2()
    {
        var result = await _service.CompileAndExecuteAsync(_macroId, BuildData("08:00", "16:00", Sunday));

        AssertInvariant(result);
        result.ResultValue.ShouldBe(2.40m);
        result.Surcharges.Count.ShouldBe(1);
        result.Surcharges[0].Type.ShouldBe(SurchargeType.Weekend2);
    }

    [Test]
    public async Task FridayConfiguredAsWeekend3_EmitsOnlyWeekend3()
    {
        var result = await _service.CompileAndExecuteAsync(_macroId, BuildData("08:00", "16:00", Friday));

        AssertInvariant(result);
        result.ResultValue.ShouldBe(2.00m);
        result.Surcharges.Count.ShouldBe(1);
        result.Surcharges[0].Type.ShouldBe(SurchargeType.Weekend3);
    }

    [Test]
    public async Task PlainWeekdayDayShift_EmitsNoSurcharges()
    {
        var result = await _service.CompileAndExecuteAsync(_macroId, BuildData("08:00", "16:00", Monday));

        AssertInvariant(result);
        result.ResultValue.ShouldBe(0m);
        result.Surcharges.Count.ShouldBe(0);
    }

    [Test]
    public async Task HolidayBeatsWeekend_MaxRuleKeepsSingleHolidayItem()
    {
        var result = await _service.CompileAndExecuteAsync(_macroId, BuildData("08:00", "16:00", Saturday, holiday: true));

        AssertInvariant(result);
        result.ResultValue.ShouldBe(4.00m);
        result.Surcharges.Count.ShouldBe(1, "MAX rule: holiday wins the segment, must not stack with weekend");
        result.Surcharges[0].Type.ShouldBe(SurchargeType.Holiday);
        result.Surcharges[0].Amount.ShouldBe(4.00m);
    }

    [Test]
    public async Task OvernightSaturdayToSunday_SplitsAcrossWeekend1And2()
    {
        var result = await _service.CompileAndExecuteAsync(_macroId, BuildData("22:00", "06:00", Saturday));

        AssertInvariant(result);
        var types = result.Surcharges.Select(s => s.Type).ToList();
        types.ShouldContain(SurchargeType.Weekend1);
        types.ShouldContain(SurchargeType.Weekend2);
        result.ResultValue.ShouldBe(2.20m);
    }
}
