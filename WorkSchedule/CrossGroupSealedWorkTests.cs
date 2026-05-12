// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class CrossGroupSealedWorkTests
{
    private DataBaseContext _context = null!;
    private IScheduleEntriesService _service = null!;
    private string _connectionString = null!;

    private Guid _visibleGroupId;
    private Guid _foreignGroupId;
    private Guid _clientId;
    private Guid _clientMembershipId;
    private Guid _shiftInForeignGroupId;
    private Guid _shiftMembershipId;
    private Guid _workId;

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
        var logger = Substitute.For<ILogger<ScheduleEntriesService>>();

        _service = new ScheduleEntriesService(_context, logger);

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
        _visibleGroupId = Guid.NewGuid();
        _foreignGroupId = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _clientMembershipId = Guid.NewGuid();
        _shiftInForeignGroupId = Guid.NewGuid();
        _shiftMembershipId = Guid.NewGuid();
        _workId = Guid.NewGuid();

        _context.Group.Add(new Group
        {
            Id = _visibleGroupId,
            Name = "TEST_VisibleGroup",
            IsDeleted = false
        });
        _context.Group.Add(new Group
        {
            Id = _foreignGroupId,
            Name = "TEST_ForeignGroup",
            IsDeleted = false
        });

        _context.Client.Add(new Client
        {
            Id = _clientId,
            Name = "TEST_CrossGroupClient",
            FirstName = "Integration",
            IsDeleted = false
        });

        _context.Shift.Add(new Shift
        {
            Id = _shiftInForeignGroupId,
            Name = "TEST_ForeignShift",
            StartShift = new TimeOnly(14, 0, 0),
            EndShift = new TimeOnly(22, 0, 0),
            IsDeleted = false
        });

        await _context.SaveChangesAsync();

        _context.GroupItem.Add(new GroupItem
        {
            Id = _clientMembershipId,
            ClientId = _clientId,
            GroupId = _visibleGroupId,
            IsDeleted = false
        });
        _context.GroupItem.Add(new GroupItem
        {
            Id = _shiftMembershipId,
            ShiftId = _shiftInForeignGroupId,
            GroupId = _foreignGroupId,
            IsDeleted = false
        });

        _context.Work.Add(new Work
        {
            Id = _workId,
            ClientId = _clientId,
            ShiftId = _shiftInForeignGroupId,
            CurrentDate = new DateOnly(2026, 5, 5),
            WorkTime = 480,
            StartTime = new TimeOnly(14, 0, 0),
            EndTime = new TimeOnly(22, 0, 0),
            IsDeleted = false
        });

        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestData()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE id = {0}", _workId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE id IN ({0}, {1})",
            _clientMembershipId, _shiftMembershipId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0}", _shiftInForeignGroupId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id = {0}", _clientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE id IN ({0}, {1})",
            _visibleGroupId, _foreignGroupId);
    }

    [Test]
    public async Task GetScheduleEntries_WhenFilteringByVisibleGroup_ReturnsCrossGroupWorkAsSealed()
    {
        var startDate = new DateOnly(2026, 5, 1);
        var endDate = new DateOnly(2026, 5, 31);
        var visibleGroupIds = new List<Guid> { _visibleGroupId };

        var result = await _service
            .GetScheduleEntriesQuery(startDate, endDate, visibleGroupIds, null)
            .Where(e => e.ClientId == _clientId && e.EntryType == 0)
            .ToListAsync();

        result.Count.ShouldBe(1, "the cross-group work must surface in the visible-group view");
        result[0].IsGroupRestricted.ShouldBeTrue(
            "Work to a shift in a foreign group must be returned as sealed " +
            "so the calling group sees the booking and cannot double-book the client.");
        result[0].EntryName.ShouldBe("TEST_ForeignShift");
    }

    [Test]
    public async Task GetScheduleEntries_WhenFilteringByForeignGroup_ReturnsWorkAsEditable()
    {
        var startDate = new DateOnly(2026, 5, 1);
        var endDate = new DateOnly(2026, 5, 31);
        var visibleGroupIds = new List<Guid> { _foreignGroupId };

        var result = await _service
            .GetScheduleEntriesQuery(startDate, endDate, visibleGroupIds, null)
            .Where(e => e.ClientId == _clientId && e.EntryType == 0)
            .ToListAsync();

        result.Count.ShouldBe(1);
        result[0].IsGroupRestricted.ShouldBeFalse(
            "Inside the shift's own group the work must be editable.");
    }

    [Test]
    public async Task GetScheduleEntries_WhenNoGroupFilter_ReturnsWorkAsEditable()
    {
        var startDate = new DateOnly(2026, 5, 1);
        var endDate = new DateOnly(2026, 5, 31);

        var result = await _service
            .GetScheduleEntriesQuery(startDate, endDate, null, null)
            .Where(e => e.ClientId == _clientId && e.EntryType == 0)
            .ToListAsync();

        result.Count.ShouldBe(1);
        result[0].IsGroupRestricted.ShouldBeFalse(
            "Without a group filter the schedule sees everything as native.");
    }
}
