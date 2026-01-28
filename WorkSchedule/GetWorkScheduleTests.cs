using FluentAssertions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ScheduleEntries;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace IntegrationTest.WorkSchedule;

[TestFixture]
public class GetWorkScheduleTests
{
    private DataBaseContext _context = null!;
    private IScheduleEntriesService _service = null!;
    private ILogger<ScheduleEntriesService> _logger = null!;
    private string _connectionString = null!;

    private Guid _testClientId;
    private Guid _testShiftId;
    private Guid _testWorkId;
    private Guid _testWorkChangeId;
    private Guid _testExpensesId;

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
        _logger = Substitute.For<ILogger<ScheduleEntriesService>>();

        _service = new ScheduleEntriesService(_context, _logger);

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
        _testWorkId = Guid.NewGuid();
        _testWorkChangeId = Guid.NewGuid();
        _testExpensesId = Guid.NewGuid();

        var client = new Klacks.Api.Domain.Models.Staffs.Client
        {
            Id = _testClientId,
            Name = "TEST_WorkSchedule",
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

        var work = new Work
        {
            Id = _testWorkId,
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            WorkTime = 480,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            IsSealed = false,
            IsDeleted = false
        };
        _context.Work.Add(work);

        var workChange = new Klacks.Api.Domain.Models.Schedules.WorkChange
        {
            Id = _testWorkChangeId,
            WorkId = _testWorkId,
            Type = Klacks.Api.Domain.Enums.WorkChangeType.CorrectionEnd,
            ChangeTime = 30,
            ToInvoice = true,
            Description = "TEST Überstunden",
            IsDeleted = false
        };
        _context.WorkChange.Add(workChange);

        var expenses = new Expenses
        {
            Id = _testExpensesId,
            WorkId = _testWorkId,
            Amount = 25.50m,
            Taxable = true,
            Description = "TEST Fahrtkosten",
            IsDeleted = false
        };
        _context.Expenses.Add(expenses);

        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestData()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM expenses WHERE id = {0}", _testExpensesId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work_change WHERE id = {0}", _testWorkChangeId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE id = {0}", _testWorkId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0}", _testShiftId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id = {0}", _testClientId);
    }

    [Test]
    public async Task GetWorkSchedule_ReturnsWorkEntry()
    {
        // Arrange
        var startDate = new DateOnly(2025, 1, 1);
        var endDate = new DateOnly(2025, 1, 31);

        // Act
        var result = await _service.GetScheduleEntriesQuery(startDate, endDate)
            .Where(e => e.ClientId == _testClientId && e.EntryType == 0)
            .ToListAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].EntryType.Should().Be(0);
        result[0].EntryName.Should().Be("TEST_Shift");
        result[0].StartTime.Should().Be(new TimeSpan(8, 0, 0));
        result[0].EndTime.Should().Be(new TimeSpan(16, 0, 0));
    }

    [Test]
    public async Task GetWorkSchedule_ReturnsWorkChangeEntry()
    {
        // Arrange
        var startDate = new DateOnly(2025, 1, 1);
        var endDate = new DateOnly(2025, 1, 31);

        // Act
        var result = await _service.GetScheduleEntriesQuery(startDate, endDate)
            .Where(e => e.ClientId == _testClientId && e.EntryType == 1)
            .ToListAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].EntryType.Should().Be(1);
        result[0].ChangeTime.Should().Be(30);
        result[0].Description.Should().Be("TEST Überstunden");
    }

    [Test]
    public async Task GetWorkSchedule_ReturnsExpensesEntry()
    {
        // Arrange
        var startDate = new DateOnly(2025, 1, 1);
        var endDate = new DateOnly(2025, 1, 31);

        // Act
        var result = await _service.GetScheduleEntriesQuery(startDate, endDate)
            .Where(e => e.ClientId == _testClientId && e.EntryType == 2)
            .ToListAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].EntryType.Should().Be(2);
        result[0].Amount.Should().Be(25.50m);
        result[0].Description.Should().Be("TEST Fahrtkosten");
    }

    [Test]
    public async Task GetWorkSchedule_ReturnsAllEntryTypes()
    {
        // Arrange
        var startDate = new DateOnly(2025, 1, 1);
        var endDate = new DateOnly(2025, 1, 31);

        // Act
        var result = await _service.GetScheduleEntriesQuery(startDate, endDate)
            .Where(e => e.ClientId == _testClientId)
            .OrderBy(e => e.EntryType)
            .ToListAsync();

        // Assert
        result.Should().HaveCount(3);
        result[0].EntryType.Should().Be(0);
        result[1].EntryType.Should().Be(1);
        result[2].EntryType.Should().Be(2);
    }

    [Test]
    public async Task GetWorkSchedule_OutOfDateRange_ReturnsEmpty()
    {
        // Arrange
        var startDate = new DateOnly(2025, 2, 1);
        var endDate = new DateOnly(2025, 2, 28);

        // Act
        var result = await _service.GetScheduleEntriesQuery(startDate, endDate)
            .Where(e => e.ClientId == _testClientId)
            .ToListAsync();

        // Assert
        result.Should().BeEmpty();
    }
}
