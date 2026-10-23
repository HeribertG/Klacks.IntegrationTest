using System.Security.Claims;
using Shouldly;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
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
public class WorkRepositoryGroupSealTests
{
    private const string AuditActor = "INTEGRATION_TEST_seal_audit_actor";
    private const string AuditAuthenticationType = "IntegrationTest";

    private DataBaseContext _context = null!;
    private WorkRepository _repo = null!;

    private Guid _group1Id;
    private Guid _group2Id;
    private Guid _shift1Id;
    private Guid _shift2Id;
    private Guid _client1Id;
    private Guid _client2Id;
    private Guid _work1Id;
    private Guid _work2Id;

    private static readonly DateOnly TestDate = new DateOnly(2026, 6, 15);
    private static readonly DateOnly PeriodStart = new DateOnly(2026, 6, 1);
    private static readonly DateOnly PeriodEnd = new DateOnly(2026, 6, 30);

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, AuditActor)], AuditAuthenticationType))
        });
        _context = new DataBaseContext(options, httpContextAccessor);

        var fuzzySearchService = Substitute.For<IClientFuzzySearchService>();
        fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<Client>()));

        var baseQueryService = new ClientBaseQueryService(
            _context,
            Substitute.For<IClientGroupFilterService>(),
            Substitute.For<IClientSearchFilterService>(),
            new Klacks.Api.Infrastructure.Services.Clients.ClientSearchService(),
            fuzzySearchService);

        _repo = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            baseQueryService,
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IClientContractDataProvider>());

        await SeedTwoGroupsWithOneClientEach();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.Work.Where(e => e.Id == _work1Id || e.Id == _work2Id).ExecuteDeleteAsync();
        await _context.GroupItem.Where(e => e.GroupId == _group1Id || e.GroupId == _group2Id).ExecuteDeleteAsync();
        await _context.Group.Where(e => e.Id == _group1Id || e.Id == _group2Id).ExecuteDeleteAsync();
        await _context.Shift.Where(e => e.Id == _shift1Id || e.Id == _shift2Id).ExecuteDeleteAsync();
        await _context.Client.Where(e => e.Id == _client1Id || e.Id == _client2Id).ExecuteDeleteAsync();
        _context.Dispose();
    }

    [Test]
    public async Task SealByPeriodAndGroup_OnlySealsWorksOfClientsInGroup()
    {
        var affected = await _repo.SealByPeriodAndGroup(
            PeriodStart, PeriodEnd,
            _group1Id,
            WorkLockLevel.Confirmed,
            "test-user");

        affected.ShouldBe(1);

        _context.ChangeTracker.Clear();
        var work1 = await _context.Work.FindAsync(_work1Id);
        var work2 = await _context.Work.FindAsync(_work2Id);

        work1!.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
        work2!.LockLevel.ShouldBe(WorkLockLevel.None);
    }

    [Test]
    public async Task UnsealByPeriodAndGroup_OnlyUnsealsWorksOfClientsInGroup()
    {
        await _context.Work
            .Where(w => w.Id == _work1Id || w.Id == _work2Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.LockLevel, WorkLockLevel.Confirmed)
                .SetProperty(w => w.SealedAt, DateTime.UtcNow)
                .SetProperty(w => w.SealedBy, "pre-sealer"));

        var affected = await _repo.UnsealByPeriodAndGroup(
            PeriodStart, PeriodEnd,
            _group1Id,
            WorkLockLevel.Confirmed);

        affected.Total.ShouldBe(1);

        _context.ChangeTracker.Clear();
        var work1 = await _context.Work.FindAsync(_work1Id);
        var work2 = await _context.Work.FindAsync(_work2Id);

        work1!.LockLevel.ShouldBe(WorkLockLevel.None);
        work2!.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
    }

    [Test]
    public async Task SealByDayAndGroup_StampsUpdateTimeAndActor()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _repo.SealByDayAndGroup(TestDate, _group1Id, WorkLockLevel.Approved, "test-user");

        await ShouldBeStampedAsync(_work1Id, before);
    }

    [Test]
    public async Task UnsealByDayAndGroup_StampsUpdateTimeAndActor()
    {
        await PreSealWithoutAuditAsync(WorkLockLevel.Approved);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _repo.UnsealByDayAndGroup(TestDate, _group1Id, WorkLockLevel.Approved);

        await ShouldBeStampedAsync(_work1Id, before);
    }

    [Test]
    public async Task SealByPeriodAndGroup_StampsUpdateTimeAndActor()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed, "test-user");

        await ShouldBeStampedAsync(_work1Id, before);
    }

    [Test]
    public async Task UnsealByPeriodAndGroup_StampsUpdateTimeAndActor()
    {
        await PreSealWithoutAuditAsync(WorkLockLevel.Closed);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _repo.UnsealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed);

        await ShouldBeStampedAsync(_work1Id, before);
    }

    /// <summary>
    /// Locks the first work the way a seal before this fix did: lock columns only, no UpdateTime, so the unseal is the
    /// only statement that can stamp it.
    /// </summary>
    private async Task PreSealWithoutAuditAsync(WorkLockLevel level)
    {
        await _context.Work
            .Where(x => x.Id == _work1Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LockLevel, level)
                .SetProperty(x => x.SealedAt, DateTime.UtcNow)
                .SetProperty(x => x.SealedBy, "pre-sealer")
                .SetProperty(x => x.UpdateTime, (DateTime?)null));
    }

    /// <summary>
    /// A lock change is a change of the entry: the scenario staleness check and every audit reader rely on UpdateTime,
    /// which a bulk ExecuteUpdate bypassing OnBeforeSaving would otherwise leave untouched.
    /// </summary>
    private async Task ShouldBeStampedAsync(Guid id, DateTime notBefore)
    {
        _context.ChangeTracker.Clear();
        var entry = await _context.Work.AsNoTracking().SingleAsync(x => x.Id == id);
        entry.UpdateTime.ShouldNotBeNull("a lock change must stamp UpdateTime");
        entry.UpdateTime!.Value.ShouldBeGreaterThanOrEqualTo(notBefore);
        entry.CurrentUserUpdated.ShouldBe(AuditActor);
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

        var shift1 = new Shift
        {
            Id = _shift1Id,
            Name = "INTEGRATION_TEST_GroupSeal_Shift1",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsDeleted = false
        };
        var shift2 = new Shift
        {
            Id = _shift2Id,
            Name = "INTEGRATION_TEST_GroupSeal_Shift2",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsDeleted = false
        };
        _context.Shift.AddRange(shift1, shift2);

        var group1 = new Group
        {
            Id = _group1Id,
            Name = "INTEGRATION_TEST_GroupSeal_Group1",
            Description = "Integration test group 1",
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            IsDeleted = false
        };
        var group2 = new Group
        {
            Id = _group2Id,
            Name = "INTEGRATION_TEST_GroupSeal_Group2",
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
            Name = "INTEGRATION_TEST_GroupSeal_Client1",
            FirstName = "Seal",
            IsDeleted = false
        };
        var client2 = new Client
        {
            Id = _client2Id,
            Name = "INTEGRATION_TEST_GroupSeal_Client2",
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
    }
}
