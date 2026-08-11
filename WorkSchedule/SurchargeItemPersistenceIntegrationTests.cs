using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.Macros;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class SurchargeItemPersistenceIntegrationTests
{
    private DataBaseContext _context = null!;
    private WorkRepository _workRepository = null!;
    private string _connectionString = null!;

    private Guid _testClientId;
    private Guid _testShiftId;
    private Guid _testMacroId;

    private const string TypedAllShiftMacro = @"
import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import we1rate
import we2rate

FUNCTION SegBonusForType(StartTime, EndTime, HolidayFlag, WeekdayNum, WantType)
    DIM SegmentHours, NightHours, NonNightHours, Amount
    DIM NRate, DRate, NType, DType, HasHoliday, IsSaturday, IsSunday

    SegmentHours = TimeToHours(EndTime) - TimeToHours(StartTime)
    IF SegmentHours < 0 THEN SegmentHours = SegmentHours + 24 ENDIF

    NightHours = TimeOverlap(""23:00"", ""06:00"", StartTime, EndTime)
    NonNightHours = SegmentHours - NightHours

    HasHoliday = HolidayFlag = 1
    IsSaturday = WeekdayNum = 6
    IsSunday = WeekdayNum = 7

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
    IF IsSaturday AndAlso WE1Rate > NRate THEN
        NRate = WE1Rate
        NType = 11
    ENDIF
    IF IsSunday AndAlso WE2Rate > NRate THEN
        NRate = WE2Rate
        NType = 12
    ENDIF

    DRate = 0
    DType = 0
    IF HasHoliday AndAlso HolidayRate > DRate THEN
        DRate = HolidayRate
        DType = 14
    ENDIF
    IF IsSaturday AndAlso WE1Rate > DRate THEN
        DRate = WE1Rate
        DType = 11
    ENDIF
    IF IsSunday AndAlso WE2Rate > DRate THEN
        DRate = WE2Rate
        DType = 12
    ENDIF

    Amount = 0
    IF NType = WantType THEN Amount = Amount + NightHours * NRate ENDIF
    IF DType = WantType THEN Amount = Amount + NonNightHours * DRate ENDIF

    SegBonusForType = Amount
ENDFUNCTION

DIM TotalBonus, WeekdayNextDay
DIM BonusNight, BonusWeekend1, BonusWeekend2, BonusHoliday

WeekdayNextDay = (Weekday MOD 7) + 1

IF TimeToHours(UntilHour) <= TimeToHours(FromHour) THEN
    BonusNight = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 10) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 10)
    BonusWeekend1 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 11) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 11)
    BonusWeekend2 = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 12) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 12)
    BonusHoliday = SegBonusForType(FromHour, ""00:00"", Holiday, Weekday, 14) + SegBonusForType(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay, 14)
ELSE
    BonusNight = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 10)
    BonusWeekend1 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 11)
    BonusWeekend2 = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 12)
    BonusHoliday = SegBonusForType(FromHour, UntilHour, Holiday, Weekday, 14)
ENDIF

TotalBonus = BonusNight + BonusWeekend1 + BonusWeekend2 + BonusHoliday

