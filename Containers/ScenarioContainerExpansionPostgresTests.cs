// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks that a container placed in a scenario gets its child works, the way the wizard apply places it
/// (BulkAddWorks stages the container work on the cloned container shift with the scenario token, then
/// ContainerWorkExpansionService.ExpandAsync expands it), and that an accept leaves the container with its children in
/// the real plan. Container templates carry no token and are not cloned, so the scenario reads the template of the real
/// container. The real plan is the control case. Only INTEGRATION_TEST_ rows are written and removed; the day lies in 2099.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using Work = Klacks.Api.Domain.Models.Schedules.Work;

namespace Klacks.IntegrationTest.Containers;

[TestFixture]
[Category("RealDatabase")]
public class ScenarioContainerExpansionPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_SCNEXP_";
    private static readonly DateOnly Day = new(2099, 11, 9);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _scenarioService = null!;
    private WorkRepository _workRepository = null!;
    private ContainerWorkExpansionService _expansion = null!;
    private Client _person = null!;
    private Shift _container = null!;
    private Shift _taskA = null!;
    private Shift _taskB = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        await CleanupAsync(_context);
        _scenarioService = AnalyseScenarioTestServiceFactory.Create(_context);
        _workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IClientContractDataProvider>());
        _expansion = BuildExpansion();
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task RealPlan_ContainerWork_GetsAChildWorkPerTemplateTask()
    {
        var containerWork = await PlaceContainerAsync(_container.Id, token: null);

        var children = await ChildrenOfAsync(containerWork.Id);

        children.Select(child => child.ShiftId).ShouldBe([_taskA.Id, _taskB.Id], ignoreOrder: true);
        children.ShouldAllBe(child => child.AnalyseToken == null && child.ClientId == _person.Id);
    }

    [Test]
    public async Task Scenario_ContainerPlacedLikeTheWizardApply_GetsItsChildWorksOnTheClonesOfThisScenario()
    {
        var token = Guid.NewGuid();
        var cloneOf = await CloneAsync(token);

        var containerWork = await PlaceContainerAsync(cloneOf[_container.Id], token);

        var children = await ChildrenOfAsync(containerWork.Id);
        children.Select(child => child.ShiftId).ShouldBe([cloneOf[_taskA.Id], cloneOf[_taskB.Id]], ignoreOrder: true);
        children.ShouldAllBe(child => child.AnalyseToken == token && child.ClientId == _person.Id);
    }

    [Test]
    public async Task Scenario_Accepted_LeavesTheContainerWithItsChildWorksInTheRealPlan()
    {
        var token = Guid.NewGuid();
        var cloneOf = await CloneAsync(token);
        var containerWork = await PlaceContainerAsync(cloneOf[_container.Id], token);

        await _scenarioService.PromoteScenarioWorksAsync(token, Day, Day, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var promoted = await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(work => work.Id == containerWork.Id);
        promoted.AnalyseToken.ShouldBeNull();
        promoted.ShiftId.ShouldBe(_container.Id);
        var children = await ChildrenOfAsync(containerWork.Id);
        children.Select(child => child.ShiftId).ShouldBe([_taskA.Id, _taskB.Id], ignoreOrder: true);
        children.ShouldAllBe(child => child.AnalyseToken == null);
    }

    [Test]
    public async Task Scenario_ShiftTable_ShowsTheContainerStaffedAndItsTasksAsContainerChildren()
    {
        var token = Guid.NewGuid();
        var cloneOf = await CloneAsync(token);
        await PlaceContainerAsync(cloneOf[_container.Id], token);
        var scheduleService = new ShiftScheduleService(
            _context,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            Substitute.For<ILogger<ShiftScheduleService>>());

        var rows = await scheduleService.GetShiftScheduleListAsync(Day, Day, analyseToken: token);

        var containerRow = rows.Single(row => row.ShiftId == cloneOf[_container.Id]);
        containerRow.Engaged.ShouldBe(1);
        containerRow.IsInTemplateContainer.ShouldBeFalse();
        rows.Single(row => row.ShiftId == cloneOf[_taskA.Id]).IsInTemplateContainer.ShouldBeTrue();
        rows.Single(row => row.ShiftId == cloneOf[_taskB.Id]).IsInTemplateContainer.ShouldBeTrue();
    }

    private ContainerWorkExpansionService BuildExpansion()
    {
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        var templateRepository = new ContainerTemplateRepository(
            _context,
            Substitute.For<ILogger<ContainerTemplate>>(),
            new EntityCollectionUpdateService(_context),
            new ContainerTemplateService(unitOfWork, Substitute.For<ILogger<ContainerTemplateService>>()));
        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Get(Arg.Any<Guid>()).Returns(call => Task.FromResult(ReadShift(call.Arg<Guid>())));
        var writeGuard = new WorkWriteGuardService(
            shiftRepository,
            _workRepository,
            Substitute.For<IPreCommitConflictChecker>(),
            new ShiftVisibilityReadRepository(_context));
        return new ContainerWorkExpansionService(
            templateRepository,
            new ContainerShiftOverrideRepository(_context),
            _workRepository,
            new BreakRepository(_context, Substitute.For<ILogger<Break>>()),
            new WorkChangeRepository(_context, Substitute.For<ILogger<WorkChange>>(), Substitute.For<IWorkMacroService>()),
            shiftRepository,
            writeGuard,
            Substitute.For<IContainerWorkChildrenManager>(),
            new ShiftReader(_context),
            Substitute.For<ILogger<ContainerWorkExpansionService>>());
    }

    private Shift? ReadShift(Guid id)
    {
        using var context = NewContext();
        return context.Shift.IgnoreQueryFilters().AsNoTracking().SingleOrDefault(shift => shift.Id == id);
    }

    private async Task<Dictionary<Guid, Guid>> CloneAsync(Guid token)
    {
        var cloneOf = await _scenarioService.CloneScenarioDataAsync(
            null, Day, Day, token, [_container.Id, _taskA.Id, _taskB.Id], null, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return cloneOf;
    }

    private async Task<Work> PlaceContainerAsync(Guid containerShiftId, Guid? token)
    {
        var containerWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _person.Id,
            ShiftId = containerShiftId,
            CurrentDate = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token
        };
        await _workRepository.Add(containerWork);
        await _expansion.ExpandAsync(containerWork, Day);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return containerWork;
    }

    private Task<List<Work>> ChildrenOfAsync(Guid containerWorkId)
        => _context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(work => work.ParentWorkId == containerWorkId && !work.IsDeleted)
            .ToListAsync();

    private async Task SeedAsync()
    {
        _person = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "PERSON",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(_person);

        _container = NewShift("CONTAINER", ShiftType.IsContainer, new TimeOnly(8, 0), new TimeOnly(16, 0));
        _taskA = NewShift("TASK_A", ShiftType.IsTask, new TimeOnly(8, 0), new TimeOnly(10, 0));
        _taskB = NewShift("TASK_B", ShiftType.IsTask, new TimeOnly(10, 0), new TimeOnly(12, 0));
        await _context.Shift.AddRangeAsync(_container, _taskA, _taskB);

        var template = new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = _container.Id,
            Weekday = (int)Day.DayOfWeek,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(16, 0)
        };
        await _context.ContainerTemplate.AddAsync(template);
        await _context.ContainerTemplateItem.AddRangeAsync(
            NewItem(template.Id, _taskA),
            NewItem(template.Id, _taskB));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static ContainerTemplateItem NewItem(Guid templateId, Shift task) => new()
    {
        Id = Guid.NewGuid(),
        ContainerTemplateId = templateId,
        ShiftId = task.Id,
        StartItem = task.StartShift,
        EndItem = task.EndShift
    };

    private static Shift NewShift(string suffix, ShiftType shiftType, TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        Abbreviation = "SCE",
        Description = "Scenario container expansion",
        Status = ShiftStatus.OriginalShift,
        FromDate = Day,
        UntilDate = Day,
        StartShift = start,
        EndShift = end,
        IsMonday = true,
        IsTuesday = true,
        IsWednesday = true,
        IsThursday = true,
        IsFriday = true,
        IsSaturday = true,
        IsSunday = true,
        ShiftType = shiftType,
        Quantity = 1,
        SumEmployees = 1,
        WorkTime = (decimal)(end - start).TotalHours
    };

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        context.ChangeTracker.Clear();
        var sql = $@"
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            UPDATE work SET parent_work_id = NULL WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%')
                OR shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM container_template_item
            WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
               OR container_template_id IN (
                    SELECT ct.id FROM container_template ct
                    JOIN shift s ON s.id = ct.container_id
                    WHERE s.name LIKE '{TestPrefix}%');
            DELETE FROM container_template WHERE container_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
