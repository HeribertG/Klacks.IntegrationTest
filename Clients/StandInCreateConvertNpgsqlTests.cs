// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Creating and converting stand-ins against PostgreSQL (port 5434) with the real ClientRepository and UnitOfWork. The
/// name frame query treats LIKE metacharacters in the pattern literally. A bulk creation stores every stand-in with its
/// sequence IdNumber, membership, group membership and qualification, continues after the highest index in the frame, and
/// stores none when one row fails. A conversion keeps id, IdNumber, works, membership, group membership and
/// qualification, and afterwards neither the period close lock nor the payroll gate see the person as a stand-in any
/// more. The dates lie in 2092 so no other data falls into the period. Every row carries the prefix
/// INTEGRATION_TEST_StandIn_ and only the rows created here are removed.
/// </summary>

using Klacks.Api.Application.Commands.Clients;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.Clients;

[TestFixture]
[Category("RealDatabase")]
public class StandInCreateConvertNpgsqlTests
{
    private const string TestPrefix = "INTEGRATION_TEST_StandIn_";
    private const int ClientNameColumnLength = 100;

    private static readonly DateOnly Day1 = new(2092, 3, 2);
    private static readonly DateOnly Day2 = Day1.AddDays(1);
    private static readonly DateOnly Day3 = Day1.AddDays(2);
    private static readonly DateTime MembershipStart = new(2092, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly Guid _qualificationId = Guid.NewGuid();
    private readonly List<Guid> _sealIds = [];

    private DataBaseContext _context = null!;
    private ClientRepository _clientRepository = null!;
    private UnitOfWork _unitOfWork = null!;

    private string RunPrefix => TestPrefix + _run + "_";

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
        _clientRepository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            new EntityCollectionUpdateService(_context),
            Substitute.For<IClientValidator>(),
            Substitute.For<ILogger<ClientRepository>>());
        _unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());

        _context.Group.Add(new Group { Id = _groupId, Name = RunPrefix + "Group", Description = TestPrefix, ValidFrom = MembershipStart });
        _context.Shift.Add(new Shift { Id = _shiftId, Name = RunPrefix + "Shift", StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0) });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _shiftId });
        _context.Qualification.Add(new Qualification { Id = _qualificationId, Name = new MultiLanguage { De = RunPrefix + "Q", En = RunPrefix + "Q" } });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _context.ChangeTracker.Clear();
        var clientIds = await _context.Client.IgnoreQueryFilters()
            .Where(c => c.Name.StartsWith(RunPrefix) || c.FirstName == RunPrefix)
            .Select(c => c.Id)
            .ToListAsync();
        await _context.Work.IgnoreQueryFilters().Where(w => clientIds.Contains(w.ClientId)).ExecuteDeleteAsync();
        await _context.SealedDay.IgnoreQueryFilters().Where(s => _sealIds.Contains(s.Id)).ExecuteDeleteAsync();
        await _context.ClientQualification.IgnoreQueryFilters().Where(q => clientIds.Contains(q.ClientId)).ExecuteDeleteAsync();
        await _context.GroupItem.IgnoreQueryFilters()
            .Where(g => g.GroupId == _groupId || (g.ClientId != null && clientIds.Contains(g.ClientId.Value)))
            .ExecuteDeleteAsync();
        await _context.Membership.IgnoreQueryFilters().Where(m => clientIds.Contains(m.ClientId)).ExecuteDeleteAsync();
        await _context.Address.IgnoreQueryFilters().Where(a => clientIds.Contains(a.ClientId)).ExecuteDeleteAsync();
        await _context.Communication.IgnoreQueryFilters().Where(c => clientIds.Contains(c.ClientId)).ExecuteDeleteAsync();
        await _context.Client.IgnoreQueryFilters().Where(c => clientIds.Contains(c.Id)).ExecuteDeleteAsync();
        await _context.Qualification.IgnoreQueryFilters().Where(q => q.Id == _qualificationId).ExecuteDeleteAsync();
        await _context.Shift.IgnoreQueryFilters().Where(s => s.Id == _shiftId).ExecuteDeleteAsync();
        await _context.Group.IgnoreQueryFilters().Where(g => g.Id == _groupId).ExecuteDeleteAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ListNamesInFrame_TreatsLikeMetacharactersLiterally_AndSkipsDeletedClients()
    {
        var prefix = RunPrefix + "%a_Fair ";
        _context.Client.AddRange(
            StandIn(prefix + "1"),
            StandIn(RunPrefix + "Xa_Fair 2"),
            StandIn(RunPrefix + "%aXFair 3"),
            StandIn(prefix + "4 night"),
            StandIn(prefix + "5", deleted: true));
        await _context.SaveChangesAsync();

        var names = await _clientRepository.ListNamesInFrameAsync(prefix, string.Empty);

        names.ShouldBe([prefix + "1", prefix + "4 night"], ignoreOrder: true);
        (await _clientRepository.ListNamesInFrameAsync(prefix, " night")).ShouldBe([prefix + "4 night"]);
    }

    [Test]
    public async Task Create_StoresEveryStandIn_WithSequenceNumber_Membership_GroupMembership_Qualification_AfterTheHighestIndex()
    {
        _context.Client.Add(StandIn(RunPrefix + "Fair 4"));
        await _context.SaveChangesAsync();

        var created = await CreateHandler().Handle(CreateCommand(RunPrefix + "Fair {n}", 3), CancellationToken.None);

        created.Select(c => c.Name).ShouldBe([RunPrefix + "Fair 5", RunPrefix + "Fair 6", RunPrefix + "Fair 7"]);
        created.Select(c => c.IdNumber).Distinct().Count().ShouldBe(3);
        created.ShouldAllBe(c => c.IdNumber > 0);

        _context.ChangeTracker.Clear();
        var ids = created.Select(c => c.Id).ToList();
        var stored = await _context.Client.AsNoTracking()
            .Include(c => c.Membership)
            .Include(c => c.GroupItems)
            .Include(c => c.Qualifications)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync();
        stored.Count.ShouldBe(3);
        foreach (var standIn in stored)
        {
            standIn.Type.ShouldBe(EntityTypeEnum.StandIn);
            standIn.Membership!.ValidFrom.ShouldBe(MembershipStart);
            standIn.Membership.ValidUntil.ShouldBeNull();
            standIn.GroupItems.ShouldHaveSingleItem().GroupId.ShouldBe(_groupId);
            var qualification = standIn.Qualifications.ShouldHaveSingleItem();
            qualification.QualificationId.ShouldBe(_qualificationId);
            qualification.Level.ShouldBe(QualificationLevel.Expert);
        }
    }

    [Test]
    public async Task Create_AllOrNothing_WhenOneRowFails_NoStandInIsStored()
    {
        var filler = new string('x', ClientNameColumnLength - 1 - RunPrefix.Length);
        var pattern = RunPrefix + filler + "{n}";

        await Should.ThrowAsync<InvalidRequestException>(
            () => CreateHandler().Handle(CreateCommand(pattern, 10), CancellationToken.None));

        _context.ChangeTracker.Clear();
        (await _context.Client.IgnoreQueryFilters().CountAsync(c => c.Name.StartsWith(RunPrefix + filler))).ShouldBe(0);
    }

    [Test]
    public async Task Convert_KeepsWorksAndAssignments_AndTheCloseLockAndThePayrollGateNoLongerSeeAStandIn()
    {
        var standIn = StandIn(RunPrefix + "Fair 1");
        standIn.Membership = new Membership { ValidFrom = MembershipStart };
        standIn.GroupItems.Add(new GroupItem { GroupId = _groupId });
        standIn.Qualifications.Add(new ClientQualification { QualificationId = _qualificationId, Level = QualificationLevel.Expert });
        _context.Client.Add(standIn);
        await _context.SaveChangesAsync();
        var workIds = new[] { AddClosedWork(standIn.Id, Day1), AddClosedWork(standIn.Id, Day3) };
        foreach (var day in new[] { Day1, Day2, Day3 })
        {
            AddGroupSeal(day);
        }

        await _context.SaveChangesAsync();
        var idNumber = standIn.IdNumber;
        var phoneticBefore = standIn.PhoneticTokens;
        var workRepository = WorkRepository();
        var gate = new PayrollCompletenessGate(_context, new ExportLogItemRepository(_context));
        var lockedBefore = await workRepository.CountStandInWorksForCloseAsync(Day1, Day3, _groupId);
        (await gate.CheckAsync(Day1, Day3, [standIn.Id])).Blockers
            .ShouldContain(b => b.Reason == PayrollExportBlockReason.StandInAssigned);
        _context.ChangeTracker.Clear();

        var result = await ConvertHandler().Handle(
            new ConvertStandInCommand(standIn.Id, new ConvertStandInResource
            {
                TargetType = (int)EntityTypeEnum.Employee,
                FirstName = RunPrefix,
                Name = RunPrefix + "Muster",
                Gender = GenderEnum.Female,
                Address = new AddressResource { Type = AddressTypeEnum.Employee, City = "Bern", Zip = "3000", ValidFrom = MembershipStart },
            }),
            CancellationToken.None);

        result.Id.ShouldBe(standIn.Id);
        _context.ChangeTracker.Clear();
        var stored = await _context.Client.AsNoTracking()
            .Include(c => c.Membership)
            .Include(c => c.GroupItems)
            .Include(c => c.Qualifications)
            .Include(c => c.Addresses)
            .SingleAsync(c => c.Id == standIn.Id);
        stored.Type.ShouldBe(EntityTypeEnum.Employee);
        stored.FirstName.ShouldBe(RunPrefix);
        stored.Name.ShouldBe(RunPrefix + "Muster");
        stored.IdNumber.ShouldBe(idNumber);
        stored.PhoneticTokens.ShouldNotBe(phoneticBefore);
        stored.Membership!.ValidFrom.ShouldBe(MembershipStart);
        stored.GroupItems.ShouldHaveSingleItem().GroupId.ShouldBe(_groupId);
        stored.Qualifications.ShouldHaveSingleItem().QualificationId.ShouldBe(_qualificationId);
        stored.Addresses.ShouldHaveSingleItem().City.ShouldBe("Bern");
        (await _context.Work.AsNoTracking().Where(w => w.ClientId == standIn.Id).Select(w => w.Id).ToListAsync())
            .ShouldBe(workIds, ignoreOrder: true);

        lockedBefore.ShouldBe(2);
        (await workRepository.CountStandInWorksForCloseAsync(Day1, Day3, _groupId)).ShouldBe(0);
        var after = await gate.CheckAsync(Day1, Day3, [standIn.Id]);
        after.Blockers.ShouldBeEmpty();
        after.IsComplete.ShouldBeTrue();
    }

    [Test]
    public async Task Convert_AnEmployee_IsRefused_AndChangesNothing()
    {
        var employee = StandIn(RunPrefix + "Employee");
        employee.Type = EntityTypeEnum.Employee;
        employee.FirstName = "Anna";
        _context.Client.Add(employee);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await Should.ThrowAsync<InvalidRequestException>(() => ConvertHandler().Handle(
            new ConvertStandInCommand(employee.Id, new ConvertStandInResource
            {
                TargetType = (int)EntityTypeEnum.ExternEmp,
                FirstName = "Other",
                Name = RunPrefix + "Other",
                Gender = GenderEnum.Male,
            }),
            CancellationToken.None));

        _context.ChangeTracker.Clear();
        var stored = await _context.Client.AsNoTracking().SingleAsync(c => c.Id == employee.Id);
        stored.Type.ShouldBe(EntityTypeEnum.Employee);
        stored.Name.ShouldBe(RunPrefix + "Employee");
    }

    private static Client StandIn(string name, bool deleted = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        FirstName = string.Empty,
        Type = EntityTypeEnum.StandIn,
        IsDeleted = deleted,
    };

    private CreateStandInsCommand CreateCommand(string pattern, int count) => new(new CreateStandInsResource
    {
        Count = count,
        NamePattern = pattern,
        GroupId = _groupId,
        ValidFrom = DateOnly.FromDateTime(MembershipStart),
        QualificationIds = [_qualificationId],
    });

    private CreateStandInsCommandHandler CreateHandler()
    {
        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.Exists(_groupId).Returns(true);
        var qualificationRepository = Substitute.For<IQualificationRepository>();
        qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>()).Returns([new Qualification { Id = _qualificationId }]);
        var groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        groupVisibilityGuard.IsGroupVisibleAsync(_groupId, Arg.Any<CancellationToken>()).Returns(true);

        return new CreateStandInsCommandHandler(
            _clientRepository,
            groupRepository,
            qualificationRepository,
            groupVisibilityGuard,
            _unitOfWork,
            Substitute.For<ILogger<CreateStandInsCommandHandler>>());
    }

    private ConvertStandInCommandHandler ConvertHandler()
    {
        var clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        return new ConvertStandInCommandHandler(
            _clientRepository,
            clientVisibilityGuard,
            new ClientMapper(),
            _unitOfWork,
            Substitute.For<IEmailClientAssignmentService>(),
            Substitute.For<ISurchargeRecalculationScope>(),
            Substitute.For<IThoroughRecalculationQueue>(),
            Substitute.For<ILogger<ConvertStandInCommandHandler>>());
    }

    private WorkRepository WorkRepository()
    {
        var baseQueryService = new ClientBaseQueryService(
            _context,
            Substitute.For<IClientGroupFilterService>(),
            Substitute.For<IClientSearchFilterService>(),
            new ClientSearchService(),
            Substitute.For<IClientFuzzySearchService>());
        return new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            baseQueryService,
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IClientContractDataProvider>());
    }

    private Guid AddClosedWork(Guid clientId, DateOnly day)
    {
        var id = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = _shiftId,
            CurrentDate = day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8,
            LockLevel = WorkLockLevel.Closed,
        });
        return id;
    }

    private void AddGroupSeal(DateOnly day)
    {
        var id = Guid.NewGuid();
        _context.SealedDay.Add(new SealedDay
        {
            Id = id,
            Date = day,
            GroupId = _groupId,
            Level = WorkLockLevel.Closed,
            Reason = TestPrefix + "seal",
            SealedAt = DateTime.UtcNow,
            SealedBy = TestPrefix + "actor",
        });
        _sealIds.Add(id);
    }
}
