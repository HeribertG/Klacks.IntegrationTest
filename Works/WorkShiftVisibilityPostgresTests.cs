// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end group visibility of the shift a Work is booked on, against PostgreSQL (IT database klacks) with the real
/// UserService, GroupVisibilityService, ShiftGroupFilterService, ShiftVisibilityReadRepository and ShiftVisibilityGuard
/// and the real WorkRepository / UnitOfWork: POST, PUT and bulk add of Works. Callers: a Supervisor who sees one root
/// group and an admin. A Work on a hidden shift must be answered exactly like a Work on a missing shift and nothing may
/// be written; a PUT that keeps a stored hidden shift stays saveable, admins are unchanged, and a scenario clone of a
/// visible shift (which carries copied group items) stays bookable. The client guard is a pass-through substitute so
/// only the shift check is under test. Every row carries the INTEGRATION_TEST_WORKSHIFTVIS_ prefix and the cleanup
/// deletes exactly those rows (Works by their prefixed client).
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Helpers;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WorkPostHandler = Klacks.Api.Application.Handlers.Works.PostCommandHandler;
using WorkPutHandler = Klacks.Api.Application.Handlers.Works.PutCommandHandler;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class WorkShiftVisibilityPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_WORKSHIFTVIS_";
    private const string IdPlaceholder = "<id>";
    private const string ChangedInformation = Prefix + "CHANGED";

    private static readonly DateOnly Day = new(2026, 6, 3);

    private DataBaseContext _context = null!;
    private ScheduleMapper _scheduleMapper = null!;
    private UnitOfWork _unitOfWork = null!;
    private WorkRepository _workRepository = null!;
    private ShiftVisibilityGuard _shiftGuard = null!;

    private AppUser _supervisor = null!;
    private AppUser _admin = null!;
    private Guid _visibleRootId;
    private Guid _hiddenRootId;
    private Client _client = null!;
    private Shift _visibleShift = null!;
    private Shift _hiddenShift = null!;
    private Shift _scenarioClone = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
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
    public async Task FilterVisible_Supervisor_PassesVisibleShiftAndItsScenarioClone_NotHiddenOrMissing()
    {
        var missing = Guid.NewGuid();

        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            var visible = await _shiftGuard.FilterVisibleAsync([_visibleShift.Id, _hiddenShift.Id, _scenarioClone.Id, missing]);

            visible.ShouldBe([_visibleShift.Id, _scenarioClone.Id], ignoreOrder: true);
        }
    }

    [Test]
    public async Task Post_Supervisor_HiddenShift_IsAnsweredLikeMissingShift_NothingWritten()
    {
        var missingShiftId = Guid.NewGuid();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => PostAsAsync(_supervisor, _hiddenShift.Id));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => PostAsAsync(_supervisor, missingShiftId));

        hidden.Message.Replace(_hiddenShift.Id.ToString(), IdPlaceholder)
            .ShouldBe(missing.Message.Replace(missingShiftId.ToString(), IdPlaceholder));
        hidden.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShift.Id));
        (await StoredWorksAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Post_Supervisor_VisibleShift_IsWritten()
    {
        (await PostAsAsync(_supervisor, _visibleShift.Id)).ShouldNotBeNull();

        (await StoredWorksAsync()).Select(w => w.ShiftId).ShouldBe([_visibleShift.Id]);
    }

    [Test]
    public async Task Post_Admin_HiddenShift_IsWritten()
    {
        (await PostAsAsync(_admin, _hiddenShift.Id)).ShouldNotBeNull();

        (await StoredWorksAsync()).Select(w => w.ShiftId).ShouldBe([_hiddenShift.Id]);
    }

    [Test]
    public async Task Put_Supervisor_MovingOntoHiddenShift_IsAnsweredLikeMissingShift_RowUnchanged()
    {
        var stored = await SeedWorkAsync(_visibleShift.Id);
        var missingShiftId = Guid.NewGuid();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => PutAsAsync(_supervisor, ResourceFor(stored.Id, _hiddenShift.Id)));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => PutAsAsync(_supervisor, ResourceFor(stored.Id, missingShiftId)));

        hidden.Message.Replace(_hiddenShift.Id.ToString(), IdPlaceholder)
            .ShouldBe(missing.Message.Replace(missingShiftId.ToString(), IdPlaceholder));
        var reloaded = (await StoredWorksAsync()).Single();
        reloaded.ShiftId.ShouldBe(_visibleShift.Id);
        reloaded.Information.ShouldBeNull();
    }

    [Test]
    public async Task Put_Supervisor_KeepingAStoredHiddenShift_IsSaved()
    {
        var stored = await SeedWorkAsync(_hiddenShift.Id);

        (await PutAsAsync(_supervisor, ResourceFor(stored.Id, _hiddenShift.Id))).ShouldNotBeNull();

        var reloaded = (await StoredWorksAsync()).Single();
        reloaded.ShiftId.ShouldBe(_hiddenShift.Id);
        reloaded.Information.ShouldBe(ChangedInformation);
    }

    [Test]
    public async Task Put_Admin_MovingOntoHiddenShift_IsSaved()
    {
        var stored = await SeedWorkAsync(_visibleShift.Id);

        (await PutAsAsync(_admin, ResourceFor(stored.Id, _hiddenShift.Id))).ShouldNotBeNull();

        (await StoredWorksAsync()).Single().ShiftId.ShouldBe(_hiddenShift.Id);
    }

    [Test]
    public async Task Bulk_Supervisor_OneHiddenShift_IsAnsweredLikeMissingShift_NothingWritten()
    {
        var missingShiftId = Guid.NewGuid();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => BulkAsAsync(_supervisor, null, _visibleShift.Id, _hiddenShift.Id));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => BulkAsAsync(_supervisor, null, _visibleShift.Id, missingShiftId));

        hidden.Message.Replace(_hiddenShift.Id.ToString(), IdPlaceholder)
            .ShouldBe(missing.Message.Replace(missingShiftId.ToString(), IdPlaceholder));
        (await StoredWorksAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Bulk_Admin_HiddenShift_IsWritten()
    {
        var response = await BulkAsAsync(_admin, null, _hiddenShift.Id);

        response.SuccessCount.ShouldBe(1);
        (await StoredWorksAsync()).Select(w => w.ShiftId).ShouldBe([_hiddenShift.Id]);
    }

    [Test]
    public async Task Bulk_Supervisor_ScenarioCloneOfVisibleShift_StaysBookable()
    {
        var response = await BulkAsAsync(_supervisor, _scenarioClone.AnalyseToken, _scenarioClone.Id);

        response.SuccessCount.ShouldBe(1);
        var stored = (await StoredWorksAsync()).Single();
        stored.ShiftId.ShouldBe(_scenarioClone.Id);
        stored.AnalyseToken.ShouldBe(_scenarioClone.AnalyseToken);
    }

    private async Task<WorkResource?> PostAsAsync(AppUser user, Guid shiftId)
    {
        var handler = new WorkPostHandler(
            _workRepository, AllClientsVisible(), _scheduleMapper, Substitute.For<IPeriodHoursService>(),
            new ScheduleEntriesService(_context, Substitute.For<ILogger<ScheduleEntriesService>>()),
            Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkNotificationFacade>(),
            Substitute.For<IShiftDefaultExpensesApplier>(), Substitute.For<IContainerWorkExpansionService>(),
            Substitute.For<ISelectedGroupContextResolver>(), Substitute.For<IDayLockService>(), _unitOfWork,
            Substitute.For<IOvertimeCascadeService>(), Substitute.For<IWorkWriteGuard>(),
            Substitute.For<IClientPlanningPeriodGuard>(), _shiftGuard, Substitute.For<ILogger<WorkPostHandler>>());

        using (ExecutionPrincipal.Begin(Guid.Parse(user.Id)))
        {
            var result = await handler.Handle(
                new PostCommand<WorkResource>(ResourceFor(Guid.Empty, shiftId)), CancellationToken.None);
            _context.ChangeTracker.Clear();
            return result;
        }
    }

    private async Task<WorkResource?> PutAsAsync(AppUser user, WorkResource resource)
    {
        var rowAccessGuard = Substitute.For<IScheduleRowAccessGuard>();
        rowAccessGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([]));
        var handler = new WorkPutHandler(
            _workRepository, rowAccessGuard, _scheduleMapper, Substitute.For<IPeriodHoursService>(),
            new ScheduleEntriesService(_context, Substitute.For<ILogger<ScheduleEntriesService>>()),
            Substitute.For<IScheduleCompletionService>(), Substitute.For<IWorkNotificationFacade>(),
            Substitute.For<IContainerWorkCascadeService>(), Substitute.For<ISelectedGroupContextResolver>(),
            Substitute.For<IDayLockService>(), _unitOfWork, Substitute.For<IOvertimeCascadeService>(), conflictChecker,
            Substitute.For<IHttpContextAccessor>(), new ParentWorkLockGuard(new WorkLockLevelService()),
            Substitute.For<IClientPlanningPeriodGuard>(), _shiftGuard, Substitute.For<ILogger<WorkPutHandler>>());

        using (ExecutionPrincipal.Begin(Guid.Parse(user.Id)))
        {
            var result = await handler.Handle(new PutCommand<WorkResource>(resource), CancellationToken.None);
            _context.ChangeTracker.Clear();
            return result;
        }
    }

    private async Task<BulkWorksResponse> BulkAsAsync(AppUser user, Guid? analyseToken, params Guid[] shiftIds)
    {
        var completionService = Substitute.For<IScheduleCompletionService>();
        completionService.SaveBulkAndTrackRangeAsync(
                Arg.Any<List<(Guid ClientId, DateOnly CurrentDate, Guid? AnalyseToken)>>(),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(async _ => { await _context.SaveChangesAsync(); });
        var conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([]));
        var handler = new BulkAddWorksCommandHandler(
            _workRepository, AllClientsVisible(), _scheduleMapper, Substitute.For<IPeriodHoursService>(), completionService,
            Substitute.For<IWorkNotificationFacade>(), Substitute.For<IContainerWorkExpansionService>(),
            Substitute.For<IOvertimeCascadeService>(), Substitute.For<IDayLockService>(), conflictChecker,
            Substitute.For<IShiftDefaultExpensesApplier>(), Substitute.For<IClientPlanningPeriodGuard>(), _shiftGuard,
            Substitute.For<ILogger<BulkAddWorksCommandHandler>>());
        var request = new BulkAddWorksRequest
        {
            PeriodStart = Day,
            PeriodEnd = Day,
            Works = shiftIds.Select(shiftId => new BulkWorkItem
            {
                ClientId = _client.Id,
                ShiftId = shiftId,
                CurrentDate = Day,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(16, 0),
                WorkTime = 8m,
                AnalyseToken = analyseToken
            }).ToList()
        };

        using (ExecutionPrincipal.Begin(Guid.Parse(user.Id)))
        {
            var response = await handler.Handle(new BulkAddWorksCommand(request), CancellationToken.None);
            _context.ChangeTracker.Clear();
            return response;
        }
    }

    private WorkResource ResourceFor(Guid workId, Guid shiftId) => new()
    {
        Id = workId,
        ClientId = _client.Id,
        ShiftId = shiftId,
        CurrentDate = Day,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        WorkTime = 8m,
        Information = workId == Guid.Empty ? null : ChangedInformation
    };

    private async Task<Work> SeedWorkAsync(Guid shiftId)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _client.Id,
            ShiftId = shiftId,
            CurrentDate = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return work;
    }

    private Task<List<Work>> StoredWorksAsync() =>
        _context.Work.AsNoTracking().Where(w => w.ClientId == _client.Id && !w.IsDeleted).ToListAsync();

    private static IClientVisibilityGuard AllClientsVisible()
    {
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    private async Task PopulateAsync()
    {
        var visibleRoot = NewGroup("VISIBLE_ROOT");
        var hiddenRoot = NewGroup("HIDDEN_ROOT");
        _visibleRootId = visibleRoot.Id;
        _hiddenRootId = hiddenRoot.Id;
        _supervisor = NewUser("SUPERVISOR");
        _admin = NewUser("ADMIN");

        _context.Group.AddRange(visibleRoot, hiddenRoot);
        _context.AppUser.AddRange(_supervisor, _admin);
        await _context.SaveChangesAsync();

        _context.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = _supervisor.Id, GroupId = _visibleRootId });

        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = Prefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false,
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidUntil = null
            }
        };
        _context.Set<Client>().Add(_client);

        _visibleShift = NewShift("VISIBLE", _visibleRootId);
        _hiddenShift = NewShift("HIDDEN", _hiddenRootId);
        _scenarioClone = NewShift("VISIBLE_CLONE", _visibleRootId);
        _scenarioClone.AnalyseToken = Guid.NewGuid();
        _context.Shift.AddRange(_visibleShift, _hiddenShift, _scenarioClone);
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
        var groupFilter = new ShiftGroupFilterService(groupVisibility, userService);
        var groupGuard = new GroupVisibilityGuard(groupVisibility, userService);
        _scheduleMapper = new ScheduleMapper();
        _unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        _workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IClientContractDataProvider>());
        _shiftGuard = new ShiftVisibilityGuard(
            groupFilter, new ShiftVisibilityReadRepository(_context), groupGuard, Substitute.For<ILogger<ShiftVisibilityGuard>>());
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
        foreach (var user in new[] { _supervisor, _admin })
        {
            userManager.FindByIdAsync(user.Id).Returns(user);
            IList<string> roles = user == _admin
                ? new List<string> { Roles.Admin }
                : new List<string> { Roles.Authorised };
            userManager.GetRolesAsync(user).Returns(Task.FromResult(roles));
            userManager.IsInRoleAsync(user, Roles.Admin).Returns(user == _admin);
        }

        return userManager;
    }

    private static Group NewGroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Description = "Work shift visibility integration test",
        ValidFrom = DateTime.UtcNow.AddYears(-1)
    };

    private static Shift NewShift(string suffix, params Guid[] groupIds)
    {
        var shiftId = Guid.NewGuid();
        return new Shift
        {
            Id = shiftId,
            Name = $"{Prefix}{suffix}",
            Abbreviation = "WSV",
            Description = "Work shift visibility integration test",
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
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM group_visibility WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_visibility WHERE app_user_id IN (SELECT id FROM ""AspNetUsers"" WHERE starts_with(email, '{Prefix}'));
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM client WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""AspNetUsers"" WHERE starts_with(email, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