OUTPUT 1, Round(TotalBonus, 2)
OUTPUT 10, BonusNight
OUTPUT 11, BonusWeekend1
OUTPUT 12, BonusWeekend2
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

        _testClientId = Guid.NewGuid();
        _testShiftId = Guid.NewGuid();
        _testMacroId = Guid.NewGuid();

        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Get(Arg.Any<Guid>()).Returns(ci => _context.Shift.FirstOrDefaultAsync(s => s.Id == ci.Arg<Guid>()));

        var macroDataProvider = Substitute.For<IMacroDataProvider>();
        macroDataProvider.GetMacroDataAsync(Arg.Any<Work>()).Returns(new MacroData
        {
            Hour = 8,
            FromHour = "08:00",
            UntilHour = "16:00",
            Weekday = 6,
            Holiday = false,
            HolidayNextDay = false,
            NightRate = 0.10m,
            HolidayRate = 0.15m,
            WE1Rate = 0.10m,
            WE2Rate = 0.10m
        });

        var macroCompilationService = new MacroCompilationService(
            new MacroManagementService(_context, new MacroCache(), Substitute.For<ILogger<MacroManagementService>>()),
            new MacroCache(),
            new MacroEngine(),
            Substitute.For<ILogger<MacroCompilationService>>());

        var overtimeSurchargeCalculator = Substitute.For<IOvertimeSurchargeCalculator>();
        overtimeSurchargeCalculator.CalculateAsync(Arg.Any<Work>())
            .Returns(OvertimeCalculationResult.None());

        var workMacroService = new WorkMacroService(
            _context, shiftRepository, macroDataProvider, macroCompilationService,
            overtimeSurchargeCalculator, Substitute.For<ILogger<WorkMacroService>>());

        var baseQueryService = new ClientBaseQueryService(
            _context, Substitute.For<IClientGroupFilterService>(), Substitute.For<IClientSearchFilterService>(), new Klacks.Api.Domain.Services.Clients.ClientSearchService(), new Klacks.IntegrationTest.TestHelpers.EmptyClientFuzzySearchService());

        _workRepository = new WorkRepository(
            _context, Substitute.For<ILogger<Work>>(), baseQueryService, workMacroService,
            Substitute.For<IClientContractDataProvider>());

        await SetupTestData();
    }

    [TearDown]
    public async Task TearDown()
    {
        var works = await _context.Work.Where(w => w.ClientId == _testClientId).ToListAsync();
        _context.Work.RemoveRange(works);

        var client = await _context.Client.FindAsync(_testClientId);
        if (client != null) _context.Client.Remove(client);

        var shift = await _context.Shift.FindAsync(_testShiftId);
        if (shift != null) _context.Shift.Remove(shift);

        var macro = await _context.Set<Macro>().FindAsync(_testMacroId);
        if (macro != null) _context.Set<Macro>().Remove(macro);

        await _context.SaveChangesAsync();
        _context.Dispose();
    }

    private async Task SetupTestData()
    {
        _context.Set<Macro>().Add(new Macro
        {
            Id = _testMacroId,
            Name = "TEST_TypedAllShift",
            Type = 0,
            Content = TypedAllShiftMacro,
            IsDeleted = false
        });

        _context.Client.Add(new Klacks.Api.Domain.Models.Staffs.Client
        {
            Id = _testClientId,
            Name = "TEST_SurchargeItem",
            FirstName = "Integration",
            IsDeleted = false
        });

        _context.Shift.Add(new Shift
        {
            Id = _testShiftId,
            Name = "TEST_Shift",
            MacroId = _testMacroId,
            StartShift = new TimeOnly(8, 0, 0),
            EndShift = new TimeOnly(16, 0, 0),
            IsDeleted = false
        });

        await _context.SaveChangesAsync();
    }

    private Work NewSaturdayWork() => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _testClientId,
        ShiftId = _testShiftId,
        CurrentDate = new DateOnly(2025, 1, 18),
        StartTime = new TimeOnly(8, 0, 0),
        EndTime = new TimeOnly(16, 0, 0),
        WorkTime = 8m,
        IsDeleted = false
    };

    private Task<List<SurchargeItem>> ItemsFor(Guid workId) =>
        _context.SurchargeItem.Where(si => si.WorkId == workId).ToListAsync();

    [Test]
    public async Task Add_SaturdayWork_PersistsSingleWeekend1SurchargeItemMatchingTotal()
    {
        var work = NewSaturdayWork();

        await _workRepository.Add(work);
        work.Surcharges.ShouldBe(0.8m);
        work.SurchargeItems.Count.ShouldBe(1);
        await _context.SaveChangesAsync();

        var items = await ItemsFor(work.Id);
        items.Count.ShouldBe(1);
        items[0].Type.ShouldBe(SurchargeType.Weekend1);
        items[0].Amount.ShouldBe(0.8m);
        Math.Round(items.Sum(i => i.Amount), 2).ShouldBe(work.Surcharges);
    }

    [Test]
    public async Task Recompute_ReplacesSurchargeItems_DoesNotAccumulate()
    {
        var work = NewSaturdayWork();

        await _workRepository.Add(work);
        await _context.SaveChangesAsync();
        (await ItemsFor(work.Id)).Count.ShouldBe(1);

        await _workRepository.Put(work);
        await _context.SaveChangesAsync();

        var items = await ItemsFor(work.Id);
        items.Count.ShouldBe(1, "recompute must replace, not accumulate");
        items[0].Type.ShouldBe(SurchargeType.Weekend1);
        items[0].Amount.ShouldBe(0.8m);
    }
}
