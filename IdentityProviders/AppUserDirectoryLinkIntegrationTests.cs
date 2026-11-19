// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Postgres proof of the directory link on AppUser (IdentityProviderId + DirectoryExternalId): the IAppUserReader
/// queries the directory sign-in and the approval list use (find by link, list by provider including deactivated
/// accounts, e-mails already in use through the normalised e-mail, accounts holding a role, a link left on a
/// soft-deleted provider), the unique index over a linked pair (the same pair twice is refused, the same external id
/// under another provider and any number of unlinked accounts are fine), and that deleting a provider through its
/// repository is a soft delete that keeps the link. Test rows carry the INTEGRATION_TEST_ prefix and are the only rows
/// cleaned up.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.IdentityProviders;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class AppUserDirectoryLinkIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_DIRLINK_";

    private LdapClientSyncTestFactory _factory = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new LdapClientSyncTestFactory();
        _ = _factory.Services;
        await CleanupAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await CleanupAsync();
        _factory.Dispose();
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task FindByDirectoryLink_ReturnsTheLinkedAccountOfThatProviderAndIdOnly()
    {
        var providerId = await CreateProviderAsync();
        var otherProviderId = await CreateProviderAsync();
        var linked = await CreateUserAsync("linked", providerId, "ext-1");
        await CreateUserAsync("other", otherProviderId, "ext-2");

        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAppUserReader>();

        (await reader.FindByDirectoryLinkAsync(providerId, "ext-1"))!.Id.ShouldBe(linked.Id);
        (await reader.FindByDirectoryLinkAsync(providerId, "ext-2")).ShouldBeNull();
        (await reader.FindByDirectoryLinkAsync(otherProviderId, "ext-1")).ShouldBeNull();
        (await reader.FindByDirectoryLinkAsync(Guid.NewGuid(), "ext-1")).ShouldBeNull();
    }

    [Test]
    public async Task GetByIdentityProvider_ReturnsEveryLinkedAccountOfTheProviderIncludingDeactivatedOnes()
    {
        var providerId = await CreateProviderAsync();
        var otherProviderId = await CreateProviderAsync();
        var active = await CreateUserAsync("active", providerId, "ext-a");
        var deactivated = await CreateUserAsync("deactivated", providerId, "ext-d", deactivatedAt: DateTime.UtcNow.AddDays(-1));
        await CreateUserAsync("elsewhere", otherProviderId, "ext-e");
        await CreateUserAsync("unlinked", providerId: null, externalId: null);

        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAppUserReader>();

        var users = await reader.GetByIdentityProviderAsync(providerId);

        users.Select(u => u.Id).ShouldBe([active.Id, deactivated.Id], ignoreOrder: true);
    }

    [Test]
    public async Task GetEmailsInUse_FindsLinkedAndUnlinkedAccountsByNormalisedEmailInTheInputSpelling()
    {
        var providerId = await CreateProviderAsync();
        await CreateUserAsync("unlinked", providerId: null, externalId: null);
        await CreateUserAsync("linked", providerId, "ext-1");

        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAppUserReader>();

        var upperUnlinked = EmailOf("unlinked").ToUpperInvariant();
        var inUse = await reader.GetEmailsInUseAsync([upperUnlinked, EmailOf("linked"), EmailOf("nobody"), "  "]);

        inUse.ShouldBe([upperUnlinked, EmailOf("linked")], ignoreOrder: true);
        (await reader.GetEmailsInUseAsync([])).ShouldBeEmpty();
    }

    [Test]
    public async Task GetUserIdsWithRoles_ReturnsOnlyAccountsHoldingARole()
    {
        var withRole = await CreateUserAsync("withrole", providerId: null, externalId: null);
        var withoutRole = await CreateUserAsync("withoutrole", providerId: null, externalId: null);
        using (var roleScope = _factory.Services.CreateScope())
        {
            var userManager = roleScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var roleManager = roleScope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleManager.RoleExistsAsync(Roles.Authorised))
            {
                await roleManager.CreateAsync(new IdentityRole(Roles.Authorised));
            }

            (await userManager.AddToRoleAsync((await userManager.FindByIdAsync(withRole.Id))!, Roles.Authorised)).Succeeded.ShouldBeTrue();
        }

        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAppUserReader>();

        var ids = await reader.GetUserIdsWithRolesAsync([withRole.Id, withoutRole.Id]);

        ids.ShouldBe([withRole.Id]);
    }

    [Test]
    public async Task FindLinkedToDeletedProvider_FindsTheLinkOnlyWhenItsProviderIsSoftDeleted()
    {
        var deletedProviderId = await CreateProviderAsync();
        var liveProviderId = await CreateProviderAsync();
        var orphan = await CreateUserAsync("orphan", deletedProviderId, "ext-orphan");
        await CreateUserAsync("live", liveProviderId, "ext-live");
        await SoftDeleteProviderAsync(deletedProviderId);

        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IAppUserReader>();

        (await reader.FindLinkedToDeletedProviderAsync("ext-orphan"))!.Id.ShouldBe(orphan.Id);
        (await reader.FindLinkedToDeletedProviderAsync("ext-live")).ShouldBeNull();
        (await reader.FindLinkedToDeletedProviderAsync("ext-nobody")).ShouldBeNull();
    }

    [Test]
    public async Task UniqueIndex_RefusesTheSameLinkedPairTwice()
    {
        var providerId = await CreateProviderAsync();
        await CreateUserAsync("first", providerId, "ext-1");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        context.AppUser.Add(NewUser("second", providerId, "ext-1", deactivatedAt: null));

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Test]
    public async Task UniqueIndex_AllowsTheSameExternalIdUnderAnotherProviderAndManyUnlinkedAccounts()
    {
        var providerId = await CreateProviderAsync();
        var otherProviderId = await CreateProviderAsync();

        await CreateUserAsync("first", providerId, "ext-1");
        await CreateUserAsync("second", otherProviderId, "ext-1");
        await CreateUserAsync("third", providerId: null, externalId: null);
        await CreateUserAsync("fourth", providerId: null, externalId: null);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        (await context.AppUser.CountAsync(u => u.UserName!.StartsWith(TestPrefix))).ShouldBe(4);
    }

    [Test]
    public async Task DeletingTheProvider_IsASoftDeleteThatKeepsTheLink()
    {
        var providerId = await CreateProviderAsync();
        var linked = await CreateUserAsync("linked", providerId, "ext-1");

        await SoftDeleteProviderAsync(providerId);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var after = await verifyContext.AppUser.AsNoTracking().SingleAsync(u => u.Id == linked.Id);
        after.IdentityProviderId.ShouldBe(providerId);
        after.DirectoryExternalId.ShouldBe("ext-1");
        (await verifyContext.Set<IdentityProvider>().IgnoreQueryFilters().SingleAsync(p => p.Id == providerId)).IsDeleted.ShouldBeTrue();
    }

    private async Task SoftDeleteProviderAsync(Guid providerId)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IIdentityProviderRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        (await repository.Delete(providerId)).ShouldNotBeNull();
        await unitOfWork.CompleteAsync();
    }

    private static string EmailOf(string kind) => $"{TestPrefix}{kind}@example.com".ToLowerInvariant();

    private static AppUser NewUser(string kind, Guid? providerId, string? externalId, DateTime? deactivatedAt)
    {
        var name = $"{TestPrefix}{kind}_{Guid.NewGuid():N}";
        return new AppUser
        {
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = EmailOf(kind),
            NormalizedEmail = EmailOf(kind).ToUpperInvariant(),
            EmailConfirmed = true,
            FirstName = "Integration",
            LastName = kind,
            IdentityProviderId = providerId,
            DirectoryExternalId = externalId,
            DeactivatedAt = deactivatedAt,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
    }

    private async Task<AppUser> CreateUserAsync(string kind, Guid? providerId, string? externalId, DateTime? deactivatedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var user = NewUser(kind, providerId, externalId, deactivatedAt);
        context.AppUser.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<Guid> CreateProviderAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var provider = new IdentityProvider
        {
            Id = Guid.NewGuid(),
            Name = $"{TestPrefix}{Guid.NewGuid():N}",
            Type = IdentityProviderType.Ldap,
            IsEnabled = true,
        };
        context.Add(provider);
        await context.SaveChangesAsync();
        return provider.Id;
    }

    private async Task CleanupAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        await context.AppUser.Where(u => u.UserName!.StartsWith(TestPrefix)).ExecuteDeleteAsync();
        await context.Set<IdentityProvider>().IgnoreQueryFilters().Where(p => p.Name.StartsWith(TestPrefix)).ExecuteDeleteAsync();
    }
}
