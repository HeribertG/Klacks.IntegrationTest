// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of the owner rule of 2026-11-15 (a shift inside a container gets no seat of its own; the container
/// is planned as a whole; a shift taken out of the container is plannable again), in the real plan and in a scenario. The
/// real WizardShiftBuilder reads the real get_shift_schedule, so the seats prove the wiring and the marker together.
/// Container templates carry no token and are not cloned: in a scenario a clone is marked through the items that name its
/// source shift, while a real row never is. The stored procedures must be loaded on the test database first. Only
/// INTEGRATION_TEST_ rows are written and removed; the day lies in 2099.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.ShiftSchedule;

[TestFixture]
[Category("RealDatabase")]
public class ContainerChildSeatsPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_CSEATS_";
    private static readonly DateOnly Day = new(2099, 11, 2);
    private static readonly DateOnly OtherDay = Day.AddDays(1);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ShiftScheduleService _scheduleService = null!;
    private WizardShiftBuilder _builder = null!;
    private Guid _scenarioToken;
    private Shift _container = null!;
    private Shift _child = null!;
    private Shift _regular = null!;
    private Shift _containerClone = null!;
    private Shift _childClone = null!;
    private Shift _regularClone = null!;
    private ContainerTemplateItem _childItem = null!;
    private ContainerTemplate _template = null!;
    private Client _person = null!;

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
        _scenarioToken = Guid.NewGuid();
        _scheduleService = new ShiftScheduleService(
            _context,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            Substitute.For<ILogger<ShiftScheduleService>>());
        _builder = new WizardShiftBuilder(_context, _scheduleService);
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task RealPlan_ChildGetsNoSeat_ContainerAndRegularShiftKeepOne()
    {
        var seats = await BuildSeatsAsync(null, _container, _child, _regular);

        SeatsOf(seats, _child).ShouldBe(0);
        SeatsOf(seats, _container).ShouldBe(1);
        SeatsOf(seats, _regular).ShouldBe(1);
    }

    [Test]
    public async Task Scenario_CloneOfAChildGetsNoSeat_CloneOfTheContainerAndOfARegularShiftKeepOne()
    {
        var seats = await BuildSeatsAsync(_scenarioToken, _containerClone, _childClone, _regularClone);

        SeatsOf(seats, _childClone).ShouldBe(0);
        SeatsOf(seats, _containerClone).ShouldBe(1);
        SeatsOf(seats, _regularClone).ShouldBe(1);
    }

    [Test]
    public async Task RealPlanAndScenario_ShiftTakenOutOfTheContainer_GetsItsSeatBack()
    {
        _context.ContainerTemplateItem.Remove(_childItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var realSeats = await BuildSeatsAsync(null, _container, _child, _regular);
        var scenarioSeats = await BuildSeatsAsync(_scenarioToken, _containerClone, _childClone, _regularClone);

        SeatsOf(realSeats, _child).ShouldBe(1);
        SeatsOf(scenarioSeats, _childClone).ShouldBe(1);
    }

    [Test]
    public async Task FullSchedule_MarksExactlyTheRowsTheBuilderLeavesOut_InBothWorlds()
    {
        var realRows = await _scheduleService.GetShiftScheduleListAsync(Day, Day);
        var scenarioRows = await _scheduleService.GetShiftScheduleListAsync(Day, Day, analyseToken: _scenarioToken);

        MarkerOf(realRows, _child).ShouldBeTrue();
        MarkerOf(realRows, _container).ShouldBeFalse();
        MarkerOf(realRows, _regular).ShouldBeFalse();
        MarkerOf(scenarioRows, _childClone).ShouldBeTrue();
        MarkerOf(scenarioRows, _containerClone).ShouldBeFalse();
        MarkerOf(scenarioRows, _regularClone).ShouldBeFalse();
    }

    [Test]
    public async Task PartialSchedule_MarksTheCloneOfAChildLikeTheFullSchedule()
    {
        var rows = await _scheduleService.GetShiftSchedulePartialAsync(
            [(_childClone.Id, Day), (_regularClone.Id, Day)], _scenarioToken);

        MarkerOf(rows, _childClone).ShouldBeTrue();
        MarkerOf(rows, _regularClone).ShouldBeFalse();
    }

    [Test]
    public async Task PartialSchedule_CloneOfAShiftTakenOutOfTheContainer_IsNotMarked()
    {
        _context.ContainerTemplateItem.Remove(_childItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var rows = await _scheduleService.GetShiftSchedulePartialAsync([(_childClone.Id, Day)], _scenarioToken);

        MarkerOf(rows, _childClone).ShouldBeFalse();
    }

    [Test]
    public async Task RealPlan_ARealRowIsNeverMarkedThroughASourceShift()
    {
        var realRowWithSource = NewShift("REAL_WITH_SOURCE", ShiftType.IsTask, token: null, sourceShiftId: _child.Id);
        await _context.Shift.AddAsync(realRowWithSource);
        await _context.SaveChangesAsync();

        var fullRows = await _scheduleService.GetShiftScheduleListAsync(Day, Day);
        var partialRows = await _scheduleService.GetShiftSchedulePartialAsync([(realRowWithSource.Id, Day)]);

        MarkerOf(fullRows, realRowWithSource).ShouldBeFalse();
        MarkerOf(partialRows, realRowWithSource).ShouldBeFalse();
    }

    [Test]
    public async Task PartialSchedule_OnADayTheTemplateDoesNotCover_MarksNothing_LikeTheFullSchedule_InBothWorlds()
    {
        await AssertPartialMatchesFullAsync(null, _child, _container, _regular);
        await AssertPartialMatchesFullAsync(_scenarioToken, _childClone, _containerClone, _regularClone);

        var partialRows = await _scheduleService.GetShiftSchedulePartialAsync(
            [(_child.Id, Day), (_child.Id, OtherDay)]);
        MarkerOf(partialRows, _child, Day).ShouldBeTrue();
        MarkerOf(partialRows, _child, OtherDay).ShouldBeFalse();
    }

    [Test]
    public async Task PartialSchedule_HolidayOnlyTemplate_MarksNothing_LikeTheFullScheduleWithoutHolidays()
    {
        _template.IsHoliday = true;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await AssertPartialMatchesFullAsync(null, _child, _container, _regular);
        var partialRows = await _scheduleService.GetShiftSchedulePartialAsync([(_child.Id, Day)]);
        MarkerOf(partialRows, _child, Day).ShouldBeFalse();
    }

    [Test]
    public async Task ContainerPlannedOnADay_TheChildCountsAsPlannedThatDayOnly_FullAndPartialAlike()
    {
        await AddWorkAsync(_container, token: null, Day);

        var full = await _scheduleService.GetShiftScheduleListAsync(Day, OtherDay);
        var partial = await _scheduleService.GetShiftSchedulePartialAsync([(_child.Id, Day), (_child.Id, OtherDay)]);

        EngagedOf(full, _child, Day).ShouldBe(_child.SumEmployees * _child.Quantity);
        EngagedOf(full, _child, OtherDay).ShouldBe(0);
        EngagedOf(partial, _child, Day).ShouldBe(_child.SumEmployees * _child.Quantity);
        EngagedOf(partial, _child, OtherDay).ShouldBe(0);
        EngagedOf(full, _regular, Day).ShouldBe(0);
    }

    [Test]
    public async Task SecondPartOfASplitContainerOnTheNextDay_DoesNotMarkTheNextDaysChildAsPlanned()
    {
        await AddTemplateForAsync(OtherDay);
        var firstPart = await AddWorkAsync(_container, token: null, Day);
        await AddWorkAsync(_container, token: null, OtherDay, splitFromWorkId: firstPart);

        var full = await _scheduleService.GetShiftScheduleListAsync(Day, OtherDay);
        var partial = await _scheduleService.GetShiftSchedulePartialAsync([(_child.Id, Day), (_child.Id, OtherDay)]);

        EngagedOf(full, _child, Day).ShouldBe(_child.SumEmployees * _child.Quantity);
        EngagedOf(full, _child, OtherDay).ShouldBe(0);
        EngagedOf(partial, _child, Day).ShouldBe(_child.SumEmployees * _child.Quantity);
        EngagedOf(partial, _child, OtherDay).ShouldBe(0);
    }

    [Test]
    public async Task ContainerNotPlanned_TheChildStaysUnplanned()
    {
        var full = await _scheduleService.GetShiftScheduleListAsync(Day, Day);

        EngagedOf(full, _child, Day).ShouldBe(0);
    }

    [Test]
    public async Task ContainerPlannedInAScenario_OnlyTheScenarioChildCountsAsPlanned()
    {
        await AddWorkAsync(_containerClone, _scenarioToken, Day);

        var scenarioRows = await _scheduleService.GetShiftScheduleListAsync(Day, Day, analyseToken: _scenarioToken);
        var realRows = await _scheduleService.GetShiftScheduleListAsync(Day, Day);
        var partialRows = await _scheduleService.GetShiftSchedulePartialAsync([(_childClone.Id, Day)], _scenarioToken);

        EngagedOf(scenarioRows, _childClone, Day).ShouldBe(_childClone.SumEmployees * _childClone.Quantity);
        EngagedOf(partialRows, _childClone, Day).ShouldBe(_childClone.SumEmployees * _childClone.Quantity);
        EngagedOf(realRows, _child, Day).ShouldBe(0);
    }

    [Test]
    public async Task ChildTakenOutOfTheContainer_IsNotPlannedByTheContainer()
    {
        await AddWorkAsync(_container, token: null, Day);
        _context.ContainerTemplateItem.Remove(_childItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var full = await _scheduleService.GetShiftScheduleListAsync(Day, Day);

        EngagedOf(full, _child, Day).ShouldBe(0);
    }

    private async Task AddTemplateForAsync(DateOnly date)
    {
        var template = new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = _container.Id,
            Weekday = (int)date.DayOfWeek,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(16, 0)
        };
        await _context.ContainerTemplate.AddAsync(template);
        await _context.ContainerTemplateItem.AddAsync(new ContainerTemplateItem
        {
            Id = Guid.NewGuid(),
            ContainerTemplateId = template.Id,
            ShiftId = _child.Id
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<Guid> AddWorkAsync(Shift shift, Guid? token, DateOnly date, Guid? splitFromWorkId = null)
    {
        var id = Guid.NewGuid();
        await _context.Work.AddAsync(new Work
        {
            Id = id,
            SplitFromWorkId = splitFromWorkId,
            ClientId = _person.Id,
            ShiftId = shift.Id,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private static int EngagedOf(IEnumerable<ShiftDayAssignment> rows, Shift shift, DateOnly date)
        => rows.Single(row => row.ShiftId == shift.Id && row.Date == date).Engaged;

    private async Task<IReadOnlyList<CoreShift>> BuildSeatsAsync(Guid? token, params Shift[] shifts)
        => await _builder.BuildAsync(shifts.Select(shift => shift.Id).ToList(), Day, Day, token, CancellationToken.None);

    private static int SeatsOf(IReadOnlyList<CoreShift> seats, Shift shift)
        => seats.Count(seat => seat.Id == shift.Id.ToString()
            && seat.Date == Day.ToString(DateTimeFormats.IsoDate, CultureInfo.InvariantCulture));

    private static bool MarkerOf(IEnumerable<ShiftDayAssignment> rows, Shift shift)
        => MarkerOf(rows, shift, Day);

    private static bool MarkerOf(IEnumerable<ShiftDayAssignment> rows, Shift shift, DateOnly date)
        => rows.Single(row => row.ShiftId == shift.Id && row.Date == date).IsInTemplateContainer;

    private async Task AssertPartialMatchesFullAsync(Guid? token, Shift child, Shift container, Shift regular)
    {
        var shifts = new[] { child, container, regular };
        var fullRows = await _scheduleService.GetShiftScheduleListAsync(Day, OtherDay, analyseToken: token);
        var pairs = shifts.SelectMany(shift => new[] { (shift.Id, Day), (shift.Id, OtherDay) }).ToList();
        var partialRows = await _scheduleService.GetShiftSchedulePartialAsync(pairs, token);

        foreach (var (shiftId, date) in pairs)
        {
            var shift = shifts.Single(candidate => candidate.Id == shiftId);
            MarkerOf(partialRows, shift, date).ShouldBe(MarkerOf(fullRows, shift, date), $"{shift.Name} on {date}");
        }
    }

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
        await _context.SaveChangesAsync();

        _container = NewShift("CONTAINER", ShiftType.IsContainer, token: null, sourceShiftId: null);
        _child = NewShift("CHILD", ShiftType.IsTask, token: null, sourceShiftId: null);
        _regular = NewShift("REGULAR", ShiftType.IsTask, token: null, sourceShiftId: null);
        await _context.Shift.AddRangeAsync(_container, _child, _regular);
        await _context.SaveChangesAsync();

        _containerClone = NewShift("CONTAINER_CLONE", ShiftType.IsContainer, _scenarioToken, _container.Id);
        _childClone = NewShift("CHILD_CLONE", ShiftType.IsTask, _scenarioToken, _child.Id);
        _regularClone = NewShift("REGULAR_CLONE", ShiftType.IsTask, _scenarioToken, _regular.Id);
        await _context.Shift.AddRangeAsync(_containerClone, _childClone, _regularClone);

        _template = new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = _container.Id,
            Weekday = (int)Day.DayOfWeek,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(16, 0)
        };
        _childItem = new ContainerTemplateItem
        {
            Id = Guid.NewGuid(),
            ContainerTemplateId = _template.Id,
            ShiftId = _child.Id
        };
        await _context.ContainerTemplate.AddAsync(_template);
        await _context.ContainerTemplateItem.AddAsync(_childItem);
        await _context.SaveChangesAsync();
    }

    private static Shift NewShift(string suffix, ShiftType shiftType, Guid? token, Guid? sourceShiftId) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        Abbreviation = "CST",
        Description = "Container seats",
        Status = ShiftStatus.OriginalShift,
        FromDate = Day,
        UntilDate = OtherDay,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
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
        WorkTime = 8m,
        AnalyseToken = token,
        ScenarioSourceShiftId = sourceShiftId
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
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM container_template_item
            WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
               OR container_template_id IN (
                    SELECT ct.id FROM container_template ct
                    JOIN shift s ON s.id = ct.container_id
                    WHERE s.name LIKE '{TestPrefix}%');
            DELETE FROM container_template
            WHERE container_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
