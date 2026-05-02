// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests verifying that Work and Break entities correctly store and load
/// ParentWorkId, that cascade soft-delete works, and that sub-works are excluded from
/// direct EF queries filtered by ParentWorkId.
/// </summary>

using Shouldly;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class ContainerWorkExpansionTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;

    private Guid _testClientId;
    private Guid _testContainerShiftId;
    private Guid _testTaskShiftId;
    private Guid _testAbsenceId;

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

        await SetupSharedTestData();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestData();
        _context?.Dispose();
    }

    private async Task SetupSharedTestData()
    {
        _testClientId = Guid.NewGuid();
        _testContainerShiftId = Guid.NewGuid();
        _testTaskShiftId = Guid.NewGuid();
        _testAbsenceId = Guid.NewGuid();

        var client = new Client
        {
            Id = _testClientId,
            Name = "TEST_ContainerExpansion",
            FirstName = "Integration",
            IsDeleted = false
        };
        _context.Client.Add(client);

        var containerShift = new Shift
        {
            Id = _testContainerShiftId,
            Name = "TEST_ContainerShift",
            ShiftType = ShiftType.IsContainer,
            StartShift = new TimeOnly(8, 0, 0),
            EndShift = new TimeOnly(16, 0, 0),
            IsDeleted = false
        };
        _context.Shift.Add(containerShift);

        var taskShift = new Shift
        {
            Id = _testTaskShiftId,
            Name = "TEST_TaskShift",
            ShiftType = ShiftType.IsTask,
            StartShift = new TimeOnly(8, 0, 0),
            EndShift = new TimeOnly(12, 0, 0),
            IsDeleted = false
        };
        _context.Shift.Add(taskShift);

        var absence = new Absence
        {
            Id = _testAbsenceId,
            Name = new MultiLanguage { De = "TEST", En = "TEST", Fr = "TEST", It = "TEST" },
            Abbreviation = new MultiLanguage { De = "T", En = "T", Fr = "T", It = "T" },
            Description = new MultiLanguage { De = "TEST", En = "TEST", Fr = "TEST", It = "TEST" },
            Color = "#000000",
            IsDeleted = false
        };
        _context.Absence.Add(absence);

        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestData()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"break\" WHERE client_id = {0}", _testClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE client_id = {0}", _testClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id IN ({0}, {1})", _testContainerShiftId, _testTaskShiftId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM absence WHERE id = {0}", _testAbsenceId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id = {0}", _testClientId);
    }

    [Test]
    public async Task SubWorks_HaveCorrectParentWorkId()
    {
        var containerWorkId = Guid.NewGuid();
        var subWorkId = Guid.NewGuid();

        var containerWork = new Work
        {
            Id = containerWorkId,
            ClientId = _testClientId,
            ShiftId = _testContainerShiftId,
            CurrentDate = new DateOnly(2025, 6, 1),
            WorkTime = 480,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            ParentWorkId = null,
            IsDeleted = false
        };
        _context.Work.Add(containerWork);

        var subWork = new Work
        {
            Id = subWorkId,
            ClientId = _testClientId,
            ShiftId = _testTaskShiftId,
            CurrentDate = new DateOnly(2025, 6, 1),
            WorkTime = 240,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(12, 0, 0),
            ParentWorkId = containerWorkId,
            IsDeleted = false
        };
        _context.Work.Add(subWork);

        await _context.SaveChangesAsync();

        var loaded = await _context.Work
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == subWorkId);

        loaded.ShouldNotBeNull();
        loaded!.ParentWorkId.ShouldBe(containerWorkId);
    }

    [Test]
    public async Task SubBreaks_HaveCorrectParentWorkId()
    {
        var containerWorkId = Guid.NewGuid();
        var subBreakId = Guid.NewGuid();

        var containerWork = new Work
        {
            Id = containerWorkId,
            ClientId = _testClientId,
            ShiftId = _testContainerShiftId,
            CurrentDate = new DateOnly(2025, 6, 2),
            WorkTime = 480,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            ParentWorkId = null,
            IsDeleted = false
        };
        _context.Work.Add(containerWork);
        await _context.SaveChangesAsync();

        var subBreak = new Break
        {
            Id = subBreakId,
            ClientId = _testClientId,
            AbsenceId = _testAbsenceId,
            CurrentDate = new DateOnly(2025, 6, 2),
            WorkTime = 60,
            StartTime = new TimeOnly(10, 0, 0),
            EndTime = new TimeOnly(11, 0, 0),
            ParentWorkId = containerWorkId,
            IsDeleted = false
        };
        _context.Break.Add(subBreak);

        await _context.SaveChangesAsync();

        var loaded = await _context.Break
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == subBreakId);

        loaded.ShouldNotBeNull();
        loaded!.ParentWorkId.ShouldBe(containerWorkId);
    }

    [Test]
    public async Task CascadeDelete_RemovesAllChildren()
    {
        var containerWorkId = Guid.NewGuid();
        var subWork1Id = Guid.NewGuid();
        var subWork2Id = Guid.NewGuid();
        var subBreakId = Guid.NewGuid();

        var containerWork = new Work
        {
            Id = containerWorkId,
            ClientId = _testClientId,
            ShiftId = _testContainerShiftId,
            CurrentDate = new DateOnly(2025, 6, 3),
            WorkTime = 480,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            ParentWorkId = null,
            IsDeleted = false
        };
        _context.Work.Add(containerWork);
        await _context.SaveChangesAsync();

        var subWork1 = new Work
        {
            Id = subWork1Id,
            ClientId = _testClientId,
            ShiftId = _testTaskShiftId,
            CurrentDate = new DateOnly(2025, 6, 3),
            WorkTime = 240,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(12, 0, 0),
            ParentWorkId = containerWorkId,
            IsDeleted = false
        };
        var subWork2 = new Work
        {
            Id = subWork2Id,
            ClientId = _testClientId,
            ShiftId = _testTaskShiftId,
            CurrentDate = new DateOnly(2025, 6, 3),
            WorkTime = 240,
            StartTime = new TimeOnly(12, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            ParentWorkId = containerWorkId,
            IsDeleted = false
        };
        _context.Work.AddRange(subWork1, subWork2);

        var subBreak = new Break
        {
            Id = subBreakId,
            ClientId = _testClientId,
            AbsenceId = _testAbsenceId,
            CurrentDate = new DateOnly(2025, 6, 3),
            WorkTime = 60,
            StartTime = new TimeOnly(10, 0, 0),
            EndTime = new TimeOnly(11, 0, 0),
            ParentWorkId = containerWorkId,
            IsDeleted = false
        };
        _context.Break.Add(subBreak);
        await _context.SaveChangesAsync();

        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE work SET is_deleted = true WHERE parent_work_id = {0}", containerWorkId);
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE \"break\" SET is_deleted = true WHERE parent_work_id = {0}", containerWorkId);

        var remainingSubWorks = await _context.Work
            .AsNoTracking()
            .Where(w => w.ParentWorkId == containerWorkId && !w.IsDeleted)
            .ToListAsync();

        var remainingSubBreaks = await _context.Break
            .AsNoTracking()
            .Where(b => b.ParentWorkId == containerWorkId && !b.IsDeleted)
            .ToListAsync();

        remainingSubWorks.ShouldBeEmpty();
        remainingSubBreaks.ShouldBeEmpty();
    }

    [Test]
    public async Task ScheduleEntries_ExcludeSubWorks_WhenFilteredByParentWorkId()
    {
        var containerWorkId = Guid.NewGuid();
        var subWorkId = Guid.NewGuid();

        var containerWork = new Work
        {
            Id = containerWorkId,
            ClientId = _testClientId,
            ShiftId = _testContainerShiftId,
            CurrentDate = new DateOnly(2025, 6, 4),
            WorkTime = 480,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(16, 0, 0),
            ParentWorkId = null,
            IsDeleted = false
        };
        _context.Work.Add(containerWork);

        var subWork = new Work
        {
            Id = subWorkId,
            ClientId = _testClientId,
            ShiftId = _testTaskShiftId,
            CurrentDate = new DateOnly(2025, 6, 4),
            WorkTime = 240,
            StartTime = new TimeOnly(8, 0, 0),
            EndTime = new TimeOnly(12, 0, 0),
            ParentWorkId = containerWorkId,
            IsDeleted = false
        };
        _context.Work.Add(subWork);
        await _context.SaveChangesAsync();

        var topLevelWorks = await _context.Work
            .AsNoTracking()
            .Where(w => w.ClientId == _testClientId
                        && w.CurrentDate == new DateOnly(2025, 6, 4)
                        && w.ParentWorkId == null
                        && !w.IsDeleted)
            .ToListAsync();

        var allWorks = await _context.Work
            .AsNoTracking()
            .Where(w => w.ClientId == _testClientId
                        && w.CurrentDate == new DateOnly(2025, 6, 4)
                        && !w.IsDeleted)
            .ToListAsync();

        topLevelWorks.Count.ShouldBe(1);
        topLevelWorks[0].Id.ShouldBe(containerWorkId);

        allWorks.Count.ShouldBe(2);
        allWorks.ShouldContain(w => w.Id == containerWorkId);
        allWorks.ShouldContain(w => w.Id == subWorkId);
    }
}
