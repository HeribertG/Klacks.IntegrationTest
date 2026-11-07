// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The period close lock for stand-ins against PostgreSQL (port 5434): the real WorkRepository counts only top-level
/// real-plan works on stand-ins (any lock level, not deleted, not a scenario, not a container child; a soft-deleted
/// stand-in's works count, because the close would seal them), the group close only those on the group's own shifts; the real PeriodSealCompletenessGuard refuses the
/// whole-period close and the group close with StandInWorksPresentException although every service is confirmed; and
/// the real GetPeriodIssuesQueryHandler reports the same count as one Error entry, which the automatic close and the close
/// advice count. The dates lie in 2091 so no other data falls into the period. Every row carries the prefix
/// INTEGRATION_TEST_StandIn_ and only the rows created here are removed.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Services.PeriodClosing;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.PeriodClosing;

[TestFixture]
[Category("RealDatabase")]
public class StandInCloseLockNpgsqlTests
{
    private const string TestPrefix = "INTEGRATION_TEST_StandIn_";

    private static readonly DateOnly PeriodStart = new(2091, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2091, 5, 31);
    private static readonly DateOnly Day = new(2091, 5, 12);

    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _otherGroupId = Guid.NewGuid();
    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly Guid _otherShiftId = Guid.NewGuid();
    private readonly Guid _employeeId = Guid.NewGuid();
    private readonly Guid _standInId = Guid.NewGuid();
    private readonly Guid _deletedStandInId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private WorkRepository _workRepository = null!;
    private PeriodSealCompletenessGuard _sealGuard = null!;

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

