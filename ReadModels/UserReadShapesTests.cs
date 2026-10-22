// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Security;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Authentification;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.ReadModels;

/// <summary>
/// Pins, against real PostgreSQL and a real UserManager/UserStore, the user reads of UserManagementService: the user
/// list skips deactivated accounts and comes in DisplayOrder, the password-reset lookup matches the stored token
/// hash and returns a user tracked by the request's context (the reset flow changes it and saves it through
/// UserManager), and the lookup by id returns an untracked user. The list reads the whole table, so its assertions
/// stay on the seeded users. Every user carries the INTEGRATION_TEST_UserReads_ prefix in user name and e-mail and
/// the cleanup deletes exactly those.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class UserReadShapesTests
{
    private const string Prefix = "INTEGRATION_TEST_UserReads_";
    private const int DisplayOrderBase = 970_000;

    private DataBaseContext _context = null!;
    private UserManager<AppUser> _userManager = null!;
    private AppUserReader _reader = null!;
    private UserManagementService _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        _userManager = new UserManager<AppUser>(
            new UserStore<AppUser, IdentityRole, DataBaseContext>(_context),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        _reader = new AppUserReader(_userManager);
        _sut = new UserManagementService(_userManager, _reader, NullLogger<UserManagementService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _userManager.Dispose();
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task UserList_SkipsDeactivatedAccounts_InDisplayOrder()
    {
        var third = NewUser("third", 3);
        var first = NewUser("first", 1);
        var gone = NewUser("gone", 2);
        gone.DeactivatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = NewUser("second", 2);
        _context.AppUser.AddRange(third, first, gone, second);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var list = await _sut.GetUserListAsync();

        var seeded = list.Where(u => u.UserName.StartsWith(Prefix)).ToList();
        seeded.Select(u => u.Id).ShouldBe([first.Id, second.Id, third.Id]);
        seeded[0].Email.ShouldBe(first.Email);
        seeded[0].FirstName.ShouldBe("Fn-first");
        seeded.ShouldAllBe(u => !u.IsAdmin && !u.IsAuthorised);
    }

    [Test]
    public async Task UserList_FlagsTheAdminRole_WhenTheRoleExists()
    {
        var adminRole = await _context.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.NormalizedName == Roles.Admin.ToUpperInvariant());
        Assume.That(adminRole, Is.Not.Null, "the integration database carries no Admin role");

        var admin = NewUser("admin", 1);
        var plain = NewUser("plain", 2);
        _context.AppUser.AddRange(admin, plain);
        await _context.SaveChangesAsync();
        _context.UserRoles.Add(new IdentityUserRole<string> { UserId = admin.Id, RoleId = adminRole!.Id });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var seeded = (await _sut.GetUserListAsync()).Where(u => u.UserName.StartsWith(Prefix)).ToList();

        seeded.Select(u => (u.Id, u.IsAdmin)).ShouldBe([(admin.Id, true), (plain.Id, false)]);
    }

    [Test]
    public async Task FindByToken_ReturnsTheTrackedHolder_ThatTheResetFlowCanSave()
    {
        const string token = "integration-reset-token";
        var holder = NewUser("holder", 1);
        holder.PasswordResetToken = PasswordResetTokenHasher.Hash(token);
        holder.PasswordResetTokenExpires = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var other = NewUser("other", 2);
        other.PasswordResetToken = PasswordResetTokenHasher.Hash("integration-other-token");
        _context.AppUser.AddRange(holder, other);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var found = await _sut.FindUserByTokenAsync(token);

        found.ShouldNotBeNull();
        found.Id.ShouldBe(holder.Id);
        _context.Entry(found).State.ShouldBe(EntityState.Unchanged);
        (await _sut.FindUserByTokenAsync("integration-unknown-token")).ShouldBeNull();

        found.PasswordResetToken = null;
        found.PasswordResetTokenExpires = null;
        var (success, _) = await _sut.UpdateUserAsync(found);
        success.ShouldBeTrue();
        _context.ChangeTracker.Clear();
        var reloaded = await _context.AppUser.AsNoTracking().SingleAsync(u => u.Id == holder.Id);
        reloaded.PasswordResetToken.ShouldBeNull();
    }

    [Test]
    public async Task FindByIdNoTracking_ReturnsAnUntrackedUser()
    {
        var user = NewUser("plain", 1);
        _context.AppUser.Add(user);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var found = await _sut.FindUserByIdNoTrackingAsync(user.Id);

        found.ShouldNotBeNull();
        found.UserName.ShouldBe(user.UserName);
        _context.Entry(found).State.ShouldBe(EntityState.Detached);
        (await _sut.FindUserByIdNoTrackingAsync(Guid.NewGuid().ToString())).ShouldBeNull();
    }

    [Test]
    public async Task ReaderReads_EqualTheFormerUserManagerUsersComposition()
    {
        const string token = "integration-compare-token";
        var holder = NewUser("cmp-holder", 2);
        holder.PasswordResetToken = PasswordResetTokenHasher.Hash(token);
        var first = NewUser("cmp-first", 1);
        var gone = NewUser("cmp-gone", 3);
        gone.DeactivatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _context.AppUser.AddRange(holder, first, gone);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var active = (await _reader.GetActiveUsersInDisplayOrderAsync()).Where(u => u.UserName!.StartsWith(Prefix)).Select(u => u.Id).ToList();
        var legacyActive = (await _userManager.Users.Where(u => u.DeactivatedAt == null).OrderBy(u => u.DisplayOrder).ToListAsync())
            .Where(u => u.UserName!.StartsWith(Prefix)).Select(u => u.Id).ToList();
        active.ShouldBe(legacyActive);
        active.ShouldBe([first.Id, holder.Id]);

        var hash = PasswordResetTokenHasher.Hash(token);
        (await _reader.FindByPasswordResetTokenHashAsync(hash))!.Id
            .ShouldBe((await _userManager.Users.FirstOrDefaultAsync(u => u.PasswordResetToken == hash))!.Id);
        (await _reader.FindByIdNoTrackingAsync(gone.Id))!.DeactivatedAt
            .ShouldBe((await _userManager.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == gone.Id))!.DeactivatedAt);
    }

    private static AppUser NewUser(string suffix, int displayOrder)
    {
        var userName = Prefix + suffix;
        var email = $"{Prefix}{suffix}@test.local";
        return new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = "Fn-" + suffix,
            LastName = "Ln-" + suffix,
            DisplayOrder = DisplayOrderBase + displayOrder,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        };
    }

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM ""AspNetUserRoles"" WHERE user_id IN (SELECT id FROM ""AspNetUsers"" WHERE starts_with(user_name, '{Prefix}'));
            DELETE FROM ""AspNetUsers"" WHERE starts_with(user_name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
