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

    private async Task<IReadOnlyList<CoreShift>> BuildSeatsAsync(Guid? token, params Shift[] shifts)
        => await _builder.BuildAsync(shifts.Select(shift => shift.Id).ToList(), Day, Day, token, CancellationToken.None);

    private static int SeatsOf(IReadOnlyList<CoreShift> seats, Shift shift)
        => seats.Count(seat => seat.Id == shift.Id.ToString()
            && seat.Date == Day.ToString(DateTimeFormats.IsoDate, CultureInfo.InvariantCulture));

    private static bool MarkerOf(IEnumerable<ShiftDayAssignment> rows, Shift shift)
        => rows.Single(row => row.ShiftId == shift.Id && row.Date == Day).IsInTemplateContainer;

    private async Task SeedAsync()
    {
        _container = NewShift("CONTAINER", ShiftType.IsContainer, token: null, sourceShiftId: null);
        _child = NewShift("CHILD", ShiftType.IsTask, token: null, sourceShiftId: null);
        _regular = NewShift("REGULAR", ShiftType.IsTask, token: null, sourceShiftId: null);
        await _context.Shift.AddRangeAsync(_container, _child, _regular);
        await _context.SaveChangesAsync();

        _containerClone = NewShift("CONTAINER_CLONE", ShiftType.IsContainer, _scenarioToken, _container.Id);
        _childClone = NewShift("CHILD_CLONE", ShiftType.IsTask, _scenarioToken, _child.Id);
        _regularClone = NewShift("REGULAR_CLONE", ShiftType.IsTask, _scenarioToken, _regular.Id);
        await _context.Shift.AddRangeAsync(_containerClone, _childClone, _regularClone);

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
        _childItem = new ContainerTemplateItem
        {
            Id = Guid.NewGuid(),
            ContainerTemplateId = template.Id,
            ShiftId = _child.Id
        };
        await _context.ContainerTemplate.AddAsync(template);
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
        UntilDate = Day,
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
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
