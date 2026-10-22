// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Handlers.Dashboard;
using Klacks.Api.Application.Queries.Dashboard;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Dashboard;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.ReadModels;

/// <summary>
/// Pins, against real PostgreSQL and get_shift_schedule, the dashboard shift-coverage statistics: the company month
/// of the clock, the schedule restricted to the caller's visible roots, a shift day contributing Quantity x
/// SumEmployees slots to each visible group it is linked to, and a shift of a hidden group contributing nothing; the
/// materialised schedule list equals the former GetShiftScheduleQuery composition. The
/// clock is pinned to a month in 2031 so only seeded shifts plan there. Every shift and group carries the
/// INTEGRATION_TEST_CoverageReads_ prefix and the cleanup deletes exactly those and their group items.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class ShiftCoverageReadShapesTests
{
    private const string Prefix = "INTEGRATION_TEST_CoverageReads_";
    private const int DaysInMarch2031 = 31;
    private static readonly DateOnly Today = new(2031, 3, 15);
    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private DataBaseContext _context = null!;
    private ShiftScheduleService _scheduleService = null!;
    private Group _visible = null!;
    private Group _hidden = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        _scheduleService = new ShiftScheduleService(
            _context,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            NullLogger<ShiftScheduleService>.Instance);
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task Coverage_CountsTheVisibleGroupsSlotsOfTheCompanyMonth_AndHidesOtherGroups()
    {
        var visibility = Substitute.For<IGroupVisibilityService>();
        visibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([_visible.Id], [_visible.Id]));
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);
        var handler = new GetShiftCoverageStatisticsQueryHandler(
            new ShiftCoverageReadRepository(_context),
            _scheduleService,
            visibility,
            clock,
            NullLogger<GetShiftCoverageStatisticsQueryHandler>.Instance);

        var result = (await handler.Handle(new GetShiftCoverageStatisticsQuery(), CancellationToken.None)).ToList();

        var row = result.ShouldHaveSingleItem();
        row.GroupId.ShouldBe(_visible.Id);
        row.GroupName.ShouldBe(_visible.Name);
        row.TotalSlots.ShouldBe(DaysInMarch2031 * 2);
        row.CoveredSlots.ShouldBe(0);
        row.TotalWorkEntries.ShouldBe(0);
    }

    [Test]
    public async Task ScheduleList_EqualsTheFormerQueryComposition()
    {
        var start = new DateOnly(2031, 3, 1);
        var end = new DateOnly(2031, 3, 31);
        var visible = new List<Guid> { _visible.Id };

        var list = await _scheduleService.GetShiftScheduleListAsync(start, end, visibleGroupIds: visible);
        var legacy = await _scheduleService.GetShiftScheduleQuery(start, end, visibleGroupIds: visible).ToListAsync();

        list.Select(r => (r.ShiftId, r.Date, r.Quantity, r.SumEmployees, r.Engaged))
            .ShouldBe(legacy.Select(r => (r.ShiftId, r.Date, r.Quantity, r.SumEmployees, r.Engaged)));
        list.Count.ShouldBe(DaysInMarch2031);
    }

    private async Task SeedAsync()
    {
        _visible = NewGroup("Visible");
        _hidden = NewGroup("Hidden");
        _context.Group.AddRange(_visible, _hidden);

        var visibleShift = NewShift("VisibleShift", quantity: 2);
        var hiddenShift = NewShift("HiddenShift", quantity: 3);
        _context.Shift.AddRange(visibleShift, hiddenShift);
        await _context.SaveChangesAsync();

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ShiftId = visibleShift.Id, GroupId = _visible.Id, ValidFrom = Past },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = hiddenShift.Id, GroupId = _hidden.Id, ValidFrom = Past });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Group NewGroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Description = Prefix,
        ValidFrom = Past,
    };

    private static Shift NewShift(string suffix, int quantity) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Abbreviation = "COV",
        Description = Prefix,
        Status = ShiftStatus.OriginalShift,
        ShiftType = ShiftType.IsTask,
        FromDate = new DateOnly(2031, 1, 1),
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        AfterShift = new TimeOnly(0, 0),
        BeforeShift = new TimeOnly(0, 0),
        WorkTime = 8.0m,
        IsMonday = true,
        IsTuesday = true,
        IsWednesday = true,
        IsThursday = true,
        IsFriday = true,
        IsSaturday = true,
        IsSunday = true,
        Quantity = quantity,
        SumEmployees = 1,
    };

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
