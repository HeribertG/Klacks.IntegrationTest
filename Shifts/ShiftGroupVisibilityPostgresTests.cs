// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end group visibility of the shift reads against PostgreSQL (IT database klacks) with the real
/// UserService, GroupVisibilityService and ShiftGroupFilterService: shift/order list (GetTruncatedListQueryHandler),
/// GET Shifts/{id}, Shifts/ByIds, the schedule stored procedures and the partial schedule. Callers: a Supervisor who
/// sees one root group (and therefore its child), a non-admin without any visible group, and an admin. Every row
/// carries the INTEGRATION_TEST_SHIFTVIS_ prefix and the cleanup deletes exactly those rows.
/// </summary>

using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.Shifts;

[TestFixture]
[Category("RealDatabase")]
public class ShiftGroupVisibilityPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_SHIFTVIS_";
    private const int PageSize = 100;

    private DataBaseContext _context = null!;
    private ScheduleMapper _scheduleMapper = null!;
    private IShiftRepository _shiftRepository = null!;
    private ShiftScheduleService _shiftScheduleService = null!;
    private ShiftGroupFilterService _groupFilter = null!;
    private string _searchToken = null!;

    private AppUser _supervisor = null!;
    private AppUser _noGroupUser = null!;
    private AppUser _admin = null!;
    private Guid _visibleRootId;
    private Guid _visibleChildId;
    private Guid _hiddenRootId;

    private Shift _visibleRootShift = null!;
    private Shift _visibleChildShift = null!;
    private Shift _hiddenShift = null!;
    private Shift _mixedShift = null!;
    private Shift _groupLessShift = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        _searchToken = "shvis" + Guid.NewGuid().ToString("N")[..10];
        await PopulateAsync();
        BuildPipeline();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ShiftList_Supervisor_SeesVisibleChildMixedAndGroupLess_NotHidden()
    {
        var ids = await ListAsAsync(_supervisor, selectedGroup: null);

        ids.ShouldContain(_visibleRootShift.Id);
        ids.ShouldContain(_visibleChildShift.Id, "subgroups of the visible root are visible");
        ids.ShouldContain(_mixedShift.Id, "a shift in a visible and a hidden group is visible");
        ids.ShouldContain(_groupLessShift.Id, "group-less shifts are visible to everyone");
        ids.ShouldNotContain(_hiddenShift.Id);
    }

    [Test]
    public async Task ShiftList_UserWithoutVisibleGroup_SeesOnlyGroupLess()
    {
        var ids = await ListAsAsync(_noGroupUser, selectedGroup: null);

        ids.ShouldContain(_groupLessShift.Id);
        ids.ShouldNotContain(_visibleRootShift.Id);
        ids.ShouldNotContain(_visibleChildShift.Id);
        ids.ShouldNotContain(_mixedShift.Id);
        ids.ShouldNotContain(_hiddenShift.Id);
    }

    [Test]
    public async Task ShiftList_Supervisor_SelectingHiddenGroup_DoesNotReturnItsShifts()
    {
        var ids = await ListAsAsync(_supervisor, selectedGroup: _hiddenRootId);

        ids.ShouldNotContain(_hiddenShift.Id);
        ids.ShouldNotContain(_mixedShift.Id);
        ids.ShouldContain(_groupLessShift.Id);
    }

    [Test]
    public async Task ShiftList_Admin_SeesEverything()
    {
        var ids = await ListAsAsync(_admin, selectedGroup: null);

        ids.ShouldContain(_hiddenShift.Id);
        ids.ShouldContain(_visibleRootShift.Id);
        ids.ShouldContain(_groupLessShift.Id);
    }

    [Test]
    public async Task ByIds_Supervisor_DropsHiddenShift()
    {
        var handler = new GetShiftsByIdsQueryHandler(
            _shiftRepository, _groupFilter, _scheduleMapper, Substitute.For<ILogger<GetShiftsByIdsQueryHandler>>());
        var requested = new List<Guid> { _visibleChildShift.Id, _hiddenShift.Id, _groupLessShift.Id };

        List<ShiftResource> result;
        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            result = await handler.Handle(new GetShiftsByIdsQuery(requested), CancellationToken.None);
        }

        result.Select(r => r.Id).ShouldBe([_visibleChildShift.Id, _groupLessShift.Id], ignoreOrder: true);
    }

    [Test]
    public async Task ByIds_UserWithoutVisibleGroup_ReturnsOnlyGroupLess()
    {
        var handler = new GetShiftsByIdsQueryHandler(
            _shiftRepository, _groupFilter, _scheduleMapper, Substitute.For<ILogger<GetShiftsByIdsQueryHandler>>());
        var requested = new List<Guid> { _visibleChildShift.Id, _hiddenShift.Id, _groupLessShift.Id };

        List<ShiftResource> result;
        using (ExecutionPrincipal.Begin(Guid.Parse(_noGroupUser.Id)))
        {
            result = await handler.Handle(new GetShiftsByIdsQuery(requested), CancellationToken.None);
        }

        result.Select(r => r.Id).ShouldBe([_groupLessShift.Id]);
    }

    [Test]
    public async Task GetById_Supervisor_HiddenShift_IsAnsweredLikeMissing()
    {
        var handler = new GetQueryHandler(
            _shiftRepository, _groupFilter, _scheduleMapper, Substitute.For<ILogger<GetQueryHandler>>());

        Exception? hiddenError;
        Exception? missingError;
        ShiftResource visible;
        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            hiddenError = await CaptureAsync(() => handler.Handle(new GetQuery<ShiftResource>(_hiddenShift.Id), CancellationToken.None));
            missingError = await CaptureAsync(() => handler.Handle(new GetQuery<ShiftResource>(Guid.NewGuid()), CancellationToken.None));
            visible = await handler.Handle(new GetQuery<ShiftResource>(_visibleChildShift.Id), CancellationToken.None);
        }

        hiddenError.ShouldNotBeNull();
        missingError.ShouldNotBeNull();
        hiddenError.GetType().ShouldBe(missingError.GetType(), "hidden must be indistinguishable from missing");
        visible.Id.ShouldBe(_visibleChildShift.Id);
    }

    [Test]
    public async Task GetById_Admin_HiddenGroupShift_IsReturned()
    {
        var handler = new GetQueryHandler(
            _shiftRepository, _groupFilter, _scheduleMapper, Substitute.For<ILogger<GetQueryHandler>>());

        ShiftResource result;
        using (ExecutionPrincipal.Begin(Guid.Parse(_admin.Id)))
        {
            result = await handler.Handle(new GetQuery<ShiftResource>(_hiddenShift.Id), CancellationToken.None);
        }

        result.Id.ShouldBe(_hiddenShift.Id);
    }

    [Test]
    public async Task CutList_Supervisor_DropsHiddenCut_KeepsGroupLessCut()
    {
        var order = NewShift("ORDER");
        order.Status = ShiftStatus.SealedOrder;
        var hiddenCut = NewShift("CUT_HIDDEN", _hiddenRootId);
        var groupLessCut = NewShift("CUT_GROUPLESS");
        foreach (var cut in new[] { hiddenCut, groupLessCut })
        {
            cut.Status = ShiftStatus.SplitShift;
            cut.OriginalId = order.Id;
        }

        _context.Shift.AddRange(order, hiddenCut, groupLessCut);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var handler = new CutListQueryhandler(
            _shiftRepository, _groupFilter, _scheduleMapper, Substitute.For<ILogger<CutListQueryhandler>>());

        List<Guid> supervisorIds;
        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            supervisorIds = (await handler.Handle(new CutListQuery(order.Id), CancellationToken.None)).Select(c => c.Id).ToList();
        }

        List<Guid> adminIds;
        using (ExecutionPrincipal.Begin(Guid.Parse(_admin.Id)))
        {
            adminIds = (await handler.Handle(new CutListQuery(order.Id), CancellationToken.None)).Select(c => c.Id).ToList();
        }

        supervisorIds.ShouldBe([groupLessCut.Id]);
        adminIds.ShouldBe([hiddenCut.Id, groupLessCut.Id], ignoreOrder: true);
    }

    [Test]
    public async Task Schedule_UserWithoutVisibleGroup_SeesNoGroupedShift()
    {
        var ids = await ScheduleShiftIdsAsAsync(_noGroupUser);

        ids.ShouldNotContain(_visibleRootShift.Id);
        ids.ShouldNotContain(_hiddenShift.Id);
        ids.ShouldNotContain(_mixedShift.Id);
    }

    [Test]
    public async Task Schedule_Supervisor_SeesRootAndChild_NotHidden()
    {
        var ids = await ScheduleShiftIdsAsAsync(_supervisor);

        ids.ShouldContain(_visibleRootShift.Id);
        ids.ShouldContain(_visibleChildShift.Id);
        ids.ShouldContain(_mixedShift.Id);
        ids.ShouldNotContain(_hiddenShift.Id);
    }

    [Test]
    public async Task Schedule_Admin_SeesHiddenGroupShift()
    {
        var ids = await ScheduleShiftIdsAsAsync(_admin);

        ids.ShouldContain(_hiddenShift.Id);
        ids.ShouldContain(_visibleRootShift.Id);
    }

    [Test]
    public async Task SchedulePartial_Supervisor_DropsHiddenShift_KeepsGroupLess()
    {
        var repository = new ShiftScheduleRepository(
            _shiftScheduleService, _groupFilter, Substitute.For<IShiftScheduleFilterService>(),
            Substitute.For<ILogger<ShiftScheduleRepository>>());
        var date = NextMonday();
        var filter = new ShiftSchedulePartialFilter
        {
            ShiftDatePairs =
            [
                new ShiftDatePairFilter { ShiftId = _visibleChildShift.Id, Date = ToUtc(date) },
                new ShiftDatePairFilter { ShiftId = _hiddenShift.Id, Date = ToUtc(date) },
                new ShiftDatePairFilter { ShiftId = _groupLessShift.Id, Date = ToUtc(date) }
            ]
        };

        List<Guid> ids;
        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            ids = (await repository.GetShiftSchedulePartialAsync(filter, CancellationToken.None)).Select(a => a.ShiftId).ToList();
        }

        ids.ShouldContain(_visibleChildShift.Id);
        ids.ShouldContain(_groupLessShift.Id);
        ids.ShouldNotContain(_hiddenShift.Id);
    }

    private async Task<List<Guid>> ListAsAsync(AppUser caller, Guid? selectedGroup)
    {
        var handler = new GetTruncatedListQueryHandler(
            _shiftRepository, _groupFilter, _scheduleMapper, Substitute.For<ILogger<GetTruncatedListQueryHandler>>());
        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true,
            IsTimeRange = true,
            IsSporadic = true,
            SearchString = _searchToken,
            SelectedGroup = selectedGroup,
            OrderBy = string.Empty,
            SortOrder = string.Empty,
            NumberOfItemsPerPage = PageSize
        };

        using (ExecutionPrincipal.Begin(Guid.Parse(caller.Id)))
        {
            var result = await handler.Handle(new GetTruncatedListQuery(filter), CancellationToken.None);
            return result.Shifts.Select(s => s.Id).ToList();
        }
    }

    private async Task<List<Guid>> ScheduleShiftIdsAsAsync(AppUser caller)
    {
        var date = NextMonday();
        using (ExecutionPrincipal.Begin(Guid.Parse(caller.Id)))
        {
            var groupIds = await _groupFilter.ResolveShiftScheduleGroupIdsAsync(null);
            return await _shiftScheduleService.GetShiftScheduleQuery(date, date, null, groupIds)
                .Select(a => a.ShiftId)
                .Distinct()
                .ToListAsync();
        }
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static DateOnly NextMonday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday);
    }

    private static DateTime ToUtc(DateOnly date) => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    private async Task PopulateAsync()
    {
        var visibleRoot = NewGroup("VISIBLE_ROOT", parent: null);
        var visibleChild = NewGroup("VISIBLE_CHILD", parent: visibleRoot.Id);
        var hiddenRoot = NewGroup("HIDDEN_ROOT", parent: null);
        _visibleRootId = visibleRoot.Id;
        _visibleChildId = visibleChild.Id;
        _hiddenRootId = hiddenRoot.Id;

        _supervisor = NewUser("SUPERVISOR");
        _noGroupUser = NewUser("NOGROUP");
        _admin = NewUser("ADMIN");

        _context.Group.AddRange(visibleRoot, visibleChild, hiddenRoot);
        _context.AppUser.AddRange(_supervisor, _noGroupUser, _admin);
        await _context.SaveChangesAsync();

        _context.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = _supervisor.Id, GroupId = _visibleRootId });

        _visibleRootShift = NewShift("ROOT", _visibleRootId);
        _visibleChildShift = NewShift("CHILD", _visibleChildId);
        _hiddenShift = NewShift("HIDDEN", _hiddenRootId);
        _mixedShift = NewShift("MIXED", _hiddenRootId, _visibleRootId);
        _groupLessShift = NewShift("GROUPLESS");
        _context.Shift.AddRange(_visibleRootShift, _visibleChildShift, _hiddenShift, _mixedShift, _groupLessShift);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private void BuildPipeline()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var userManager = BuildUserManager();
        var userService = new UserService(accessor, userManager, Substitute.For<ILogger<UserService>>());
        var groupVisibility = new GroupVisibilityService(_context, userManager, userService);
        _groupFilter = new ShiftGroupFilterService(groupVisibility, userService);
        _scheduleMapper = new ScheduleMapper();

        var queryPipeline = new ShiftQueryPipelineService(
            new DateRangeFilterService(),
            new ShiftSearchService(),
            new ShiftSortingService(),
            new ShiftStatusFilterService(),
            new ShiftPaginationService());
        _shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            queryPipeline,
            new ShiftGroupManagementService(_context, Substitute.For<ILogger<ShiftGroupManagementService>>()),
            new EntityCollectionUpdateService(_context),
            new ShiftValidator(),
            _scheduleMapper,
            TestCompanyClock.Utc());

        _shiftScheduleService = new ShiftScheduleService(
            _context,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            Substitute.For<ILogger<ShiftScheduleService>>());
    }

    private UserManager<AppUser> BuildUserManager()
    {
        var userManager = Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(),
            Substitute.For<IOptions<IdentityOptions>>(),
            Substitute.For<IPasswordHasher<AppUser>>(),
            new List<IUserValidator<AppUser>>(),
            new List<IPasswordValidator<AppUser>>(),
            Substitute.For<ILookupNormalizer>(),
            Substitute.For<IdentityErrorDescriber>(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<AppUser>>>());
        foreach (var user in new[] { _supervisor, _noGroupUser, _admin })
        {
            userManager.FindByIdAsync(user.Id).Returns(user);
            IList<string> roles = user == _admin ? new List<string> { Roles.Admin } : new List<string>();
            userManager.GetRolesAsync(user).Returns(Task.FromResult(roles));
            userManager.IsInRoleAsync(user, Roles.Admin).Returns(user == _admin);
        }

        return userManager;
    }

    private Group NewGroup(string suffix, Guid? parent) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Description = "Shift group visibility integration test",
        ValidFrom = DateTime.UtcNow.AddYears(-1),
        Parent = parent,
        Root = parent
    };

    private Shift NewShift(string suffix, params Guid[] groupIds)
    {
        var shiftId = Guid.NewGuid();
        return new Shift
        {
            Id = shiftId,
            Name = $"{Prefix}{suffix} {_searchToken}",
            Abbreviation = "SVT",
            Description = "Shift group visibility integration test",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 8m,
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            Quantity = 1,
            SumEmployees = 1,
            GroupItems = groupIds
                .Select(groupId => new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = shiftId })
                .ToList()
        };
    }

    private static AppUser NewUser(string suffix)
    {
        var email = $"{Prefix}{suffix}@example.invalid";
        return new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = Prefix,
            LastName = suffix,
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        var sql = $@"
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM group_visibility WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_visibility WHERE app_user_id IN (SELECT id FROM ""AspNetUsers"" WHERE starts_with(email, '{Prefix}'));
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""AspNetUsers"" WHERE starts_with(email, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
