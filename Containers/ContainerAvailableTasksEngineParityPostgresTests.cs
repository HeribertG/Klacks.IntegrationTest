// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the day rule of the available-task list (ContainerAvailableTasksService, also the candidate source of the
/// container autofill) to the schedule engine against PostgreSQL (IT database klacks): for every holiday tuple the UI sends
/// (template editor slots false/false, true/true, true/false; schedule dialogs null and true/null) the listed tasks are
/// exactly the seeded tasks get_shift_schedule runs on that day. Every row carries the INTEGRATION_TEST_TASKDAYRULE_ prefix
/// and the cleanup deletes exactly those rows.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Containers;

[TestFixture]
[Category("RealDatabase")]
public class ContainerAvailableTasksEngineParityPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_TASKDAYRULE_";
    private const int Monday = 1;
    private const int DaysPerWeek = 7;

    private static readonly DateOnly NormalMonday = new(2026, 6, 1);
    private static readonly DateOnly HolidayMonday = new(2026, 6, 8);
    private static readonly DateOnly SeedFrom = new(2026, 1, 1);
    private static readonly TimeOnly From = new(8, 0);
    private static readonly TimeOnly Until = new(12, 0);

    private DataBaseContext _context = null!;
    private ContainerAvailableTasksService _availableTasks = null!;
    private ShiftScheduleService _schedule = null!;
    private Shift _container = null!;
    private List<Guid> _taskIds = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        await PopulateAsync();
        BuildServices();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [TestCase(null, null, TestName = "NormalMonday_ScheduleDialog_ListsTheEngineTasks")]
    [TestCase(false, false, TestName = "NormalMonday_TemplateEditorWeekdaySlot_ListsTheEngineTasks")]
    public async Task NormalMonday_ListsExactlyTheTasksTheEngineRuns(bool? isHoliday, bool? isWeekdayAndHoliday)
    {
        var engine = await EngineTasksAsync(NormalMonday, NormalMonday, []);

        var listed = await ListAsync(isHoliday, isWeekdayAndHoliday);

        engine.ShouldNotBeEmpty();
        listed.ShouldBe(engine, ignoreOrder: true);
    }

    [TestCase(true, null, TestName = "HolidayMonday_ScheduleDialog_ListsTheEngineTasks")]
    [TestCase(true, true, TestName = "HolidayMonday_TemplateEditorWeekdayAndHolidaySlot_ListsTheEngineTasks")]
    public async Task HolidayMonday_ListsExactlyTheTasksTheEngineRuns(bool? isHoliday, bool? isWeekdayAndHoliday)
    {
        var engine = await EngineTasksAsync(HolidayMonday, HolidayMonday, [HolidayMonday]);

        var listed = await ListAsync(isHoliday, isWeekdayAndHoliday);

        engine.ShouldNotBeEmpty();
        listed.ShouldBe(engine, ignoreOrder: true);
    }

    [Test]
    public async Task EveryHolidaySlot_ListsExactlyTheTasksTheEngineRunsOnAHolidayOfEveryWeekday()
    {
        var holidayWeek = Enumerable.Range(0, DaysPerWeek).Select(HolidayMonday.AddDays).ToList();
        var runs = await EngineRunsAsync(holidayWeek[0], holidayWeek[^1], holidayWeek);
        var onEveryHoliday = runs
            .GroupBy(r => r.ShiftId)
            .Where(g => g.Select(r => r.Date).Distinct().Count() == DaysPerWeek)
            .Select(g => g.Key)
            .ToList();

        var listed = await ListAsync(true, false);

        onEveryHoliday.ShouldNotBeEmpty();
        listed.ShouldBe(onEveryHoliday, ignoreOrder: true);
    }

    private async Task<List<Guid>> ListAsync(bool? isHoliday, bool? isWeekdayAndHoliday)
    {
        var tasks = await _availableTasks.GetAvailableTasksAsync(
            _container.Id, Monday, From, Until, isHoliday: isHoliday, isWeekdayAndHoliday: isWeekdayAndHoliday);
        return tasks.Select(t => t.Id).Where(_taskIds.Contains).ToList();
    }

    private async Task<List<Guid>> EngineTasksAsync(DateOnly start, DateOnly end, List<DateOnly> holidays)
    {
        var runs = await EngineRunsAsync(start, end, holidays);
        return runs.Select(r => r.ShiftId).Distinct().ToList();
    }

    private async Task<List<ShiftDayAssignment>> EngineRunsAsync(DateOnly start, DateOnly end, List<DateOnly> holidays)
    {
        var runs = await _schedule.GetShiftScheduleQuery(start, end, holidays).ToListAsync();
        return runs.Where(r => _taskIds.Contains(r.ShiftId)).ToList();
    }

    private async Task PopulateAsync()
    {
        _container = NewShift("Container", ShiftType.IsContainer, isTimeRange: false);
        _container.IsMonday = true;

        var monday = NewTask("Monday");
        monday.IsMonday = true;
        var mondayAndHoliday = NewTask("MondayAndHoliday");
        mondayAndHoliday.IsMonday = true;
        mondayAndHoliday.IsHoliday = true;
        var pureHoliday = NewTask("PureHoliday");
        pureHoliday.IsHoliday = true;
        var mondayWeekdayAndHoliday = NewTask("MondayWeekdayAndHoliday");
        mondayWeekdayAndHoliday.IsMonday = true;
        mondayWeekdayAndHoliday.IsWeekdayAndHoliday = true;
        var tuesdayWeekdayAndHoliday = NewTask("TuesdayWeekdayAndHoliday");
        tuesdayWeekdayAndHoliday.IsTuesday = true;
        tuesdayWeekdayAndHoliday.IsWeekdayAndHoliday = true;
        var tuesday = NewTask("Tuesday");
        tuesday.IsTuesday = true;
        var everyWeekdayAndHoliday = NewTask("EveryWeekdayAndHoliday");
        everyWeekdayAndHoliday.IsMonday = true;
        everyWeekdayAndHoliday.IsTuesday = true;
        everyWeekdayAndHoliday.IsWednesday = true;
        everyWeekdayAndHoliday.IsThursday = true;
        everyWeekdayAndHoliday.IsFriday = true;
        everyWeekdayAndHoliday.IsSaturday = true;
        everyWeekdayAndHoliday.IsSunday = true;
        everyWeekdayAndHoliday.IsWeekdayAndHoliday = true;

        var tasks = new[]
        {
            monday, mondayAndHoliday, pureHoliday, mondayWeekdayAndHoliday, tuesdayWeekdayAndHoliday, tuesday,
            everyWeekdayAndHoliday
        };
        _taskIds = tasks.Select(t => t.Id).ToList();

        await _context.Shift.AddAsync(_container);
        await _context.Shift.AddRangeAsync(tasks);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private void BuildServices()
    {
        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            Substitute.For<IShiftQueryPipelineService>(),
            Substitute.For<IShiftGroupManagementService>(),
            collectionUpdateService,
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper(),
            TestCompanyClock.Utc());
        var containerTemplateService = new ContainerTemplateService(
            Substitute.For<IUnitOfWork>(), Substitute.For<ILogger<ContainerTemplateService>>());
        _availableTasks = new ContainerAvailableTasksService(
            shiftRepository,
            new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>()),
            new ContainerTemplateRepository(
                _context, Substitute.For<ILogger<ContainerTemplate>>(), collectionUpdateService, containerTemplateService),
            Substitute.For<ILogger<ContainerAvailableTasksService>>());
        _schedule = new ShiftScheduleService(
            _context,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            Substitute.For<ILogger<ShiftScheduleService>>());
    }

    private static Shift NewTask(string name) => NewShift(name, ShiftType.IsTask, isTimeRange: true);

    private static Shift NewShift(string name, ShiftType shiftType, bool isTimeRange)
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = $"{Prefix}{name}",
            Abbreviation = name[..3].ToUpperInvariant(),
            Description = string.Empty,
            Status = ShiftStatus.OriginalShift,
            ShiftType = shiftType,
            FromDate = SeedFrom,
            StartShift = From,
            EndShift = Until,
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 0.5m,
            IsTimeRange = isTimeRange,
            IsSporadic = false
        };
    }

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        var shifts = $"SELECT id FROM shift WHERE starts_with(name, '{Prefix}')";
        var sql = $@"
            DELETE FROM group_item WHERE shift_id IN ({shifts});
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