        var baseQueryService = new ClientBaseQueryService(
            _context,
            Substitute.For<IClientGroupFilterService>(),
            Substitute.For<IClientSearchFilterService>(),
            new ClientSearchService(),
            Substitute.For<IClientFuzzySearchService>());
        _workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            baseQueryService,
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IClientContractDataProvider>());
        _sealGuard = new PeriodSealCompletenessGuard(_workRepository, Substitute.For<IScheduleRowAccessGuard>());

        await CleanupAsync();
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        _context.Dispose();
    }

    [Test]
    public async Task CountStandInWorks_GroupClose_CountsOnlyTheGroupsOwnTopLevelRealPlanStandInWorks_AlsoOfADeletedStandIn()
    {
        (await _workRepository.CountStandInWorksForCloseAsync(PeriodStart, PeriodEnd, _groupId)).ShouldBe(3);
    }

    [Test]
    public async Task CountStandInWorks_WholePeriod_CountsEveryGroup_ButNoEmployeeDeletedScenarioOrChildWork()
    {
        (await _workRepository.CountStandInWorksForCloseAsync(PeriodStart, PeriodEnd, null)).ShouldBe(4);
    }

    [Test]
    public async Task Close_And_ByGroup_Refused_WhileStandInWork_AlthoughEveryServiceIsConfirmed()
    {
        await _context.Work
            .Where(w => w.ClientId == _standInId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.LockLevel, WorkLockLevel.Confirmed));
        (await _workRepository.CountUnsealedForCloseAsync(PeriodStart, PeriodEnd, null)).ShouldBe(0);

        var whole = await Should.ThrowAsync<StandInWorksPresentException>(
            () => _sealGuard.EnsureAllSealedAsync(PeriodStart, PeriodEnd, null));
        var byGroup = await Should.ThrowAsync<StandInWorksPresentException>(
            () => _sealGuard.EnsureAllSealedAsync(PeriodStart, PeriodEnd, _groupId));

        whole.StandInWorkCount.ShouldBe(4);
        byGroup.StandInWorkCount.ShouldBe(3);
    }

    [Test]
    public async Task Close_IsNoLongerRefused_OnceTheStandInWorksLieOnAPerson()
    {
        await _context.Work
            .Where(w => w.ClientId == _standInId || w.ClientId == _deletedStandInId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.ClientId, _employeeId)
                .SetProperty(w => w.LockLevel, WorkLockLevel.Confirmed));

        await _sealGuard.EnsureAllSealedAsync(PeriodStart, PeriodEnd, null);
        await _sealGuard.EnsureAllSealedAsync(PeriodStart, PeriodEnd, _groupId);
    }

    [Test]
    public async Task PeriodIssues_ReportTheGuardCount_AsOneError_ForTheGroup()
    {
        var issues = await IssuesHandler().Handle(new GetPeriodIssuesQuery(PeriodStart, PeriodEnd, _groupId), CancellationToken.None);

        var entry = issues.ShouldHaveSingleItem();
        entry.Code.ShouldBe(PeriodIssueLimits.StandInWorksCode);
        entry.MessageParams["count"].ShouldBe(3.ToString(CultureInfo.InvariantCulture));
        PeriodCloseGates.CountErrors(issues).ShouldBe(1);
    }

    private GetPeriodIssuesQueryHandler IssuesHandler()
    {
        var readRepository = Substitute.For<IPeriodClosingReadRepository>();
        readRepository.GetScheduleNoteIssues(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodScheduleNoteIssueRow>());
        var validationLoader = Substitute.For<IPeriodValidationLoader>();
        validationLoader.LoadAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<PeriodIssueDto>());
        var groupGuard = Substitute.For<IGroupVisibilityGuard>();
        groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);

        return new GetPeriodIssuesQueryHandler(
            readRepository, validationLoader, Substitute.For<IComplianceEscalationService>(), groupGuard,
            Substitute.For<IClientVisibilityGuard>(), _sealGuard, NullLogger<GetPeriodIssuesQueryHandler>.Instance);
    }

    private static Work NewWork(Guid clientId, Guid shiftId, string start, Action<Work>? configure = null)
    {
        var startTime = TimeOnly.Parse(start, CultureInfo.InvariantCulture);
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = Day,
            StartTime = startTime,
            EndTime = startTime.AddHours(1),
            WorkTime = 1,
            LockLevel = WorkLockLevel.Confirmed,
        };
        configure?.Invoke(work);
        return work;
    }

    private async Task SeedAsync()
    {
        _context.Shift.AddRange(
            new Shift { Id = _shiftId, Name = TestPrefix + "Shift", StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0) },
            new Shift { Id = _otherShiftId, Name = TestPrefix + "OtherShift", StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0) });
        _context.Group.AddRange(
            new Group { Id = _groupId, Name = TestPrefix + "Group", Description = TestPrefix, ValidFrom = DateTime.UtcNow.AddYears(-1) },
            new Group { Id = _otherGroupId, Name = TestPrefix + "OtherGroup", Description = TestPrefix, ValidFrom = DateTime.UtcNow.AddYears(-1) });
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _shiftId },
            new GroupItem { Id = Guid.NewGuid(), GroupId = _otherGroupId, ShiftId = _otherShiftId });
        _context.Client.AddRange(
            new Client { Id = _employeeId, Name = TestPrefix + "Employee", FirstName = "Anna", Type = EntityTypeEnum.Employee },
            new Client { Id = _standInId, Name = TestPrefix + "Fair 1", FirstName = string.Empty, Type = EntityTypeEnum.StandIn },
            new Client { Id = _deletedStandInId, Name = TestPrefix + "Fair 2", FirstName = string.Empty, Type = EntityTypeEnum.StandIn, IsDeleted = true });
        await _context.SaveChangesAsync();

        var parent = NewWork(_standInId, _shiftId, "08:00");
        _context.Work.AddRange(
            parent,
            NewWork(_standInId, _shiftId, "10:00", w => w.LockLevel = WorkLockLevel.None),
            NewWork(_standInId, _otherShiftId, "08:00"),
            NewWork(_standInId, _shiftId, "12:00", w => w.IsDeleted = true),
            NewWork(_standInId, _shiftId, "13:00", w => w.AnalyseToken = Guid.NewGuid()),
            NewWork(_standInId, _shiftId, "14:00", w => w.CurrentDate = PeriodEnd.AddDays(1)),
            NewWork(_deletedStandInId, _shiftId, "15:00"),
            NewWork(_employeeId, _shiftId, "08:00"));
        await _context.SaveChangesAsync();

        _context.Work.Add(NewWork(_standInId, _shiftId, "08:30", w => w.ParentWorkId = parent.Id));
        await _context.SaveChangesAsync();
    }

    private async Task CleanupAsync()
    {
        var clientIds = new[] { _employeeId, _standInId, _deletedStandInId };
        await _context.Work.IgnoreQueryFilters()
            .Where(w => w.ParentWorkId != null && clientIds.Contains(w.ClientId))
            .ExecuteDeleteAsync();
        await _context.Work.IgnoreQueryFilters()
            .Where(w => clientIds.Contains(w.ClientId))
            .ExecuteDeleteAsync();
        await _context.GroupItem.IgnoreQueryFilters().Where(gi => gi.GroupId == _groupId || gi.GroupId == _otherGroupId).ExecuteDeleteAsync();
        await _context.Group.IgnoreQueryFilters().Where(g => g.Id == _groupId || g.Id == _otherGroupId).ExecuteDeleteAsync();
        await _context.Shift.IgnoreQueryFilters().Where(s => s.Id == _shiftId || s.Id == _otherShiftId).ExecuteDeleteAsync();
        await _context.Client.IgnoreQueryFilters().Where(c => clientIds.Contains(c.Id)).ExecuteDeleteAsync();
    }
}
