using FluentAssertions;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.Macros;
using Klacks.Api.Domain.Services.PeriodHours;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Presentation.DTOs.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace IntegrationTest.WorkSchedule;

[TestFixture]
public class BulkAddWorksIntegrationTests
{
    private DataBaseContext _context = null!;
    private BulkAddWorksCommandHandler _handler = null!;
    private string _connectionString = null!;

    private Guid _testClientId;
    private Guid _testShiftId;
    private Guid _testMacroId;
    private Guid _testContractId;

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
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(Task.FromResult(1));

        var workNotificationService = Substitute.For<IWorkNotificationService>();
        var periodHoursService = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            workNotificationService);

        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Get(Arg.Any<Guid>()).Returns(callInfo =>
        {
            var shiftId = callInfo.Arg<Guid>();
            return _context.Shift.FirstOrDefaultAsync(s => s.Id == shiftId);
        });

        var macroManagementService = new MacroManagementService(
            _context,
            Substitute.For<ILogger<MacroManagementService>>());

        var macroCache = new MacroCache();

        var macroDataProvider = new MacroDataProvider(
            _context,
            Substitute.For<IHolidayCalculatorCache>());

        var macroEngine = new MacroEngine();

        var workMacroService = new WorkMacroService(
            shiftRepository,
            macroManagementService,
            macroCache,
            macroDataProvider,
            macroEngine,
            Substitute.For<ILogger<WorkMacroService>>());

        var workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            unitOfWork,
            Substitute.For<IClientGroupFilterService>(),
            Substitute.For<IClientSearchFilterService>(),
            workMacroService,
            periodHoursService,
            mockHttpContextAccessor);

        var scheduleMapper = new ScheduleMapper();
        var shiftStatsNotificationService = Substitute.For<IShiftStatsNotificationService>();
        var shiftScheduleService = Substitute.For<IShiftScheduleService>();
        shiftScheduleService.GetShiftSchedulePartialAsync(
            Arg.Any<List<(Guid ShiftId, DateOnly Date)>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ShiftDayAssignment>()));

        _handler = new BulkAddWorksCommandHandler(
            workRepository,
            scheduleMapper,
            unitOfWork,
            workNotificationService,
            shiftStatsNotificationService,
            shiftScheduleService,
            periodHoursService,
            mockHttpContextAccessor,
            Substitute.For<ILogger<BulkAddWorksCommandHandler>>());

        await SetupTestData();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestData();
        _context?.Dispose();
    }

    private async Task SetupTestData()
    {
        _testClientId = Guid.NewGuid();
        _testShiftId = Guid.NewGuid();
        _testMacroId = Guid.NewGuid();
        _testContractId = Guid.NewGuid();

        var macro = new Macro
        {
            Id = _testMacroId,
            Name = "AllShift",
            Type = 0,
            Content = @"import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import sarate
import sorate
import guaranteedhours
import fulltime

FUNCTION CalcSegment(StartTime, EndTime, HolidayFlag, WeekdayNum)
    DIM SegmentHours, NightHours, NonNightHours
    DIM NRate, DRate, HasHoliday, IsSaturday, IsSunday

    SegmentHours = TimeToHours(EndTime) - TimeToHours(StartTime)
    IF SegmentHours < 0 THEN SegmentHours = SegmentHours + 24 ENDIF

    NightHours = TimeOverlap(""23:00"", ""06:00"", StartTime, EndTime)
    NonNightHours = SegmentHours - NightHours

    HasHoliday = HolidayFlag = 1
    IsSaturday = WeekdayNum = 6
    IsSunday = WeekdayNum = 7

    NRate = 0
    IF NightHours > 0 THEN NRate = NightRate ENDIF
    IF HasHoliday AndAlso HolidayRate > NRate THEN NRate = HolidayRate ENDIF
    IF IsSaturday AndAlso SaRate > NRate THEN NRate = SaRate ENDIF
    IF IsSunday AndAlso SoRate > NRate THEN NRate = SoRate ENDIF

    DRate = 0
    IF HasHoliday AndAlso HolidayRate > DRate THEN DRate = HolidayRate ENDIF
    IF IsSaturday AndAlso SaRate > DRate THEN DRate = SaRate ENDIF
    IF IsSunday AndAlso SoRate > DRate THEN DRate = SoRate ENDIF

    CalcSegment = NightHours * NRate + NonNightHours * DRate
ENDFUNCTION

DIM TotalBonus, WeekdayNextDay

WeekdayNextDay = (Weekday MOD 7) + 1

IF TimeToHours(UntilHour) <= TimeToHours(FromHour) THEN
    TotalBonus = CalcSegment(FromHour, ""00:00"", Holiday, Weekday)
    TotalBonus = TotalBonus + CalcSegment(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay)
ELSE
    TotalBonus = CalcSegment(FromHour, UntilHour, Holiday, Weekday)
ENDIF

OUTPUT 1, Round(TotalBonus, 2)",
            Description = new MultiLanguage
            {
                De = "Zuschlagsberechnung",
                En = "Surcharge calculation",
                Fr = "Calcul des suppléments",
                It = "Calcolo dei supplementi"
            },
            IsDeleted = false
        };
        _context.Set<Macro>().Add(macro);

        var contract = new Contract
        {
            Id = _testContractId,
            Name = "TEST_Contract",
            SaRate = 0.1m,
            SoRate = 0.1m,
            NightRate = 0.1m,
            HolidayRate = 0.15m,
            GuaranteedHours = 168m,
            FullTime = 100m,
            ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDeleted = false
        };
        _context.Set<Contract>().Add(contract);

        var client = new Klacks.Api.Domain.Models.Staffs.Client
        {
            Id = _testClientId,
            Name = "TEST_BulkAdd",
            FirstName = "Integration",
            IsDeleted = false
        };
        _context.Client.Add(client);

        var clientContract = new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ContractId = _testContractId,
            FromDate = new DateOnly(2025, 1, 1),
            IsActive = true,
            IsDeleted = false
        };
        _context.Set<ClientContract>().Add(clientContract);

        var shift = new Shift
        {
            Id = _testShiftId,
            Name = "TEST_Shift",
            MacroId = _testMacroId,
            StartShift = new TimeOnly(8, 0, 0),
            EndShift = new TimeOnly(16, 0, 0),
            IsDeleted = false
        };
        _context.Shift.Add(shift);

        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestData()
    {
        var works = await _context.Work
            .Where(w => w.ClientId == _testClientId)
            .ToListAsync();
        _context.Work.RemoveRange(works);

        var clientContracts = await _context.Set<ClientContract>()
            .Where(cc => cc.ClientId == _testClientId)
            .ToListAsync();
        _context.Set<ClientContract>().RemoveRange(clientContracts);

        var client = await _context.Client.FindAsync(_testClientId);
        if (client != null)
        {
            _context.Client.Remove(client);
        }

        var contract = await _context.Set<Contract>().FindAsync(_testContractId);
        if (contract != null)
        {
            _context.Set<Contract>().Remove(contract);
        }

        var shift = await _context.Shift.FindAsync(_testShiftId);
        if (shift != null)
        {
            _context.Shift.Remove(shift);
        }

        var macro = await _context.Set<Macro>().FindAsync(_testMacroId);
        if (macro != null)
        {
            _context.Set<Macro>().Remove(macro);
        }

        var periodHours = await _context.ClientPeriodHours
            .Where(p => p.ClientId == _testClientId)
            .ToListAsync();
        _context.ClientPeriodHours.RemoveRange(periodHours);

        await _context.SaveChangesAsync();
    }

    private BulkWorkItem CreateWorkItem(DateTime date)
    {
        return new BulkWorkItem
        {
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = date,
            WorkTime = 8,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0)
        };
    }

    [Test]
    public async Task BulkAddWorks_Saturday_ShouldReturnCorrectPeriodHours()
    {
        // Arrange
        var saturday = new DateTime(2025, 1, 18, 0, 0, 0, DateTimeKind.Utc);

        var request = new BulkAddWorksRequest
        {
            Works = [CreateWorkItem(saturday)],
            PeriodStart = new DateOnly(2025, 1, 1),
            PeriodEnd = new DateOnly(2025, 1, 31)
        };
        var command = new BulkAddWorksCommand(request);

        // Act
        var response = await _handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(1);
        response.FailedCount.Should().Be(0);

        response.PeriodHours.Should().NotBeNull();
        response.PeriodHours.Should().ContainKey(_testClientId);

        var periodHours = response.PeriodHours![_testClientId];
        periodHours.Hours.Should().Be(8m, "Saturday work: 8 hours");
        periodHours.Surcharges.Should().Be(0.8m, "Saturday surcharge: 10% of 8 hours");
    }

    [Test]
    public async Task BulkAddWorks_Sunday_ShouldReturnCorrectPeriodHours()
    {
        // Arrange
        var sunday = new DateTime(2025, 1, 19, 0, 0, 0, DateTimeKind.Utc);

        var request = new BulkAddWorksRequest
        {
            Works = [CreateWorkItem(sunday)],
            PeriodStart = new DateOnly(2025, 1, 1),
            PeriodEnd = new DateOnly(2025, 1, 31)
        };
        var command = new BulkAddWorksCommand(request);

        // Act
        var response = await _handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(1);
        response.FailedCount.Should().Be(0);
        response.PeriodHours.Should().NotBeNull();
        response.PeriodHours.Should().ContainKey(_testClientId);

        var periodHours = response.PeriodHours![_testClientId];
        periodHours.Hours.Should().Be(8m, "Sunday work: 8 hours");
        periodHours.Surcharges.Should().Be(0.8m, "Sunday surcharge: 10% of 8 hours");
    }

    [Test]
    public async Task BulkAddWorks_Monday_ShouldReturnCorrectPeriodHours()
    {
        // Arrange
        var monday = new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc);

        var request = new BulkAddWorksRequest
        {
            Works = [CreateWorkItem(monday)],
            PeriodStart = new DateOnly(2025, 1, 1),
            PeriodEnd = new DateOnly(2025, 1, 31)
        };
        var command = new BulkAddWorksCommand(request);

        // Act
        var response = await _handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(1);
        response.FailedCount.Should().Be(0);
        response.PeriodHours.Should().NotBeNull();
        response.PeriodHours.Should().ContainKey(_testClientId);

        var periodHours = response.PeriodHours![_testClientId];
        periodHours.Hours.Should().Be(8m, "Monday work: 8 hours");
        periodHours.Surcharges.Should().Be(0m, "Monday: no surcharge");
    }

    [Test]
    public async Task BulkAddWorks_SaturdaySundayMonday_ShouldReturnCorrectPeriodHours()
    {
        // Arrange
        var saturday = new DateTime(2025, 1, 18, 0, 0, 0, DateTimeKind.Utc);
        var sunday = new DateTime(2025, 1, 19, 0, 0, 0, DateTimeKind.Utc);
        var monday = new DateTime(2025, 1, 20, 0, 0, 0, DateTimeKind.Utc);

        var request = new BulkAddWorksRequest
        {
            Works =
            [
                CreateWorkItem(saturday),
                CreateWorkItem(sunday),
                CreateWorkItem(monday)
            ],
            PeriodStart = new DateOnly(2025, 1, 1),
            PeriodEnd = new DateOnly(2025, 1, 31)
        };
        var command = new BulkAddWorksCommand(request);

        // Act
        var response = await _handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(3);
        response.FailedCount.Should().Be(0);
        response.CreatedIds.Should().HaveCount(3);

        response.PeriodHours.Should().NotBeNull();
        response.PeriodHours.Should().ContainKey(_testClientId);

        var periodHours = response.PeriodHours![_testClientId];
        periodHours.Hours.Should().Be(24m, "3 works @ 8 hours each");
        periodHours.Surcharges.Should().Be(1.6m, "Saturday 0.8 + Sunday 0.8");
    }

    [Test]
    public async Task BulkAddWorks_Holiday_ShouldReturnCorrectPeriodHours()
    {
        // Arrange
        var newYear = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var mockMacroData = new MacroData
        {
            Hour = 8,
            FromHour = "08:00",
            UntilHour = "16:00",
            Weekday = 3,
            Holiday = true,
            HolidayNextDay = false,
            NightRate = 0.1m,
            HolidayRate = 0.15m,
            SaRate = 0.1m,
            SoRate = 0.1m,
            GuaranteedHours = 168m,
            FullTime = 100m
        };

        var mockMacroDataProvider = Substitute.For<IMacroDataProvider>();
        mockMacroDataProvider.GetMacroDataAsync(Arg.Any<Work>()).Returns(mockMacroData);

        var handler = CreateHandlerWithMockedMacroDataProvider(mockMacroDataProvider);

        var request = new BulkAddWorksRequest
        {
            Works = [CreateWorkItem(newYear)],
            PeriodStart = new DateOnly(2025, 1, 1),
            PeriodEnd = new DateOnly(2025, 1, 31)
        };
        var command = new BulkAddWorksCommand(request);

        // Act
        var response = await handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(1);
        response.FailedCount.Should().Be(0);
        response.PeriodHours.Should().NotBeNull();
        response.PeriodHours.Should().ContainKey(_testClientId);

        var periodHours = response.PeriodHours![_testClientId];
        periodHours.Hours.Should().Be(8m, "Holiday work: 8 hours");
        periodHours.Surcharges.Should().Be(1.2m, "Holiday surcharge: 15% of 8 hours = 1.2");
    }

    private BulkAddWorksCommandHandler CreateHandlerWithMockedMacroDataProvider(IMacroDataProvider macroDataProvider)
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(Task.FromResult(1));

        var workNotificationService = Substitute.For<IWorkNotificationService>();
        var periodHoursService = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            workNotificationService);

        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Get(Arg.Any<Guid>()).Returns(callInfo =>
        {
            var shiftId = callInfo.Arg<Guid>();
            return _context.Shift.FirstOrDefaultAsync(s => s.Id == shiftId);
        });

        var macroManagementService = new MacroManagementService(
            _context,
            Substitute.For<ILogger<MacroManagementService>>());

        var macroCache = new MacroCache();
        var macroEngine = new MacroEngine();

        var workMacroService = new WorkMacroService(
            shiftRepository,
            macroManagementService,
            macroCache,
            macroDataProvider,
            macroEngine,
            Substitute.For<ILogger<WorkMacroService>>());

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();

        var workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            unitOfWork,
            Substitute.For<IClientGroupFilterService>(),
            Substitute.For<IClientSearchFilterService>(),
            workMacroService,
            periodHoursService,
            mockHttpContextAccessor);

        var scheduleMapper = new ScheduleMapper();
        var shiftStatsNotificationService = Substitute.For<IShiftStatsNotificationService>();
        var shiftScheduleService = Substitute.For<IShiftScheduleService>();
        shiftScheduleService.GetShiftSchedulePartialAsync(
            Arg.Any<List<(Guid ShiftId, DateOnly Date)>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ShiftDayAssignment>()));

        return new BulkAddWorksCommandHandler(
            workRepository,
            scheduleMapper,
            unitOfWork,
            workNotificationService,
            shiftStatsNotificationService,
            shiftScheduleService,
            periodHoursService,
            mockHttpContextAccessor,
            Substitute.For<ILogger<BulkAddWorksCommandHandler>>());
    }
}
