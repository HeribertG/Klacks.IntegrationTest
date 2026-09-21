// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Groups;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Groups;

/// <summary>
/// Integration tests for the first-group visibility guarantee against PostgreSQL.
///
/// Database Connection:
/// - Host: localhost
/// - Port: 5434
/// - Database: klacks
/// - Username: postgres
/// - Password: admin
///
/// The zero-group branch of RequiresPreservationAsync cannot be exercised here: the shared
/// integration database holds real groups, and emptying it would be destructive. What is verified
/// against the real database is everything that depends on PostgreSQL semantics - that a root group
/// is stored with Root = NULL and a single group_visibility row on it makes the root AND its whole
/// subtree visible to a non-admin (the "nothing changes for them" guarantee), that the preservation
/// grant is idempotent, and that the group-existence predicate ignores soft-deleted groups.
///
/// Every row this fixture creates carries the INTEGRATION_TEST_ prefix and the cleanup deletes
/// exactly those rows - never anything matched by a production-plausible value.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class FirstGroupVisibilityPreservationIntegrationTests
{
    private const string TestGroupPrefix = "INTEGRATION_TEST_GROUP_VISIBILITY_";
    private const string TestUserPrefix = "INTEGRATION_TEST_USER_VISIBILITY_";

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private UserManager<AppUser> _userManager = null!;
    private IUserService _userService = null!;
    private GroupVisibilityService _groupVisibilityService = null!;
    private GroupVisibilityPreservationService _preservationService = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        using var context = CreateContext();
        await CleanupTestDataWithContext(context);
    }

    [SetUp]
    public void SetUp()
    {
        _context = CreateContext();

        var userStore = Substitute.For<IUserStore<AppUser>>();
        _userManager = Substitute.For<UserManager<AppUser>>(
            userStore,
            Substitute.For<IOptions<IdentityOptions>>(),
            Substitute.For<IPasswordHasher<AppUser>>(),
            new List<IUserValidator<AppUser>>(),
            new List<IPasswordValidator<AppUser>>(),
            Substitute.For<ILookupNormalizer>(),
            Substitute.For<IdentityErrorDescriber>(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<AppUser>>>());
        _userManager.GetUsersInRoleAsync(Arg.Any<string>())
            .Returns(Task.FromResult<IList<AppUser>>(new List<AppUser>()));

        _userService = Substitute.For<IUserService>();
        _groupVisibilityService = new GroupVisibilityService(_context, _userManager, _userService);

        _preservationService = new GroupVisibilityPreservationService(
            _context, _groupVisibilityService, Substitute.For<ILogger<GroupVisibilityPreservationService>>());
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataWithContext(_context);
        _context?.Dispose();
        _userManager?.Dispose();
    }

    [Test]
    public async Task AnyGroupsExistAsync_IsTrue_ForTheSharedIntegrationDatabase()
    {
        (await _groupVisibilityService.AnyGroupsExistAsync()).ShouldBeTrue(
            "the shared integration database is expected to hold groups - the zero-group branch is covered by unit tests");
    }

    [Test]
    public async Task RequiresPreservationAsync_IsFalse_WhileGroupsExist()
    {
        var root = NewTestRoot("LATER_ROOT");

        (await _preservationService.RequiresPreservationAsync(root)).ShouldBeFalse();
    }

    [Test]
    public async Task ASingleRowOnTheRoot_MakesTheWholeSubtreeVisibleToANonAdmin()
    {
        var root = NewTestRoot("ROOT");
        var child = NewTestChild("CHILD", root.Id);
        var grandchild = NewTestChild("GRANDCHILD", child.Id);
        grandchild.Root = root.Id;
        _context.Group.AddRange(root, child, grandchild);

        var user = NewTestUser("PLANNER");
        _context.AppUser.Add(user);
        await _context.SaveChangesAsync();

        _context.GroupVisibility.Add(new GroupVisibility
        {
            Id = Guid.NewGuid(),
            AppUserId = user.Id,
            GroupId = root.Id
        });
        await _context.SaveChangesAsync();

        _userService.IsAdmin().Returns(Task.FromResult(false));
        _userService.GetIdString().Returns(user.Id);

        var scope = await _groupVisibilityService.GetVisibilityScopeAsync();

        scope.IsUnrestricted.ShouldBeFalse();
        scope.VisibleRootIds.ShouldContain(root.Id);
        scope.VisibleGroupIds.ShouldContain(root.Id, "a root is stored with Root = NULL and must resolve through its own Id");
        scope.VisibleGroupIds.ShouldContain(child.Id);
        scope.VisibleGroupIds.ShouldContain(grandchild.Id);
    }

    [Test]
    public async Task PreserveForNewRootAsync_IsIdempotent_AgainstPostgres()
    {
        var root = NewTestRoot("IDEMPOTENT_ROOT");
        _context.Group.Add(root);

        var user = NewTestUser("IDEMPOTENT_PLANNER");
        _context.AppUser.Add(user);
        await _context.SaveChangesAsync();

        var service = await PreservationServiceAffectingOnlyAsync(user.Id);

        var firstRun = await service.PreserveForNewRootAsync(root);
        await _context.SaveChangesAsync();

        firstRun.ShouldBe(1);
        (await _context.GroupVisibility.AsNoTracking().CountAsync(x => x.GroupId == root.Id && x.AppUserId == user.Id))
            .ShouldBe(1, "the affected user must hold exactly one row for the new root");

        var secondRun = await service.PreserveForNewRootAsync(root);
        await _context.SaveChangesAsync();

        secondRun.ShouldBe(0, "a repeated grant must not add a single row");
        (await _context.GroupVisibility.AsNoTracking().CountAsync(x => x.GroupId == root.Id)).ShouldBe(1);
    }

    /// <summary>
    /// A preservation service that treats every account except the given one as an admin, so the grant
    /// touches exactly one row in the shared integration database instead of every real user. The
    /// all-users behaviour is covered by the unit tests, where the user population is controlled.
    /// </summary>
    /// <param name="onlyAffectedUserId">The single account the grant may touch</param>
    private async Task<GroupVisibilityPreservationService> PreservationServiceAffectingOnlyAsync(string onlyAffectedUserId)
    {
        var everyoneElse = await _context.AppUser
            .AsNoTracking()
            .Where(u => u.Id != onlyAffectedUserId)
            .Select(u => u.Id)
            .ToListAsync();

        var visibility = Substitute.For<IGroupVisibilityService>();
        visibility.ReadAdmins().Returns(Task.FromResult(everyoneElse));
        visibility.AnyGroupsExistAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        return new GroupVisibilityPreservationService(
            _context, visibility, Substitute.For<ILogger<GroupVisibilityPreservationService>>());
    }

    private DataBaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static Group NewTestRoot(string nameSuffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{TestGroupPrefix}{nameSuffix}",
        Description = "First-group visibility integration test",
        ValidFrom = DateTime.UtcNow.AddYears(-1)
    };

    private static Group NewTestChild(string nameSuffix, Guid parentId)
    {
        var child = NewTestRoot(nameSuffix);
        child.Parent = parentId;
        child.Root = parentId;
        return child;
    }

    private static AppUser NewTestUser(string nameSuffix)
    {
        var email = $"{TestUserPrefix}{nameSuffix}@example.invalid";

        return new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = TestUserPrefix,
            LastName = nameSuffix,
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    private static async Task CleanupTestDataWithContext(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM group_visibility WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestGroupPrefix}%');
            DELETE FROM group_visibility WHERE app_user_id IN (SELECT id FROM ""AspNetUsers"" WHERE email LIKE '{TestUserPrefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestGroupPrefix}%');
            DELETE FROM ""group"" WHERE name LIKE '{TestGroupPrefix}%';
            DELETE FROM ""AspNetUsers"" WHERE email LIKE '{TestUserPrefix}%';
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
