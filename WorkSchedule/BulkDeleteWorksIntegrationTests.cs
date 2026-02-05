using FluentAssertions;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.PeriodHours;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Settings;
using Klacks.Api.Application.DTOs.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class BulkDeleteWorksIntegrationTests
{
    private DataBaseContext _context = null!;
    private BulkDeleteWorksCommandHandler _handler = null!;
    private string _connectionString = null!;

    private Guid _testClientId;
    private Guid _testShiftId;
    private List<Guid> _testWorkIds = new();

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

        var workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            unitOfWork,
            Substitute.For<IClientGroupFilterService>(),
            Substitute.For<IClientSearchFilterService>(),
            Substitute.For<IWorkMacroService>(),
            periodHoursService,
            mockHttpContextAccessor);

        var scheduleMapper = new ScheduleMapper();
        var shiftStatsNotificationService = Substitute.For<IShiftStatsNotificationService>();
        var shiftScheduleService = Substitute.For<IShiftScheduleService>();
        shiftScheduleService.GetShiftSchedulePartialAsync(
            Arg.Any<List<(Guid ShiftId, DateOnly Date)>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ShiftDayAssignment>()));

        _handler = new BulkDeleteWorksCommandHandler(
            workRepository,
            scheduleMapper,
            unitOfWork,
            workNotificationService,
            shiftStatsNotificationService,
            shiftScheduleService,
            periodHoursService,
            mockHttpContextAccessor,
            Substitute.For<ILogger<BulkDeleteWorksCommandHandler>>());

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
        // Arrange
        _testClientId = Guid.NewGuid();
        _testShiftId = Guid.NewGuid();
        _testWorkIds.Clear();

        var client = new Klacks.Api.Domain.Models.Staffs.Client
        {
            Id = _testClientId,
            Name = "TEST_BulkDelete",
            FirstName = "Integration",
            IsDeleted = false
        };
        _context.Client.Add(client);

        var shift = new Shift
        {
            Id = _testShiftId,
            Name = "TEST_Shift",
            StartShift = new TimeOnly(8, 0, 0),
            EndShift = new TimeOnly(16, 0, 0),
            IsDeleted = false
        };
        _context.Shift.Add(shift);

        await _context.SaveChangesAsync();

        // Create works with surcharges: 3 weekdays, 1 Saturday, 1 Sunday
        var saturday = new DateOnly(2025, 1, 18); // Saturday
        var sunday = new DateOnly(2025, 1, 19);   // Sunday

        // Saturday work: 8 hours with 10% surcharge = 0.8
        var saturdayWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = saturday,
            WorkTime = 8,
            Surcharges = 0.8m,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            IsDeleted = false
        };
        _context.Work.Add(saturdayWork);
        _testWorkIds.Add(saturdayWork.Id);

        // Sunday work: 8 hours with 10% surcharge = 0.8
        var sundayWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = sunday,
            WorkTime = 8,
            Surcharges = 0.8m,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            IsDeleted = false
        };
        _context.Work.Add(sundayWork);
        _testWorkIds.Add(sundayWork.Id);

        // Monday work: 8 hours, no surcharge
        var mondayWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = new DateOnly(2025, 1, 20),
            WorkTime = 8,
            Surcharges = 0m,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            IsDeleted = false
        };
        _context.Work.Add(mondayWork);
        _testWorkIds.Add(mondayWork.Id);

        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestData()
    {
        var works = await _context.Work
            .Where(w => w.ClientId == _testClientId)
            .ToListAsync();
        _context.Work.RemoveRange(works);

        var client = await _context.Client.FindAsync(_testClientId);
        if (client != null)
        {
            _context.Client.Remove(client);
        }

        var shift = await _context.Shift.FindAsync(_testShiftId);
        if (shift != null)
        {
            _context.Shift.Remove(shift);
        }

        var periodHours = await _context.ClientPeriodHours
            .Where(p => p.ClientId == _testClientId)
            .ToListAsync();
        _context.ClientPeriodHours.RemoveRange(periodHours);

        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task BulkDelete_ShouldReturnCorrectPeriodHours_WithSurcharges()
    {
        // Arrange
        // Delete Monday only, keep Saturday + Sunday with surcharges
        var mondayWorkId = _testWorkIds[2];
        var request = new BulkDeleteWorksRequest
        {
            WorkIds = new List<Guid> { mondayWorkId }
        };
        var command = new BulkDeleteWorksCommand(request);

        // Act
        var response = await _handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(1);
        response.FailedCount.Should().Be(0);
        response.DeletedIds.Should().HaveCount(1);

        response.PeriodHours.Should().NotBeNull();
        response.PeriodHours.Should().ContainKey(_testClientId);

        var periodHours = response.PeriodHours![_testClientId];
        periodHours.Hours.Should().Be(16m, "2 remaining works @ 8h each (Saturday + Sunday)");
        periodHours.Surcharges.Should().Be(1.6m, "Saturday 0.8 + Sunday 0.8");
    }

    [Test]
    public async Task BulkDelete_DeleteOnlySaturday_ShouldReturnCorrectSurcharges()
    {
        // Arrange
        // Delete only Saturday work
        var saturdayWorkId = _testWorkIds[0];
        var request = new BulkDeleteWorksRequest
        {
            WorkIds = new List<Guid> { saturdayWorkId }
        };
        var command = new BulkDeleteWorksCommand(request);

        // Act
        var response = await _handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(1);
        response.PeriodHours.Should().NotBeNull();
        response.PeriodHours.Should().ContainKey(_testClientId);

        var periodHours = response.PeriodHours![_testClientId];
        // Remaining: Sunday (8h, 0.8) + Monday (8h, 0.0) = 16h total, 0.8 surcharges
        periodHours.Hours.Should().Be(16m, "2 remaining works @ 8h each");
        periodHours.Surcharges.Should().Be(0.8m, "Only Sunday surcharge remains");
    }

    [Test]
    public async Task BulkDelete_DeleteAllWorks_ShouldReturnZeroPeriodHours()
    {
        // Arrange
        var request = new BulkDeleteWorksRequest
        {
            WorkIds = _testWorkIds
        };
        var command = new BulkDeleteWorksCommand(request);

        // Act
        var response = await _handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(3);
        response.PeriodHours.Should().NotBeNull();
        response.PeriodHours.Should().ContainKey(_testClientId);

        var periodHours = response.PeriodHours![_testClientId];
        periodHours.Hours.Should().Be(0m, "All works deleted");
        periodHours.Surcharges.Should().Be(0m, "All works deleted");
    }

    [Test]
    public async Task BulkDelete_NonExistentWork_ShouldIncreaseFailedCount()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var request = new BulkDeleteWorksRequest
        {
            WorkIds = new List<Guid> { nonExistentId }
        };
        var command = new BulkDeleteWorksCommand(request);

        // Act
        var response = await _handler.Handle(command, CancellationToken.None);

        // Assert
        response.SuccessCount.Should().Be(0);
        response.FailedCount.Should().Be(1);
        response.DeletedIds.Should().BeEmpty("No works were deleted");
    }
}
