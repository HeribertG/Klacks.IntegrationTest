using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class BreakRepositoryGroupSealTests
{
    private DataBaseContext _context = null!;
    private BreakRepository _repo = null!;

    private Guid _group1Id;
    private Guid _group2Id;
    private Guid _shift1Id;
    private Guid _shift2Id;
    private Guid _client1Id;
    private Guid _client2Id;
    private Guid _work1Id;
    private Guid _work2Id;
    private Guid _break1Id;
    private Guid _break2Id;

    private static readonly DateOnly TestDate = new DateOnly(2026, 7, 10);
    private static readonly DateOnly PeriodStart = new DateOnly(2026, 7, 1);
    private static readonly DateOnly PeriodEnd = new DateOnly(2026, 7, 31);

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _repo = new BreakRepository(
            _context,
            Substitute.For<ILogger<Break>>());

        await SeedTwoGroupsWithOneClientEach();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.Break.Where(e => e.Id == _break1Id || e.Id == _break2Id).ExecuteDeleteAsync();
        await _context.Work.Where(e => e.Id == _work1Id || e.Id == _work2Id).ExecuteDeleteAsync();
        await _context.GroupItem.Where(e => e.GroupId == _group1Id || e.GroupId == _group2Id).ExecuteDeleteAsync();
        await _context.Group.Where(e => e.Id == _group1Id || e.Id == _group2Id).ExecuteDeleteAsync();
        await _context.Shift.Where(e => e.Id == _shift1Id || e.Id == _shift2Id).ExecuteDeleteAsync();
        await _context.Client.Where(e => e.Id == _client1Id || e.Id == _client2Id).ExecuteDeleteAsync();
        _context.Dispose();
    }

    [Test]
    public async Task SealByPeriodAndGroup_OnlySealsBreaksOfClientsInGroup()
    {
        var affected = await _repo.SealByPeriodAndGroup(
            PeriodStart, PeriodEnd,
            _group1Id,
            WorkLockLevel.Confirmed,
            "test-user");

        affected.Should().Be(1);

        _context.ChangeTracker.Clear();
        var break1 = await _context.Break.FindAsync(_break1Id);
        var break2 = await _context.Break.FindAsync(_break2Id);

        break1!.LockLevel.Should().Be(WorkLockLevel.Confirmed);
        break2!.LockLevel.Should().Be(WorkLockLevel.None);
    }

    [Test]
    public async Task UnsealByPeriodAndGroup_OnlyUnsealsBreaksOfClientsInGroup()
    {
        await _context.Break
            .Where(b => b.Id == _break1Id || b.Id == _break2Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.LockLevel, WorkLockLevel.Confirmed)
                .SetProperty(b => b.SealedAt, DateTime.UtcNow)
                .SetProperty(b => b.SealedBy, "pre-sealer"));

        var affected = await _repo.UnsealByPeriodAndGroup(
            PeriodStart, PeriodEnd,
            _group1Id,
            WorkLockLevel.Confirmed);

        affected.Should().Be(1);

        _context.ChangeTracker.Clear();
        var break1 = await _context.Break.FindAsync(_break1Id);
        var break2 = await _context.Break.FindAsync(_break2Id);

        break1!.LockLevel.Should().Be(WorkLockLevel.None);
        break2!.LockLevel.Should().Be(WorkLockLevel.Confirmed);
    }

    private async Task SeedTwoGroupsWithOneClientEach()
    {
        _group1Id = Guid.NewGuid();
        _group2Id = Guid.NewGuid();
        _shift1Id = Guid.NewGuid();
        _shift2Id = Guid.NewGuid();
        _client1Id = Guid.NewGuid();
        _client2Id = Guid.NewGuid();
        _work1Id = Guid.NewGuid();
        _work2Id = Guid.NewGuid();
        _break1Id = Guid.NewGuid();
        _break2Id = Guid.NewGuid();

        var shift1 = new Shift
        {
            Id = _shift1Id,
            Name = "TEST_BreakGroupSeal_Shift1",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsDeleted = false
        };
        var shift2 = new Shift
        {
            Id = _shift2Id,
            Name = "TEST_BreakGroupSeal_Shift2",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsDeleted = false
        };
        _context.Shift.AddRange(shift1, shift2);

        var group1 = new Group
        {
            Id = _group1Id,
            Name = "TEST_BreakGroupSeal_Group1",
            Description = "Integration test group 1",
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            IsDeleted = false
        };
        var group2 = new Group
        {
            Id = _group2Id,
            Name = "TEST_BreakGroupSeal_Group2",
            Description = "Integration test group 2",
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            IsDeleted = false
        };
        _context.Group.AddRange(group1, group2);

        var groupItem1 = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _group1Id,
            ShiftId = _shift1Id,
            IsDeleted = false
        };
        var groupItem2 = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _group2Id,
            ShiftId = _shift2Id,
            IsDeleted = false
        };
        _context.GroupItem.AddRange(groupItem1, groupItem2);

        var client1 = new Client
        {
            Id = _client1Id,
            Name = "TEST_BreakGroupSeal_Client1",
            FirstName = "Seal",
            IsDeleted = false
        };
        var client2 = new Client
        {
            Id = _client2Id,
            Name = "TEST_BreakGroupSeal_Client2",
            FirstName = "Seal",
            IsDeleted = false
        };
        _context.Client.AddRange(client1, client2);

        await _context.SaveChangesAsync();

        var work1 = new Work
        {
            Id = _work1Id,
            ClientId = _client1Id,
            ShiftId = _shift1Id,
            CurrentDate = TestDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8,
            LockLevel = WorkLockLevel.None,
            IsDeleted = false
        };
        var work2 = new Work
        {
            Id = _work2Id,
            ClientId = _client2Id,
            ShiftId = _shift2Id,
            CurrentDate = TestDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8,
            LockLevel = WorkLockLevel.None,
            IsDeleted = false
        };
        _context.Work.AddRange(work1, work2);
        await _context.SaveChangesAsync();

        var absenceId = await GetAnyAbsenceIdAsync();

        var break1 = new Break
        {
            Id = _break1Id,
            ClientId = _client1Id,
            CurrentDate = TestDate,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(12, 30),
            WorkTime = 0.5m,
            LockLevel = WorkLockLevel.None,
            AbsenceId = absenceId,
            IsDeleted = false
        };
        var break2 = new Break
        {
            Id = _break2Id,
            ClientId = _client2Id,
            CurrentDate = TestDate,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(12, 30),
            WorkTime = 0.5m,
            LockLevel = WorkLockLevel.None,
            AbsenceId = absenceId,
            IsDeleted = false
        };
        _context.Break.AddRange(break1, break2);
        await _context.SaveChangesAsync();
    }

    private async Task<Guid> GetAnyAbsenceIdAsync()
    {
        var absence = await _context.Absence
            .Where(a => !a.IsDeleted)
            .Select(a => a.Id)
            .FirstOrDefaultAsync();

        if (absence == Guid.Empty)
            throw new InvalidOperationException("No absence found in DB for Break seed. Run the DB seed first.");

        return absence;
    }
}
