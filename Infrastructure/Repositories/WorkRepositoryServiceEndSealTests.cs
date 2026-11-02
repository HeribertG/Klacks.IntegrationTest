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

/// <summary>
/// Proves on Npgsql the queries of the seal-after-service rule: the day approval seals only services that
/// ended by the company-local cutoff (night shifts end on the following day), and the period close counts and lists
/// only unsealed top-level works of the real plan, restricted to the group's own shifts for a group close. The dates
/// lie in 2090 so no other data of the database falls into the ranges.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class WorkRepositoryServiceEndSealTests
{
    private const string AuditActor = "INTEGRATION_TEST_service_end_actor";
    private const string AuditAuthenticationType = "IntegrationTest";
    private const string Prefix = "INTEGRATION_TEST_ServiceEnd_";

    private static readonly DateOnly Day = new(2090, 3, 15);
    private static readonly DateOnly OtherDay = new(2090, 3, 17);
    private static readonly DateOnly PeriodStart = new(2090, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2090, 3, 31);

    private DataBaseContext _context = null!;
    private WorkRepository _repo = null!;

    private Guid _group1Id;
    private Guid _group2Id;
    private Guid _shift1Id;
    private Guid _shift2Id;
    private Guid _client1Id;
    private Guid _client2Id;
    private Guid _dayWorkId;
    private Guid _nightWorkId;
    private Guid _otherGroupWorkId;
    private Guid _deletedWorkId;
    private Guid _scenarioWorkId;
    private Guid _childWorkId;
    private Guid _confirmedWorkId;
    private Guid _deletedClientId;
    private Guid _deletedClientWorkId;

    private List<Guid> AllWorkIds => [_dayWorkId, _nightWorkId, _otherGroupWorkId, _deletedWorkId, _scenarioWorkId, _childWorkId, _confirmedWorkId, _deletedClientWorkId];

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

        await Seed();
    }

    [TearDown]
    public async Task TearDown()
    {
        var ids = AllWorkIds;
        await _context.Work.IgnoreQueryFilters().Where(e => e.ParentWorkId != null && ids.Contains(e.Id)).ExecuteDeleteAsync();
        await _context.Work.IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync();
        await _context.GroupItem.IgnoreQueryFilters().Where(e => e.GroupId == _group1Id || e.GroupId == _group2Id).ExecuteDeleteAsync();
        await _context.Group.IgnoreQueryFilters().Where(e => e.Id == _group1Id || e.Id == _group2Id).ExecuteDeleteAsync();
        await _context.Shift.IgnoreQueryFilters().Where(e => e.Id == _shift1Id || e.Id == _shift2Id).ExecuteDeleteAsync();
        await _context.Client.IgnoreQueryFilters().Where(e => e.Id == _client1Id || e.Id == _client2Id || e.Id == _deletedClientId).ExecuteDeleteAsync();
        _context.Dispose();
    }

    private static ServiceEndCutoff At(DateOnly day, int hour, int minute) => new(day, new TimeOnly(hour, minute));

    private async Task<WorkLockLevel> LevelOf(Guid id)
    {
        _context.ChangeTracker.Clear();
        return (await _context.Work.AsNoTracking().SingleAsync(w => w.Id == id)).LockLevel;
    }

    [Test]
    public async Task SealByDayAndGroup_LeavesAServiceThatHasNotEndedYet_AndSealsItFromItsEndMinute()
    {
        (await _repo.SealByDayAndGroup(Day, _group1Id, WorkLockLevel.Approved, "approver", At(Day, 15, 59))).ShouldBe(0);
        (await LevelOf(_dayWorkId)).ShouldBe(WorkLockLevel.None);

        (await _repo.SealByDayAndGroup(Day, _group1Id, WorkLockLevel.Approved, "approver", At(Day, 16, 0))).ShouldBe(1);
        (await LevelOf(_dayWorkId)).ShouldBe(WorkLockLevel.Approved);
        (await LevelOf(_nightWorkId)).ShouldBe(WorkLockLevel.None);
    }

    [Test]
    public async Task SealByDayAndGroup_SealsANightShiftOnlyOnTheFollowingDay()
    {
        var nextDay = Day.AddDays(1);

        (await _repo.SealByDayAndGroup(Day, _group1Id, WorkLockLevel.Approved, "approver", At(Day, 23, 59))).ShouldBe(1);
        (await LevelOf(_nightWorkId)).ShouldBe(WorkLockLevel.None);

        (await _repo.SealByDayAndGroup(Day, _group1Id, WorkLockLevel.Approved, "approver", At(nextDay, 5, 59))).ShouldBe(0);
        (await LevelOf(_nightWorkId)).ShouldBe(WorkLockLevel.None);

        (await _repo.SealByDayAndGroup(Day, _group1Id, WorkLockLevel.Approved, "approver", At(nextDay, 6, 0))).ShouldBe(1);
        (await LevelOf(_nightWorkId)).ShouldBe(WorkLockLevel.Approved);
    }

    [Test]
    public async Task SealByDayAndGroup_NeverTouchesAnotherGroup()
    {
        await _repo.SealByDayAndGroup(Day, _group1Id, WorkLockLevel.Approved, "approver", At(Day.AddDays(5), 0, 0));

        (await LevelOf(_otherGroupWorkId)).ShouldBe(WorkLockLevel.None);
    }

    [Test]
    public async Task HasUnendedByDayAndGroup_IsTrueWhileAServiceIsStillToCome_AndIgnoresAnotherGroup()
    {
        (await _repo.HasUnendedByDayAndGroupAsync(Day, _group1Id, WorkLockLevel.Approved, At(Day, 12, 0))).ShouldBeTrue();
        (await _repo.HasUnendedByDayAndGroupAsync(Day, _group1Id, WorkLockLevel.Approved, At(Day.AddDays(1), 6, 0))).ShouldBeFalse();
        (await _repo.HasUnendedByDayAndGroupAsync(Day, _group2Id, WorkLockLevel.Approved, At(Day, 17, 0))).ShouldBeFalse();
    }

    [Test]
    public async Task CountUnsealedForClose_GroupClose_CountsOnlyTheGroupsOwnTopLevelRealPlanWorks()
    {
        var count = await _repo.CountUnsealedForCloseAsync(PeriodStart, PeriodEnd, _group1Id);

        count.ShouldBe(2);
    }

    [Test]
    public async Task CountUnsealedForClose_WholePeriod_CountsEveryGroup_ButNoDeletedScenarioChildOrConfirmedWork()
    {
        var count = await _repo.CountUnsealedForCloseAsync(PeriodStart, PeriodEnd, null);

        count.ShouldBe(3);
    }

    [Test]
    public async Task CountUnsealedForClose_DoesNotCountTheWorkOfADeletedPerson_AndTheCloseSealsItAnyway()
    {
        (await _repo.CountUnsealedForCloseAsync(PeriodStart, PeriodEnd, _group1Id)).ShouldBe(2);
        (await _repo.ListUnsealedForCloseAsync(PeriodStart, PeriodEnd, _group1Id, 10)).Select(w => w.WorkId).ShouldNotContain(_deletedClientWorkId);

        await _repo.SealByPeriodAndGroup(PeriodStart, PeriodEnd, _group1Id, WorkLockLevel.Closed, "closer");

        (await LevelOf(_deletedClientWorkId)).ShouldBe(WorkLockLevel.Closed);
    }

    [Test]
    public async Task CountUnsealedForClose_DropsToZero_OnceEveryServiceIsConfirmed()
    {
        await _context.Work
            .Where(w => w.Id == _dayWorkId || w.Id == _nightWorkId || w.Id == _otherGroupWorkId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.LockLevel, WorkLockLevel.Confirmed));

        (await _repo.CountUnsealedForCloseAsync(PeriodStart, PeriodEnd, null)).ShouldBe(0);
    }

    [Test]
    public async Task ListUnsealedForClose_OrdersByDayAndStart_ReadsNames_AndHonoursTheLimit()
    {
        var all = await _repo.ListUnsealedForCloseAsync(PeriodStart, PeriodEnd, _group1Id, 10);

        all.Select(w => w.WorkId).ToList().ShouldBe(new List<Guid> { _dayWorkId, _nightWorkId });
        all[0].ClientName.ShouldBe("Seal " + Prefix + "Client1");
        all[0].ShiftName.ShouldBe(Prefix + "Shift1");
        all[0].Date.ShouldBe(Day);
        all[0].StartTime.ShouldBe(new TimeOnly(8, 0));
        all[0].EndTime.ShouldBe(new TimeOnly(16, 0));

        var capped = await _repo.ListUnsealedForCloseAsync(PeriodStart, PeriodEnd, _group1Id, 1);
        capped.Select(w => w.WorkId).ToList().ShouldBe(new List<Guid> { _dayWorkId });
    }

    private Work NewWork(Guid id, Guid clientId, Guid shiftId, string start, string end, Action<Work>? configure = null)
    {
        var work = new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = Day,
            StartTime = TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            EndTime = TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture),
            WorkTime = 8,
            LockLevel = WorkLockLevel.None,
            IsDeleted = false
        };
        configure?.Invoke(work);
        return work;
    }

    private async Task Seed()
    {
        _group1Id = Guid.NewGuid();
        _group2Id = Guid.NewGuid();
        _shift1Id = Guid.NewGuid();
        _shift2Id = Guid.NewGuid();
        _client1Id = Guid.NewGuid();
        _client2Id = Guid.NewGuid();
        _dayWorkId = Guid.NewGuid();
        _nightWorkId = Guid.NewGuid();
        _otherGroupWorkId = Guid.NewGuid();
        _deletedWorkId = Guid.NewGuid();
        _scenarioWorkId = Guid.NewGuid();
        _childWorkId = Guid.NewGuid();
        _confirmedWorkId = Guid.NewGuid();
        _deletedClientId = Guid.NewGuid();
        _deletedClientWorkId = Guid.NewGuid();

        _context.Shift.AddRange(
            new Shift { Id = _shift1Id, Name = Prefix + "Shift1", StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0), IsDeleted = false },
            new Shift { Id = _shift2Id, Name = Prefix + "Shift2", StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0), IsDeleted = false });
        _context.Group.AddRange(
            new Group { Id = _group1Id, Name = Prefix + "Group1", Description = "Integration test group 1", ValidFrom = DateTime.UtcNow.AddYears(-1), IsDeleted = false },
            new Group { Id = _group2Id, Name = Prefix + "Group2", Description = "Integration test group 2", ValidFrom = DateTime.UtcNow.AddYears(-1), IsDeleted = false });
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), GroupId = _group1Id, ShiftId = _shift1Id, IsDeleted = false },
            new GroupItem { Id = Guid.NewGuid(), GroupId = _group2Id, ShiftId = _shift2Id, IsDeleted = false });
        _context.Client.AddRange(
            new Client { Id = _client1Id, Name = Prefix + "Client1", FirstName = "Seal", IsDeleted = false },
            new Client { Id = _client2Id, Name = Prefix + "Client2", FirstName = "Seal", IsDeleted = false },
            new Client { Id = _deletedClientId, Name = Prefix + "DeletedClient", FirstName = "Seal", IsDeleted = true });
        await _context.SaveChangesAsync();

        _context.Work.AddRange(
            NewWork(_dayWorkId, _client1Id, _shift1Id, "08:00", "16:00"),
            NewWork(_nightWorkId, _client1Id, _shift1Id, "22:00", "06:00"),
            NewWork(_otherGroupWorkId, _client2Id, _shift2Id, "09:00", "17:00"),
            NewWork(_deletedWorkId, _client1Id, _shift1Id, "10:00", "18:00", w => w.IsDeleted = true),
            NewWork(_scenarioWorkId, _client1Id, _shift1Id, "11:00", "19:00", w => w.AnalyseToken = Guid.NewGuid()),
            NewWork(_confirmedWorkId, _client1Id, _shift1Id, "12:00", "20:00", w => { w.LockLevel = WorkLockLevel.Confirmed; w.CurrentDate = OtherDay; }));
        await _context.SaveChangesAsync();

        _context.Work.Add(NewWork(_deletedClientWorkId, _deletedClientId, _shift1Id, "09:00", "10:00", w => w.CurrentDate = OtherDay));
        _context.Work.Add(NewWork(_childWorkId, _client1Id, _shift1Id, "08:00", "12:00", w => { w.ParentWorkId = _dayWorkId; w.CurrentDate = OtherDay; }));
        await _context.SaveChangesAsync();
    }
}
