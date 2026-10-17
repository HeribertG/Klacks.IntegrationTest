// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end group visibility of the shift WRITE paths against PostgreSQL (IT database klacks) with the real
/// UserService, GroupVisibilityService, ShiftGroupFilterService, GroupVisibilityGuard and ShiftVisibilityGuard:
/// PUT/POST Shifts, Cuts/Batch, RequiredQualifications and linking a shift into a group. Callers: a Supervisor who sees
/// one root group (and its child), a non-admin without any visible group, and an admin. A hidden shift must be answered
/// exactly like a missing one and stay unchanged in the database. Every row carries the INTEGRATION_TEST_SHIFTWRITE_
/// prefix and the cleanup deletes exactly those rows.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Qualifications;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
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
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using GroupItemPostHandler = Klacks.Api.Application.Handlers.GroupItems.PostCommandHandler;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using ShiftPostHandler = Klacks.Api.Application.Handlers.Shifts.PostCommandHandler;
using ShiftPutHandler = Klacks.Api.Application.Handlers.Shifts.PutCommandHandler;

namespace Klacks.IntegrationTest.Shifts;

[TestFixture]
[Category("RealDatabase")]
public class ShiftWriteVisibilityPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_SHIFTWRITE_";
    private const string Renamed = Prefix + "RENAMED";
    private const string IdPlaceholder = "<id>";

    private DataBaseContext _context = null!;
    private ScheduleMapper _scheduleMapper = null!;
    private IShiftRepository _shiftRepository = null!;
    private ShiftRequiredQualificationRepository _requirementRepository = null!;
    private UnitOfWork _unitOfWork = null!;
    private ShiftVisibilityGuard _shiftGuard = null!;
    private GroupVisibilityGuard _groupGuard = null!;
    private ShiftCutFacade _cutFacade = null!;

    private AppUser _supervisor = null!;
    private AppUser _noGroupUser = null!;
    private AppUser _admin = null!;
    private Guid _visibleRootId;
    private Guid _visibleChildId;
    private Guid _hiddenRootId;

    private Shift _ownShift = null!;
    private Shift _hiddenShift = null!;
    private Shift _mixedShift = null!;
    private Shift _groupLessShift = null!;

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
    public async Task Put_Supervisor_HiddenShift_IsAnsweredLikeMissing_AndRowStaysUnchanged()
    {
        var hiddenResource = await ResourceWithAsync(_hiddenShift.Id, Renamed, _visibleRootId);
        var missingResource = await ResourceWithAsync(_ownShift.Id, Renamed, _visibleRootId);
        missingResource.Id = Guid.NewGuid();

        var hidden = await PutAsAsync(_supervisor, hiddenResource);
        var missing = await PutAsAsync(_supervisor, missingResource);

        hidden.ShouldBeNull();
        missing.ShouldBeNull();
        var stored = await ReloadAsync(_hiddenShift.Id);
        stored.Name.ShouldBe(_hiddenShift.Name);
        stored.GroupItems.Where(gi => !gi.IsDeleted).Select(gi => gi.GroupId).ShouldBe([_hiddenRootId]);
    }

    [Test]
    public async Task Put_Supervisor_OwnShift_IsSaved()
    {
        var result = await PutAsAsync(_supervisor, await ResourceWithAsync(_ownShift.Id, Renamed, _visibleChildId));

        result.ShouldNotBeNull();
        (await ReloadAsync(_ownShift.Id)).Name.ShouldBe(Renamed);
    }

    [Test]
    public async Task Put_Supervisor_MixedShift_UnchangedHiddenAssignment_IsSaved()
    {
        var result = await PutAsAsync(_supervisor, await ResourceWithAsync(_mixedShift.Id, Renamed, _hiddenRootId, _visibleRootId));

        result.ShouldNotBeNull();
        var stored = await ReloadAsync(_mixedShift.Id);
        stored.Name.ShouldBe(Renamed);
        stored.GroupItems.Where(gi => !gi.IsDeleted).Select(gi => gi.GroupId)
            .ShouldBe([_hiddenRootId, _visibleRootId], ignoreOrder: true);
    }

    [Test]
    public async Task Put_Supervisor_AddingHiddenGroupToOwnShift_IsRefusedLikeMissingGroup_AndRowStaysUnchanged()
    {
        var resource = await ResourceWithAsync(_ownShift.Id, Renamed, _visibleChildId, _hiddenRootId);

        var error = await Should.ThrowAsync<KeyNotFoundException>(() => PutAsAsync(_supervisor, resource));

        error.Message.ShouldBe(string.Format(ShiftVisibilityMessages.GroupNotFound, _hiddenRootId));
        var stored = await ReloadAsync(_ownShift.Id);
        stored.Name.ShouldBe(_ownShift.Name);
        stored.GroupItems.Where(gi => !gi.IsDeleted).Select(gi => gi.GroupId).ShouldBe([_visibleChildId]);
    }

    [Test]
    public async Task Put_Supervisor_RemovingHiddenGroupFromMixedShift_IsRefused()
    {
        var resource = await ResourceWithAsync(_mixedShift.Id, Renamed, _visibleRootId);

        await Should.ThrowAsync<KeyNotFoundException>(() => PutAsAsync(_supervisor, resource));

        (await ReloadAsync(_mixedShift.Id)).GroupItems.Count(gi => !gi.IsDeleted).ShouldBe(2);
    }

    [Test]
    public async Task Put_UserWithoutVisibleGroup_MayWriteGroupLessShift_ButNotGroupedOne()
    {
        var groupLess = await PutAsAsync(_noGroupUser, await ResourceWithAsync(_groupLessShift.Id, Renamed));
        var grouped = await PutAsAsync(_noGroupUser, await ResourceWithAsync(_ownShift.Id, Renamed, _visibleChildId));

        groupLess.ShouldNotBeNull();
        grouped.ShouldBeNull();
        (await ReloadAsync(_groupLessShift.Id)).Name.ShouldBe(Renamed);
        (await ReloadAsync(_ownShift.Id)).Name.ShouldBe(_ownShift.Name);
    }

    [Test]
    public async Task Put_Admin_HiddenShift_IsSaved()
    {
        var result = await PutAsAsync(_admin, await ResourceWithAsync(_hiddenShift.Id, Renamed, _hiddenRootId));

        result.ShouldNotBeNull();
        (await ReloadAsync(_hiddenShift.Id)).Name.ShouldBe(Renamed);
    }

    [Test]
    public async Task Post_Supervisor_HiddenGroup_IsRefused_VisibleGroup_IsCreated()
    {
        var hiddenNew = NewShift("NEW_HIDDEN", _hiddenRootId);
        var visibleNew = NewShift("NEW_VISIBLE", _visibleChildId);
        var handler = new ShiftPostHandler(
            _shiftRepository, _scheduleMapper, _unitOfWork, Substitute.For<IDefaultShiftMacroResolver>(),
            Substitute.For<IOrderSealingService>(), _shiftGuard, Substitute.For<ILogger<ShiftPostHandler>>());

        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
                new PostCommand<ShiftResource>(_scheduleMapper.ToShiftResource(hiddenNew)), CancellationToken.None));
            (await handler.Handle(
                new PostCommand<ShiftResource>(_scheduleMapper.ToShiftResource(visibleNew)), CancellationToken.None))
                .ShouldNotBeNull();
        }

        _context.ChangeTracker.Clear();
        (await _context.Shift.AnyAsync(s => s.Name == hiddenNew.Name)).ShouldBeFalse();
        (await _context.Shift.AnyAsync(s => s.Name == visibleNew.Name)).ShouldBeTrue();
    }

    [Test]
    public async Task CutsBatch_Supervisor_HiddenParent_IsAnsweredLikeMissing_AndNoPieceIsWritten()
    {
        var hiddenPiece = PieceOf(_hiddenShift, _visibleRootId);
        var missingParentId = Guid.NewGuid();
        var missingPiece = PieceOf(_hiddenShift, _visibleRootId);

        Exception? hiddenError;
        Exception? missingError;
        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            hiddenError = await CaptureAsync(() => _cutFacade.ProcessBatchCutsAsync(
                [new CutOperation { Type = "CREATE", ParentId = _hiddenShift.Id.ToString(), Data = hiddenPiece }]));
            missingError = await CaptureAsync(() => _cutFacade.ProcessBatchCutsAsync(
                [new CutOperation { Type = "CREATE", ParentId = missingParentId.ToString(), Data = missingPiece }]));
        }

        hiddenError.ShouldBeOfType<KeyNotFoundException>();
        missingError.ShouldBeOfType<KeyNotFoundException>();
        hiddenError.Message.Replace(_hiddenShift.Id.ToString(), IdPlaceholder)
            .ShouldBe(missingError.Message.Replace(missingParentId.ToString(), IdPlaceholder));
        _context.ChangeTracker.Clear();
        (await _context.Shift.AnyAsync(s => s.Id == hiddenPiece.Id)).ShouldBeFalse();
    }

    [Test]
    public async Task CutsBatch_Supervisor_UpdateOfHiddenShift_IsAnsweredLikeMissing()
    {
        var update = await ResourceWithAsync(_hiddenShift.Id, Renamed, _hiddenRootId);

        KeyNotFoundException error;
        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            error = await Should.ThrowAsync<KeyNotFoundException>(() => _cutFacade.ProcessBatchCutsAsync(
                [new CutOperation { Type = "UPDATE", ParentId = string.Empty, Data = update }]));
        }

        error.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenShift.Id));
        (await ReloadAsync(_hiddenShift.Id)).Name.ShouldBe(_hiddenShift.Name);
    }

    [Test]
    public async Task CutsBatch_Supervisor_RepeatedUpdateAddingHiddenGroup_IsRefused_AndRowStaysUnchanged()
    {
        var unchanged = await ResourceWithAsync(_ownShift.Id, Renamed, _visibleChildId);
        var withHiddenGroup = await ResourceWithAsync(_ownShift.Id, Renamed, _visibleChildId, _hiddenRootId);

        KeyNotFoundException error;
        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            error = await Should.ThrowAsync<KeyNotFoundException>(() => _cutFacade.ProcessBatchCutsAsync(
            [
                new CutOperation { Type = "UPDATE", ParentId = string.Empty, Data = unchanged },
                new CutOperation { Type = "UPDATE", ParentId = string.Empty, Data = withHiddenGroup }
            ]));
        }

        error.Message.ShouldBe(string.Format(ShiftVisibilityMessages.GroupNotFound, _hiddenRootId));
        var stored = await ReloadAsync(_ownShift.Id);
        stored.Name.ShouldBe(_ownShift.Name);
        stored.GroupItems.Where(gi => !gi.IsDeleted).Select(gi => gi.GroupId).ShouldBe([_visibleChildId]);
    }

    [Test]
    public async Task RequiredQualification_Supervisor_HiddenShift_IsAnsweredLikeMissing()
    {
        var handler = new SetShiftRequiredQualificationCommandHandler(_requirementRepository, _unitOfWork, _shiftGuard);
        var missingShiftId = Guid.NewGuid();

        Exception? hiddenError;
        Exception? missingError;
        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            hiddenError = await CaptureAsync(() => handler.Handle(
                new SetShiftRequiredQualificationCommand(_hiddenShift.Id, Guid.NewGuid(), true, QualificationLevel.Advanced, null),
                CancellationToken.None));
            missingError = await CaptureAsync(() => handler.Handle(
                new SetShiftRequiredQualificationCommand(missingShiftId, Guid.NewGuid(), true, QualificationLevel.Advanced, null),
                CancellationToken.None));
        }

        hiddenError.ShouldBeOfType<KeyNotFoundException>();
        missingError.ShouldBeOfType<KeyNotFoundException>();
        hiddenError.Message.Replace(_hiddenShift.Id.ToString(), IdPlaceholder)
            .ShouldBe(missingError.Message.Replace(missingShiftId.ToString(), IdPlaceholder));
        _context.ChangeTracker.Clear();
        (await _context.ShiftRequiredQualification.AnyAsync(r => r.ShiftId == _hiddenShift.Id)).ShouldBeFalse();
    }

    [Test]
    public async Task LinkHiddenShiftIntoOwnGroup_Supervisor_IsRefused_AndNoLinkIsWritten()
    {
        var handler = new GroupItemPostHandler(
            new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>()),
            new GroupMapper(),
            _unitOfWork,
            _groupGuard,
            Substitute.For<IClientVisibilityGuard>(),
            _shiftGuard,
            Substitute.For<ILogger<GroupItemPostHandler>>());
        var link = new GroupItemResource
        {
            Id = Guid.NewGuid(),
            GroupId = _visibleRootId,
            ShiftId = _hiddenShift.Id,
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        using (ExecutionPrincipal.Begin(Guid.Parse(_supervisor.Id)))
        {
            await Should.ThrowAsync<KeyNotFoundException>(
                () => handler.Handle(new PostCommand<GroupItemResource>(link), CancellationToken.None));
        }

        _context.ChangeTracker.Clear();
        (await _context.GroupItem.AnyAsync(gi => gi.ShiftId == _hiddenShift.Id && gi.GroupId == _visibleRootId)).ShouldBeFalse();
    }

    private async Task<ShiftResource?> PutAsAsync(AppUser caller, ShiftResource resource)
    {
        var handler = new ShiftPutHandler(
            _shiftRepository, _requirementRepository, _scheduleMapper, _unitOfWork, _shiftGuard,
            Substitute.For<ILogger<ShiftPutHandler>>());
        try
        {
            using (ExecutionPrincipal.Begin(Guid.Parse(caller.Id)))
            {
                return await handler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);
            }
        }
        finally
        {
            _context.ChangeTracker.Clear();
        }
    }

    private async Task<ShiftResource> ResourceWithAsync(Guid shiftId, string name, params Guid[] groupIds)
    {
        var shift = await _shiftRepository.Get(shiftId);
        shift.ShouldNotBeNull();
        var resource = _scheduleMapper.ToShiftResource(shift);
        resource.Name = name;
        resource.Groups = groupIds.Select(id => new SimpleGroupResource { Id = id }).ToList();
        _context.ChangeTracker.Clear();
        return resource;
    }

    private ShiftResource PieceOf(Shift parent, params Guid[] groupIds)
    {
        var piece = NewShift("PIECE", groupIds);
        piece.Status = ShiftStatus.SplitShift;
        piece.FromDate = parent.FromDate;
        return _scheduleMapper.ToShiftResource(piece);
    }

    private async Task<Shift> ReloadAsync(Guid shiftId)
    {
        _context.ChangeTracker.Clear();
        var shift = await _context.Shift.AsNoTracking().Include(s => s.GroupItems).FirstAsync(s => s.Id == shiftId);
        return shift;
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

        _ownShift = NewShift("OWN", _visibleChildId);
        _hiddenShift = NewShift("HIDDEN", _hiddenRootId);
        _mixedShift = NewShift("MIXED", _hiddenRootId, _visibleRootId);
        _groupLessShift = NewShift("GROUPLESS");
        foreach (var draft in new[] { _ownShift, _hiddenShift, _mixedShift, _groupLessShift })
        {
            draft.Status = ShiftStatus.OriginalOrder;
        }

        _context.Shift.AddRange(_ownShift, _hiddenShift, _mixedShift, _groupLessShift);
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
        _groupGuard = new GroupVisibilityGuard(groupVisibility, userService);
        _scheduleMapper = new ScheduleMapper();
        _unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());

        var shiftValidator = new ShiftValidator();
        _shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            new ShiftQueryPipelineService(
                new DateRangeFilterService(),
                new ShiftSearchService(),
                new ShiftSortingService(),
                new ShiftStatusFilterService(),
                new ShiftPaginationService()),
            new ShiftGroupManagementService(_context, Substitute.For<ILogger<ShiftGroupManagementService>>()),
            new EntityCollectionUpdateService(_context),
            shiftValidator,
            _scheduleMapper,
            TestCompanyClock.Utc());
        _requirementRepository = new ShiftRequiredQualificationRepository(
            _context, Substitute.For<ILogger<ShiftRequiredQualification>>());
        _shiftGuard = new ShiftVisibilityGuard(
            groupFilter, new ShiftVisibilityReadRepository(_context), _groupGuard, Substitute.For<ILogger<ShiftVisibilityGuard>>());
        _cutFacade = new ShiftCutFacade(
            _shiftRepository,
            _requirementRepository,
            new ShiftTreeService(_context, Substitute.For<ILogger<ShiftTreeService>>()),
            new ShiftResetService(_shiftRepository, Substitute.For<ILogger<ShiftResetService>>()),
            shiftValidator,
            _scheduleMapper,
            _unitOfWork,
            _shiftGuard,
            Substitute.For<ILogger<ShiftCutFacade>>());
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
            IList<string> roles = user == _admin
                ? new List<string> { Roles.Admin }
                : new List<string> { Roles.Authorised };
            userManager.GetRolesAsync(user).Returns(Task.FromResult(roles));
            userManager.IsInRoleAsync(user, Roles.Admin).Returns(user == _admin);
        }

        return userManager;
    }

    private static Group NewGroup(string suffix, Guid? parent) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Description = "Shift write visibility integration test",
        ValidFrom = DateTime.UtcNow.AddYears(-1),
        Parent = parent,
        Root = parent
    };

    private static Shift NewShift(string suffix, params Guid[] groupIds)
    {
        var shiftId = Guid.NewGuid();
        return new Shift
        {
            Id = shiftId,
            Name = $"{Prefix}{suffix}",
            Abbreviation = "SWV",
            Description = "Shift write visibility integration test",
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
            DELETE FROM shift_required_qualification WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
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
