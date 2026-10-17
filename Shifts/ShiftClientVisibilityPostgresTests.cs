// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end customer reference of the shift POST and PUT handlers against PostgreSQL (IT database klacks) with the
/// real UserService, GroupVisibilityService, ClientGroupFilterService, ClientSearchRepository, ClientVisibilityGuard,
/// ClientRepository and ClientReferenceGuard. A Supervisor sees one group; a customer in a hidden group, an unknown id
/// and a soft-deleted customer must all be refused with the same 404, nothing written and no customer data in the
/// answer. A shift whose stored customer is hidden stays saveable as long as the customer is not changed; an admin
/// keeps saving valid customers. Every row carries the INTEGRATION_TEST_SHIFTCLIENT_ prefix and the cleanup deletes
/// exactly those rows.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using ShiftPostHandler = Klacks.Api.Application.Handlers.Shifts.PostCommandHandler;
using ShiftPutHandler = Klacks.Api.Application.Handlers.Shifts.PutCommandHandler;

namespace Klacks.IntegrationTest.Shifts;

[TestFixture]
[Category("RealDatabase")]
public class ShiftClientVisibilityPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_SHIFTCLIENT_";
    private const string Renamed = Prefix + "RENAMED";
    private const string HiddenStreet = Prefix + "HIDDEN_STREET";
    private const string IdPlaceholder = "<id>";

    private DataBaseContext _context = null!;
    private ScheduleMapper _scheduleMapper = null!;
    private IShiftRepository _shiftRepository = null!;
    private ShiftRequiredQualificationRepository _requirementRepository = null!;
    private UnitOfWork _unitOfWork = null!;
    private ShiftVisibilityGuard _shiftGuard = null!;
    private ClientReferenceGuard _clientReferenceGuard = null!;

    private AppUser _supervisor = null!;
    private AppUser _admin = null!;
    private Guid _visibleGroupId;
    private Guid _hiddenGroupId;
    private Client _visibleClient = null!;
    private Client _secondVisibleClient = null!;
    private Client _hiddenClient = null!;
    private Client _deletedClient = null!;
    private Shift _ownShift = null!;
    private Shift _hiddenCustomerShift = null!;

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
    public async Task Post_Supervisor_HiddenMissingAndDeletedCustomer_AreTheSame404_AndNothingIsWritten()
    {
        var missingId = Guid.NewGuid();

        var hidden = await CapturePostAsync(_supervisor, "NEW_HIDDEN", _hiddenClient.Id);
        var missing = await CapturePostAsync(_supervisor, "NEW_MISSING", missingId);
        var deleted = await CapturePostAsync(_supervisor, "NEW_DELETED", _deletedClient.Id);

        hidden.ShouldBeOfType<KeyNotFoundException>();
        missing.ShouldBeOfType<KeyNotFoundException>();
        deleted.ShouldBeOfType<KeyNotFoundException>();
        var expected = missing.Message.Replace(missingId.ToString(), IdPlaceholder);
        hidden.Message.Replace(_hiddenClient.Id.ToString(), IdPlaceholder).ShouldBe(expected);
        deleted.Message.Replace(_deletedClient.Id.ToString(), IdPlaceholder).ShouldBe(expected);
        hidden.Message.ShouldNotContain(_hiddenClient.Name!);
        hidden.Message.ShouldNotContain(HiddenStreet);
        _context.ChangeTracker.Clear();
        (await _context.Shift.AnyAsync(s => s.Name.StartsWith(Prefix + "NEW_"))).ShouldBeFalse();
    }

    [Test]
    public async Task Post_Supervisor_VisibleCustomer_IsCreated()
    {
        var result = await PostAsAsync(_supervisor, "NEW_VISIBLE", _visibleClient.Id);

        result.ShouldNotBeNull();
        _context.ChangeTracker.Clear();
        (await _context.Shift.SingleAsync(s => s.Name == Prefix + "NEW_VISIBLE")).ClientId.ShouldBe(_visibleClient.Id);
    }

    [Test]
    public async Task Post_Admin_UnknownCustomer_Is404_InsteadOfAForeignKeyError()
    {
        var unknownId = Guid.NewGuid();

        var error = await CapturePostAsync(_admin, "NEW_ADMIN_UNKNOWN", unknownId);

        error.ShouldBeOfType<KeyNotFoundException>();
        error.Message.ShouldBe(string.Format(ClientVisibilityMessages.ClientNotFound, unknownId));
        _context.ChangeTracker.Clear();
        (await _context.Shift.AnyAsync(s => s.Name == Prefix + "NEW_ADMIN_UNKNOWN")).ShouldBeFalse();
    }

    [Test]
    public async Task Post_Admin_CustomerOfAnyGroup_IsCreated()
    {
        var result = await PostAsAsync(_admin, "NEW_ADMIN_HIDDEN", _hiddenClient.Id);

        result.ShouldNotBeNull();
        _context.ChangeTracker.Clear();
        (await _context.Shift.SingleAsync(s => s.Name == Prefix + "NEW_ADMIN_HIDDEN")).ClientId.ShouldBe(_hiddenClient.Id);
    }

    [Test]
    public async Task Put_Supervisor_ChangeToHiddenMissingOrDeletedCustomer_IsTheSame404_AndRowStaysUnchanged()
    {
        var missingId = Guid.NewGuid();

        var hidden = await CapturePutAsync(_supervisor, _ownShift.Id, _hiddenClient.Id);
        var missing = await CapturePutAsync(_supervisor, _ownShift.Id, missingId);
        var deleted = await CapturePutAsync(_supervisor, _ownShift.Id, _deletedClient.Id);

        hidden.ShouldBeOfType<KeyNotFoundException>();
        missing.ShouldBeOfType<KeyNotFoundException>();
        deleted.ShouldBeOfType<KeyNotFoundException>();
        var expected = missing.Message.Replace(missingId.ToString(), IdPlaceholder);
        hidden.Message.Replace(_hiddenClient.Id.ToString(), IdPlaceholder).ShouldBe(expected);
        deleted.Message.Replace(_deletedClient.Id.ToString(), IdPlaceholder).ShouldBe(expected);
        hidden.Message.ShouldNotContain(_hiddenClient.Name!);
        var stored = await ReloadAsync(_ownShift.Id);
        stored.Name.ShouldBe(_ownShift.Name);
        stored.ClientId.ShouldBe(_visibleClient.Id);
    }

    [Test]
    public async Task Put_Supervisor_UnchangedHiddenCustomer_IsSaved()
    {
        var result = await PutAsAsync(_supervisor, _hiddenCustomerShift.Id, _hiddenClient.Id);

        result.ShouldNotBeNull();
        var stored = await ReloadAsync(_hiddenCustomerShift.Id);
        stored.Name.ShouldBe(Renamed);
        stored.ClientId.ShouldBe(_hiddenClient.Id);
    }

    [Test]
    public async Task Put_Supervisor_ChangeToOtherVisibleCustomer_IsSaved()
    {
        var result = await PutAsAsync(_supervisor, _ownShift.Id, _secondVisibleClient.Id);

        result.ShouldNotBeNull();
        (await ReloadAsync(_ownShift.Id)).ClientId.ShouldBe(_secondVisibleClient.Id);
    }

    [Test]
    public async Task Put_Admin_ChangeToCustomerOfAnyGroup_IsSaved_UnknownIs404()
    {
        var unknownId = Guid.NewGuid();

        var result = await PutAsAsync(_admin, _ownShift.Id, _hiddenClient.Id);
        var unknown = await CapturePutAsync(_admin, _hiddenCustomerShift.Id, unknownId);

        result.ShouldNotBeNull();
        (await ReloadAsync(_ownShift.Id)).ClientId.ShouldBe(_hiddenClient.Id);
        unknown.ShouldBeOfType<KeyNotFoundException>();
        unknown.Message.ShouldBe(string.Format(ClientVisibilityMessages.ClientNotFound, unknownId));
        (await ReloadAsync(_hiddenCustomerShift.Id)).Name.ShouldBe(_hiddenCustomerShift.Name);
    }

    private async Task<ShiftResource?> PostAsAsync(AppUser caller, string suffix, Guid clientId)
    {
        var handler = new ShiftPostHandler(
            _shiftRepository, _scheduleMapper, _unitOfWork, Substitute.For<IDefaultShiftMacroResolver>(),
            Substitute.For<IOrderSealingService>(), _shiftGuard, _clientReferenceGuard,
            Substitute.For<ILogger<ShiftPostHandler>>());
        var shift = NewShift(suffix, clientId, _visibleGroupId);
        try
        {
            using (ExecutionPrincipal.Begin(Guid.Parse(caller.Id)))
            {
                return await handler.Handle(
                    new PostCommand<ShiftResource>(_scheduleMapper.ToShiftResource(shift)), CancellationToken.None);
            }
        }
        finally
        {
            _context.ChangeTracker.Clear();
        }
    }

    private async Task<Exception?> CapturePostAsync(AppUser caller, string suffix, Guid clientId) =>
        await CaptureAsync(() => PostAsAsync(caller, suffix, clientId));

    private async Task<ShiftResource?> PutAsAsync(AppUser caller, Guid shiftId, Guid clientId)
    {
        var shift = await _shiftRepository.Get(shiftId);
        shift.ShouldNotBeNull();
        var resource = _scheduleMapper.ToShiftResource(shift);
        resource.Name = Renamed;
        resource.ClientId = clientId;
        resource.Client = null;
        _context.ChangeTracker.Clear();

        var handler = new ShiftPutHandler(
            _shiftRepository, _requirementRepository, _scheduleMapper, _unitOfWork, _shiftGuard, _clientReferenceGuard,
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

    private async Task<Exception?> CapturePutAsync(AppUser caller, Guid shiftId, Guid clientId) =>
        await CaptureAsync(() => PutAsAsync(caller, shiftId, clientId));

    private async Task<Shift> ReloadAsync(Guid shiftId)
    {
        _context.ChangeTracker.Clear();
        return await _context.Shift.AsNoTracking().FirstAsync(s => s.Id == shiftId);
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
        var visibleGroup = NewGroup("VISIBLE");
        var hiddenGroup = NewGroup("HIDDEN");
        _visibleGroupId = visibleGroup.Id;
        _hiddenGroupId = hiddenGroup.Id;
        _supervisor = NewUser("SUPERVISOR");
        _admin = NewUser("ADMIN");
        _context.Group.AddRange(visibleGroup, hiddenGroup);
        _context.AppUser.AddRange(_supervisor, _admin);
        await _context.SaveChangesAsync();
        _context.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = _supervisor.Id, GroupId = _visibleGroupId });

        _visibleClient = NewClient("VISIBLE_CLIENT");
        _secondVisibleClient = NewClient("SECOND_VISIBLE_CLIENT");
        _hiddenClient = NewClient("HIDDEN_CLIENT");
        _hiddenClient.Addresses.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = _hiddenClient.Id,
            Street = HiddenStreet,
            City = Prefix + "CITY",
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        _deletedClient = NewClient("DELETED_CLIENT");
        _context.Client.AddRange(_visibleClient, _secondVisibleClient, _hiddenClient, _deletedClient);
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), GroupId = _visibleGroupId, ClientId = _visibleClient.Id },
            new GroupItem { Id = Guid.NewGuid(), GroupId = _visibleGroupId, ClientId = _secondVisibleClient.Id },
            new GroupItem { Id = Guid.NewGuid(), GroupId = _hiddenGroupId, ClientId = _hiddenClient.Id },
            new GroupItem { Id = Guid.NewGuid(), GroupId = _visibleGroupId, ClientId = _deletedClient.Id });

        _ownShift = NewShift("OWN", _visibleClient.Id, _visibleGroupId);
        _hiddenCustomerShift = NewShift("HIDDEN_CUSTOMER", _hiddenClient.Id, _visibleGroupId);
        _context.Shift.AddRange(_ownShift, _hiddenCustomerShift);
        await _context.SaveChangesAsync();

        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE client SET is_deleted = true WHERE id = {_deletedClient.Id}");
        _context.ChangeTracker.Clear();
    }

    private void BuildPipeline()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var userManager = BuildUserManager();
        var userService = new UserService(accessor, userManager, Substitute.For<ILogger<UserService>>());
        var groupVisibility = new GroupVisibilityService(_context, userManager, userService);
        var groupGuard = new GroupVisibilityGuard(groupVisibility, userService);
        var groupClient = new GroupClientService(
            _context, new MemoryCache(new MemoryCacheOptions()), Substitute.For<ILogger<GroupClientService>>());
        var clientFilter = new ClientGroupFilterService(
            groupClient, groupVisibility, userService, Substitute.For<ILogger<ClientGroupFilterService>>());
        var clientSearch = new ClientSearchRepository(
            _context, clientFilter, Substitute.For<IClientFuzzySearchService>(), Substitute.For<ICompanyClock>());
        var clientValidator = new ClientValidator();
        var clientRepository = new ClientRepository(
            _context,
            new MacroEngine(),
            new ClientChangeTrackingService(_context, new ClientSortingService()),
            new ClientEntityManagementService(clientValidator),
            new EntityCollectionUpdateService(_context),
            clientValidator,
            Substitute.For<ILogger<ClientRepository>>());
        _clientReferenceGuard = new ClientReferenceGuard(new ClientVisibilityGuard(clientSearch, clientFilter), clientRepository);

        _scheduleMapper = new ScheduleMapper();
        _unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
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
            new ShiftValidator(),
            _scheduleMapper,
            TestCompanyClock.Utc());
        _requirementRepository = new ShiftRequiredQualificationRepository(
            _context, Substitute.For<ILogger<ShiftRequiredQualification>>());
        _shiftGuard = new ShiftVisibilityGuard(
            new ShiftGroupFilterService(groupVisibility, userService),
            new ShiftVisibilityReadRepository(_context),
            groupGuard,
            Substitute.For<ILogger<ShiftVisibilityGuard>>());
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
        Description = "Shift client visibility integration test",
        ValidFrom = DateTime.UtcNow.AddYears(-1)
    };

    private static Client NewClient(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        FirstName = "Test",
        Company = string.Empty,
        LegalEntity = false,
        Type = EntityTypeEnum.Customer,
        Membership = new Membership
        {
            Id = Guid.NewGuid(),
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = null
        }
    };

    private static Shift NewShift(string suffix, Guid clientId, Guid groupId)
    {
        var shiftId = Guid.NewGuid();
        return new Shift
        {
            Id = shiftId,
            Name = Prefix + suffix,
            Abbreviation = "SCV",
            Description = "Shift client visibility integration test",
            Status = ShiftStatus.OriginalOrder,
            ShiftType = ShiftType.IsTask,
            ClientId = clientId,
            FromDate = new DateOnly(2026, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 8m,
            IsMonday = true,
            Quantity = 1,
            SumEmployees = 1,
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = shiftId }]
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
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_visibility WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_visibility WHERE app_user_id IN (SELECT id FROM ""AspNetUsers"" WHERE starts_with(email, '{Prefix}'));
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
            DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM client WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""AspNetUsers"" WHERE starts_with(email, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
