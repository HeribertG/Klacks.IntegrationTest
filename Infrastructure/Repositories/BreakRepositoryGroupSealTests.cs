using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Exports;
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
    private Guid _memberClientId;
    private Guid _memberBreakId;
    private Guid? _globalSealedDayId;

    private static readonly DateOnly TestDate = new DateOnly(2026, 7, 10);
    private static readonly DateOnly DayWithoutWork = new DateOnly(2026, 7, 12);
    private static readonly DateOnly PeriodStart = new DateOnly(2026, 7, 1);
    private static readonly DateOnly PeriodEnd = new DateOnly(2026, 7, 31);

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
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
        await _context.Break.Where(e => e.Id == _break1Id || e.Id == _break2Id || e.Id == _memberBreakId).ExecuteDeleteAsync();
        await _context.Membership.Where(e => e.ClientId == _memberClientId).ExecuteDeleteAsync();
        await _context.Work.Where(e => e.Id == _work1Id || e.Id == _work2Id).ExecuteDeleteAsync();
        await _context.GroupItem.Where(e => e.GroupId == _group1Id || e.GroupId == _group2Id).ExecuteDeleteAsync();
        await _context.SealedDay.IgnoreQueryFilters().Where(e => e.GroupId == _group1Id || e.GroupId == _group2Id || e.Id == _globalSealedDayId).ExecuteDeleteAsync();
        await _context.Group.Where(e => e.Id == _group1Id || e.Id == _group2Id).ExecuteDeleteAsync();
        await _context.Shift.Where(e => e.Id == _shift1Id || e.Id == _shift2Id).ExecuteDeleteAsync();
        await _context.Client.Where(e => e.Id == _client1Id || e.Id == _client2Id || e.Id == _memberClientId).ExecuteDeleteAsync();
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

        affected.ShouldBe(1);

        _context.ChangeTracker.Clear();
        var break1 = await _context.Break.FindAsync(_break1Id);
        var break2 = await _context.Break.FindAsync(_break2Id);

        break1!.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
        break2!.LockLevel.ShouldBe(WorkLockLevel.None);
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

        affected.Total.ShouldBe(1);

        _context.ChangeTracker.Clear();
        var break1 = await _context.Break.FindAsync(_break1Id);
        var break2 = await _context.Break.FindAsync(_break2Id);

        break1!.LockLevel.ShouldBe(WorkLockLevel.None);
        break2!.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
    }

    [Test]
    public async Task SealByPeriodAndGroup_SealsAbsenceOfGroupMemberOnDayWithoutWork_AndPayrollLoaderReadsIt()
    {
        await SeedGroupMemberWithAbsenceOnDayWithoutWorkAsync(_group1Id);

        var affected = await _repo.SealByPeriodAndGroup(
            PeriodStart, PeriodEnd,
            _group1Id,
            WorkLockLevel.Closed,
            "test-user");

        affected.ShouldBe(2);

        _context.ChangeTracker.Clear();
        var memberBreak = await _context.Break.FindAsync(_memberBreakId);
        var break2 = await _context.Break.FindAsync(_break2Id);
        memberBreak!.LockLevel.ShouldBe(WorkLockLevel.Closed);
        break2!.LockLevel.ShouldBe(WorkLockLevel.None);

        var data = await new PayrollExportDataLoader(_context).LoadAsync(PeriodStart, PeriodEnd, null);

        var member = data.Employees.Single(e => e.ClientId == _memberClientId);
        member.Entries.ShouldContain(e => e.Kind == PayrollEntryKind.Absence && e.Date == DayWithoutWork);
    }

    [Test]
    public async Task SealByPeriodAndGroup_DoesNotSealAbsenceOfOtherGroupsMember()
    {
        await SeedGroupMemberWithAbsenceOnDayWithoutWorkAsync(_group2Id);

        await _repo.SealByPeriodAndGroup(
            PeriodStart, PeriodEnd,
            _group1Id,
            WorkLockLevel.Closed,
            "test-user");

        _context.ChangeTracker.Clear();
        var memberBreak = await _context.Break.FindAsync(_memberBreakId);
        memberBreak!.LockLevel.ShouldBe(WorkLockLevel.None);
    }

    [Test]
    public async Task ReopenByOtherGroup_LeavesBreakSealedByFirstGroupClosed()
    {
        await SeedMemberOfBothGroupsAsync();
        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed, "closer-a");

        var reopened = await _repo.UnsealByPeriodAndGroup(PeriodStart, PeriodEnd, _group2Id, WorkLockLevel.Closed);

        _context.ChangeTracker.Clear();
        var memberBreak = await _context.Break.FindAsync(_memberBreakId);
        memberBreak!.LockLevel.ShouldBe(WorkLockLevel.Closed);
        memberBreak.SealedByGroupId.ShouldBe(_group1Id);
        reopened.Total.ShouldBe(0);
    }

    [Test]
    public async Task ReopenByOwningGroup_UnsealsAndClearsOwner()
    {
        await SeedMemberOfBothGroupsAsync();
        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed, "closer-a");

        await _repo.UnsealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed);

        _context.ChangeTracker.Clear();
        var memberBreak = await _context.Break.FindAsync(_memberBreakId);
        memberBreak!.LockLevel.ShouldBe(WorkLockLevel.None);
        memberBreak.SealedByGroupId.ShouldBeNull();
    }

    [Test]
    public async Task SealBySecondGroup_KeepsFirstGroupAsOwner()
    {
        await SeedMemberOfBothGroupsAsync();
        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed, "closer-a");

        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group2Id, WorkLockLevel.Closed, "closer-b");

        _context.ChangeTracker.Clear();
        var memberBreak = await _context.Break.FindAsync(_memberBreakId);
        memberBreak!.SealedByGroupId.ShouldBe(_group1Id);
        memberBreak.SealedBy.ShouldBe("closer-a");
    }

    [Test]
    public async Task LegacySealWithoutOwner_IsReopenedByTheGroupOfTheSameDayWork()
    {
        await _context.Break
            .Where(b => b.Id == _break2Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.LockLevel, WorkLockLevel.Closed)
                .SetProperty(b => b.SealedByGroupId, (Guid?)null));

        await _repo.UnsealByPeriodAndGroup(PeriodStart, PeriodEnd, _group2Id, WorkLockLevel.Closed);

        _context.ChangeTracker.Clear();
        var break2 = await _context.Break.FindAsync(_break2Id);
        break2!.LockLevel.ShouldBe(WorkLockLevel.None);
    }

    [Test]
    public async Task EmptyGroupSealedDay_LocksMember_NotOtherGroupsMember()
    {
        await SeedGroupMemberWithAbsenceOnDayWithoutWorkAsync(_group1Id);
        var sealedDays = new SealedDayRepository(_context);
        var emptyDay = DayWithoutWork.AddDays(1);
        await sealedDays.AddAsync(new SealedDay
        {
            Date = emptyDay,
            GroupId = _group1Id,
            Level = WorkLockLevel.Closed,
            SealedAt = DateTime.UtcNow,
            SealedBy = "test-user"
        });
        await _context.SaveChangesAsync();

        (await sealedDays.IsDayLockedAsync(emptyDay, _memberClientId)).ShouldBeTrue();
        (await sealedDays.GetLockedPairsAsync([(emptyDay, _memberClientId), (emptyDay, _client2Id)]))
            .ShouldBe(new[] { (emptyDay, _memberClientId) });
        (await sealedDays.FindFirstLockedDateForClientAsync(PeriodStart, PeriodEnd, _memberClientId)).ShouldBe(emptyDay);
        (await sealedDays.IsDayLockedAsync(emptyDay, _client2Id)).ShouldBeFalse();
    }

    [Test]
    public async Task LegacySealWithoutOwner_OnDayWithoutWork_SurvivesGroupReopen()
    {
        await SeedGroupMemberWithAbsenceOnDayWithoutWorkAsync(_group1Id);
        _globalSealedDayId = Guid.NewGuid();
        _context.SealedDay.Add(new SealedDay
        {
            Id = _globalSealedDayId.Value,
            Date = DayWithoutWork,
            GroupId = null,
            Level = WorkLockLevel.Closed,
            SealedAt = DateTime.UtcNow,
            SealedBy = "global-closer"
        });
        await _context.SaveChangesAsync();
        await _context.Break
            .Where(b => b.Id == _memberBreakId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.LockLevel, WorkLockLevel.Closed)
                .SetProperty(b => b.SealedByGroupId, (Guid?)null));

        await _repo.UnsealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed);

        _context.ChangeTracker.Clear();
        var memberBreak = await _context.Break.FindAsync(_memberBreakId);
        memberBreak!.LockLevel.ShouldBe(WorkLockLevel.Closed);
    }

    [Test]
    public async Task ReopenByFirstCloser_WhileOtherGroupStillSealed_KeepsClosed_AndTransfersOwner()
    {
        await SeedMemberOfBothGroupsAsync();
        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group2Id, WorkLockLevel.Closed, "closer-b");
        await AddGroupSealedDayAsync(_group2Id);
        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed, "closer-a");
        await AddGroupSealedDayAsync(_group1Id);

        await _repo.UnsealByPeriodAndGroup(PeriodStart, PeriodEnd, _group2Id, WorkLockLevel.Closed);

        _context.ChangeTracker.Clear();
        var memberBreak = await _context.Break.FindAsync(_memberBreakId);
        memberBreak!.LockLevel.ShouldBe(WorkLockLevel.Closed);
        memberBreak.SealedByGroupId.ShouldBe(_group1Id);
    }

    [Test]
    public async Task DayApproval_LocksEmptyDayForMembers_UntilTheApprovingGroupRevokes()
    {
        await SeedGroupMemberWithAbsenceOnDayWithoutWorkAsync(_group1Id);
        var sealedDays = new SealedDayRepository(_context);
        var emptyDay = DayWithoutWork.AddDays(2);
        await sealedDays.AddAsync(new SealedDay
        {
            Date = emptyDay,
            GroupId = _group1Id,
            Level = WorkLockLevel.Approved,
            SealedAt = DateTime.UtcNow,
            SealedBy = "approver"
        });
        await _context.SaveChangesAsync();

        (await sealedDays.IsDayLockedAsync(emptyDay, _memberClientId)).ShouldBeTrue();
        (await sealedDays.IsDayLockedAsync(emptyDay, _client2Id)).ShouldBeFalse();

        (await sealedDays.SoftDeleteDayApprovalAsync(emptyDay, _group2Id, "other")).ShouldBe(0);
        (await sealedDays.IsDayLockedAsync(emptyDay, _memberClientId)).ShouldBeTrue();

        (await sealedDays.SoftDeleteDayApprovalAsync(emptyDay, _group1Id, "approver")).ShouldBe(1);
        _context.ChangeTracker.Clear();
        (await sealedDays.IsDayLockedAsync(emptyDay, _memberClientId)).ShouldBeFalse();
    }

    [Test]
    public async Task CloseAfterApproval_ReopenRestoresTheApprovingGroupAsOwner()
    {
        await SeedMemberOfBothGroupsAsync();
        await _repo.SealByDayAndGroup(DayWithoutWork, _group1Id, WorkLockLevel.Approved, "approver");
        await _context.Break
            .Where(b => b.Id == _memberBreakId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.LockLevel, WorkLockLevel.Approved)
                .SetProperty(b => b.SealedByGroupId, _group1Id));

        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group2Id, WorkLockLevel.Closed, "closer-b");
        await _repo.UnsealByPeriodAndGroup(PeriodStart, PeriodEnd, _group2Id, WorkLockLevel.Closed);

        _context.ChangeTracker.Clear();
        var memberBreak = await _context.Break.FindAsync(_memberBreakId);
        memberBreak!.LockLevel.ShouldBe(WorkLockLevel.Approved);
        memberBreak.SealedByGroupId.ShouldBe(_group1Id);
    }
    private async Task AddGroupSealedDayAsync(Guid groupId)
    {
        _context.SealedDay.Add(new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = DayWithoutWork,
            GroupId = groupId,
            Level = WorkLockLevel.Closed,
            SealedAt = DateTime.UtcNow,
            SealedBy = "test-user"
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedMemberOfBothGroupsAsync()
    {
        await SeedGroupMemberWithAbsenceOnDayWithoutWorkAsync(_group1Id);
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = _group2Id,
            ClientId = _memberClientId,
            IsDeleted = false
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedGroupMemberWithAbsenceOnDayWithoutWorkAsync(Guid groupId)
    {
        _memberClientId = Guid.NewGuid();
        _memberBreakId = Guid.NewGuid();

        _context.Client.Add(new Client
        {
            Id = _memberClientId,
            Name = "INTEGRATION_TEST_BreakGroupSeal_Member",
            FirstName = "Seal",
            Type = EntityTypeEnum.Employee,
            IsDeleted = false
        });
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = _memberClientId,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = _memberClientId,
            IsDeleted = false
        });
        _context.Break.Add(new Break
        {
            Id = _memberBreakId,
            ClientId = _memberClientId,
            CurrentDate = DayWithoutWork,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            WorkTime = 0m,
            LockLevel = WorkLockLevel.None,
            AbsenceId = await GetAnyAbsenceIdAsync(),
            IsDeleted = false
        });
        await _context.SaveChangesAsync();
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
            Name = "INTEGRATION_TEST_BreakGroupSeal_Shift1",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsDeleted = false
        };
        var shift2 = new Shift
        {
            Id = _shift2Id,
            Name = "INTEGRATION_TEST_BreakGroupSeal_Shift2",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsDeleted = false
        };
        _context.Shift.AddRange(shift1, shift2);

        var group1 = new Group
        {
            Id = _group1Id,
            Name = "INTEGRATION_TEST_BreakGroupSeal_Group1",
            Description = "Integration test group 1",
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            IsDeleted = false
        };
        var group2 = new Group
        {
            Id = _group2Id,
            Name = "INTEGRATION_TEST_BreakGroupSeal_Group2",
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
            Name = "INTEGRATION_TEST_BreakGroupSeal_Client1",
            FirstName = "Seal",
            IsDeleted = false
        };
        var client2 = new Client
        {
            Id = _client2Id,
            Name = "INTEGRATION_TEST_BreakGroupSeal_Client2",
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
