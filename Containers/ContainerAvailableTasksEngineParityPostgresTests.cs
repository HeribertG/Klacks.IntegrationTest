// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the slot rule of the available-task list (ContainerAvailableTasksService, also the candidate source of the
/// container autofill) to the schedule engine against PostgreSQL (IT database klacks). Three containers carry one template
/// each: the normal Monday slot, the generic holiday slot (every public holiday, stored under the FT weekday key) and the
/// Monday holiday slot. A marker task that runs every day sits in each template, so get_shift_schedule's
/// is_in_template_container tells on which days of the range (a normal Monday, a holiday Monday, a holiday Wednesday) each
/// slot applies. Per slot the listed tasks must be exactly the seeded tasks the engine runs on every one of those days.
/// Every row carries the INTEGRATION_TEST_TASKDAYRULE_ prefix and the cleanup deletes exactly those rows.
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
    private const int FtHolidayTemplateWeekday = 1;

    private static readonly DateOnly NormalMonday = new(2026, 6, 1);
    private static readonly DateOnly HolidayMonday = new(2026, 6, 8);
    private static readonly DateOnly HolidayWednesday = new(2026, 6, 10);
    private static readonly DateOnly SeedFrom = new(2026, 1, 1);
    private static readonly TimeOnly From = new(8, 0);
    private static readonly TimeOnly Until = new(12, 0);

    private DataBaseContext _context = null!;
    private ContainerAvailableTasksService _availableTasks = null!;
    private ShiftScheduleService _schedule = null!;
    private Dictionary<string, Slot> _slots = null!;
    private List<Guid> _candidateIds = null!;

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

    [TestCase("NormalMonday")]
    [TestCase("GenericHoliday")]
    [TestCase("MondayHoliday")]
    public async Task Slot_ListsExactlyTheTasksTheEngineRunsOnEveryDayTheSlotAppliesOn(string slotName)
    {
        var slot = _slots[slotName];
        var runs = await _schedule
            .GetShiftScheduleQuery(NormalMonday, HolidayWednesday, [HolidayMonday, HolidayWednesday])
            .ToListAsync();
        var slotDays = runs
            .Where(r => r.ShiftId == slot.MarkerId && r.IsInTemplateContainer)
            .Select(r => r.Date)
            .ToHashSet();
        var engine = _candidateIds
            .Where(id => slotDays.All(day => runs.Any(r => r.ShiftId == id && r.Date == day)))
            .ToList();

        var listed = (await _availableTasks.GetAvailableTasksAsync(
                slot.ContainerId, Monday, From, Until,
                isHoliday: slot.IsHoliday, isWeekdayAndHoliday: slot.IsWeekdayAndHoliday))
            .Select(t => t.Id)
            .Where(_candidateIds.Contains)
            .ToList();

        slotDays.ShouldBe(slot.ExpectedDays, ignoreOrder: true);
        engine.ShouldNotBeEmpty();
        listed.ShouldBe(engine, ignoreOrder: true);
    }

    private async Task PopulateAsync()
    {
        var candidates = new[]
        {
            NewTask("Monday", s => s.IsMonday = true),
            NewTask("MondayAndHoliday", s => { s.IsMonday = true; s.IsHoliday = true; }),
            NewTask("PureHoliday", s => s.IsHoliday = true),
            NewTask("WednesdayAndHoliday", s => { s.IsWednesday = true; s.IsHoliday = true; }),
            NewTask("MondayWeekdayAndHoliday", s => { s.IsMonday = true; s.IsWeekdayAndHoliday = true; }),
            NewTask("WednesdayWeekdayAndHoliday", s => { s.IsWednesday = true; s.IsWeekdayAndHoliday = true; }),
            NewTask("Tuesday", s => s.IsTuesday = true),
            NewTask("EveryWeekdayAndHoliday", s => { TickEveryWeekday(s); s.IsWeekdayAndHoliday = true; })
        };
        _candidateIds = candidates.Select(c => c.Id).ToList();
        await _context.Shift.AddRangeAsync(candidates);

        _slots = new Dictionary<string, Slot>
        {
            ["NormalMonday"] = await AddSlotAsync("NormalMonday", Monday, false, false, [NormalMonday]),
            ["GenericHoliday"] = await AddSlotAsync(
                "GenericHoliday", FtHolidayTemplateWeekday, true, false, [HolidayMonday, HolidayWednesday]),
            ["MondayHoliday"] = await AddSlotAsync("MondayHoliday", Monday, true, true, [HolidayMonday])
        };

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<Slot> AddSlotAsync(
        string name, int weekday, bool isHoliday, bool isWeekdayAndHoliday, DateOnly[] expectedDays)
    {
        var container = NewShift($"Container{name}", ShiftType.IsContainer, isTimeRange: false);
        container.IsMonday = true;
        container.IsHoliday = isHoliday && !isWeekdayAndHoliday;
        container.IsWeekdayAndHoliday = isWeekdayAndHoliday;
        var marker = NewTask($"Marker{name}", s => { TickEveryWeekday(s); s.IsHoliday = true; });

        var template = new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = container.Id,
            Weekday = weekday,
            IsHoliday = isHoliday,
            IsWeekdayAndHoliday = isWeekdayAndHoliday,
            FromTime = From,
            UntilTime = Until,
            ContainerTemplateItems =
            [
                new ContainerTemplateItem { Id = Guid.NewGuid(), ShiftId = marker.Id, StartItem = From, EndItem = Until }
            ]
        };

        await _context.Shift.AddRangeAsync(container, marker);
        await _context.ContainerTemplate.AddAsync(template);
        return new Slot(container.Id, marker.Id, isHoliday, isWeekdayAndHoliday, expectedDays);
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

    private static void TickEveryWeekday(Shift shift)
    {
        shift.IsMonday = true;
        shift.IsTuesday = true;
        shift.IsWednesday = true;
        shift.IsThursday = true;
        shift.IsFriday = true;
        shift.IsSaturday = true;
        shift.IsSunday = true;
    }

    private static Shift NewTask(string name, Action<Shift> flags)
    {
        var task = NewShift(name, ShiftType.IsTask, isTimeRange: true);
        flags(task);
        return task;
    }

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
            DELETE FROM container_template_item WHERE shift_id IN ({shifts})
                OR container_template_id IN (SELECT id FROM container_template WHERE container_id IN ({shifts}));
            DELETE FROM container_template WHERE container_id IN ({shifts});
            DELETE FROM group_item WHERE shift_id IN ({shifts});
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed record Slot(
        Guid ContainerId, Guid MarkerId, bool IsHoliday, bool IsWeekdayAndHoliday, DateOnly[] ExpectedDays);
}
