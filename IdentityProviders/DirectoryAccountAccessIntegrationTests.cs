// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// HTTP-level proof of the directory sign-in approval through the real pipeline (authorization, model binding, handlers,
/// UserManager, Postgres), with ILdapService substituted so no directory server is contacted:
/// - granting access creates a password-less account without a role that shows up in the user list (flagged as
///   directory-only); withdrawing hides it from the list and granting again brings the same account back;
/// - the approval list reports the link state of the rows;
/// - both endpoints are admin only (403 for an account without a role, 401 without a token);
/// - a provider not used for authentication and an entry the directory does not know are coded 400 refusals;
/// - an e-mail already used by a Klacks account is flagged on the row and refuses the grant (no linking by e-mail);
///   withdrawing an entry nothing is linked to is refused; a link left on a soft-deleted provider moves to the provider
///   the entry is granted on next;
/// - signing in with the typed directory name works for an approved account (200 with a token) and answers 401 with the
///   code directoryAccessNotGranted when the directory password is right but the account was never approved or was
///   withdrawn; a wrong directory password is a plain 401 without that code.
/// Test rows carry the INTEGRATION_TEST_ prefix; accounts the approval created are removed through their provider link.
/// </summary>

using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.IdentityProviders;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class DirectoryAccountAccessIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_DIRACC_";
    private const string LocalPassword = "Integration#DirAcc2026!xY";
    private const string DirectoryPassword = "directory-secret";
    private const string WrongDirectoryPassword = "wrong-directory-secret";
    private const string BaseAddress = "https://localhost";
    private const string LoginPath = "/api/backend/Accounts/LoginUser";
    private const string UserListPath = "/api/backend/Accounts";
    private const string ProvidersPath = "/api/backend/IdentityProviders";
    private const string AccessSuffix = "/DirectoryAccounts/Access";
    private const string AccountsSuffix = "/DirectoryAccounts";
    private const string TokenProperty = "token";
    private const string ErrorCodeProperty = "errorCode";
    private const string NotGrantedCode = "directoryAccessNotGranted";
    private const string NotFoundCode = "directoryAccountNotFound";
    private const string NotUsableCode = "directoryProviderNotUsableForLogin";
    private const string EmailTakenCode = "directoryAccountEmailTaken";
    private const string NotLinkedCode = "directoryAccountNotLinked";

    private LdapClientSyncTestFactory _factory = null!;
    private Guid _providerId;
    private Guid _unusableProviderId;
    private string _adminToken = null!;
    private string _plannerToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new LdapClientSyncTestFactory();
        _ = _factory.Services;
        await CleanupAsync();

        await CreateLocalUserAsync("admin", Roles.Admin);
        await CreateLocalUserAsync("planner", role: null);
        _providerId = await CreateProviderAsync(useForAuthentication: true);
        _unusableProviderId = await CreateProviderAsync(useForAuthentication: false);
        _adminToken = await LoginLocalAsync("admin");
        _plannerToken = await LoginLocalAsync("planner");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await CleanupAsync();
        _factory.Dispose();
    }

    [TearDown]
    public async Task TearDown()
    {
        _factory.FakeLdapService.ClearSubstitute();
        await DeleteDirectoryAccountsAsync();
    }

    [Test]
    public async Task Grant_CreatesAPasswordlessRolelessAccountThatTheUserListShowsAsDirectoryOnly()
    {
        var entry = NewEntry();
        StubEntry(entry);

        using var response = await PutAccessAsync(_providerId, entry.StableId, mayLogin: true, _adminToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("mayLogin").GetBoolean().ShouldBeTrue();
        var userId = body.RootElement.GetProperty("userId").GetString();
        userId.ShouldNotBeNullOrEmpty();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var created = await userManager.FindByIdAsync(userId!);
        created.ShouldNotBeNull();
        created!.PasswordHash.ShouldBeNull();
        created.EmailConfirmed.ShouldBeTrue();
        created.IdentityProviderId.ShouldBe(_providerId);
        created.DirectoryExternalId.ShouldBe(entry.StableId);
        (await userManager.GetRolesAsync(created)).ShouldBeEmpty();

        var listed = await FindInUserListAsync(userId!);
        listed.ShouldNotBeNull();
        listed!.Value.GetProperty("isDirectoryLoginOnly").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task Revoke_HidesTheAccountFromTheUserList_AndGrantingAgainBringsTheSameAccountBack()
    {
        var entry = NewEntry();
        StubEntry(entry);
        var userId = await GrantAsync(entry);

        using var revoke = await PutAccessAsync(_providerId, entry.StableId, mayLogin: false, _adminToken);

        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var revoked = JsonDocument.Parse(await revoke.Content.ReadAsStringAsync()))
        {
            revoked.RootElement.GetProperty("mayLogin").GetBoolean().ShouldBeFalse();
        }

        (await FindInUserListAsync(userId)).ShouldBeNull();

        using var regrant = await PutAccessAsync(_providerId, entry.StableId, mayLogin: true, _adminToken);

        regrant.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var restored = JsonDocument.Parse(await regrant.Content.ReadAsStringAsync());
        restored.RootElement.GetProperty("userId").GetString().ShouldBe(userId);
        (await FindInUserListAsync(userId)).ShouldNotBeNull();
    }

    [Test]
    public async Task List_ReportsTheLinkStateOfTheRows()
    {
        var entry = NewEntry();
        StubEntry(entry);

        var before = await GetAccountRowAsync(entry.StableId);
        before.GetProperty("mayLogin").GetBoolean().ShouldBeFalse();
        before.GetProperty("email").GetString().ShouldBe(entry.Mail);

        var userId = await GrantAsync(entry);

        var after = await GetAccountRowAsync(entry.StableId);
        after.GetProperty("mayLogin").GetBoolean().ShouldBeTrue();
        after.GetProperty("userId").GetString().ShouldBe(userId);
    }

    [Test]
    public async Task AccountWithoutRole_IsForbiddenOnTheListAndOnTheAccessChange()
    {
        var entry = NewEntry();
        StubEntry(entry);

        using var list = await GetAccountsAsync(_providerId, _plannerToken);
        using var access = await PutAccessAsync(_providerId, entry.StableId, mayLogin: true, _plannerToken);

        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        access.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _factory.FakeLdapService.DidNotReceive().FindByExternalIdAsync(Arg.Any<IdentityProvider>(), Arg.Any<string>());
    }

    [Test]
    public async Task WithoutAToken_BothEndpointsAreUnauthorized()
    {
        using var list = await GetAccountsAsync(_providerId, token: null);
        using var access = await PutAccessAsync(_providerId, Guid.NewGuid().ToString(), mayLogin: true, token: null);

        list.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        access.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Grant_OnAProviderNotUsedForAuthentication_IsACodedBadRequest()
    {
        using var response = await PutAccessAsync(_unusableProviderId, Guid.NewGuid().ToString(), mayLogin: true, _adminToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeOfAsync(response)).ShouldBe(NotUsableCode);
    }

    [Test]
    public async Task Grant_ForAnEntryTheDirectoryDoesNotKnow_IsACodedBadRequest()
    {
        using var response = await PutAccessAsync(_providerId, Guid.NewGuid().ToString(), mayLogin: true, _adminToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeOfAsync(response)).ShouldBe(NotFoundCode);
    }

    [Test]
    public async Task Login_ApprovedAccount_SignsInWithTheDirectoryPassword()
    {
        var entry = NewEntry();
        StubEntry(entry);
        var userId = await GrantAsync(entry);

        using var response = await LoginAsync(entry.Uid!, DirectoryPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty(TokenProperty).GetString().ShouldNotBeNullOrEmpty();
        body.RootElement.GetProperty("id").GetString().ShouldBe(userId);
    }

    [Test]
    public async Task Login_RightDirectoryPasswordButNeverApproved_IsA401WithTheNotGrantedCode()
    {
        var entry = NewEntry();
        StubEntry(entry);

        using var response = await LoginAsync(entry.Uid!, DirectoryPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeOfAsync(response)).ShouldBe(NotGrantedCode);
    }

    [Test]
    public async Task Login_AfterTheAccessWasWithdrawn_IsA401WithTheNotGrantedCode()
    {
        var entry = NewEntry();
        StubEntry(entry);
        await GrantAsync(entry);
        using var revoke = await PutAccessAsync(_providerId, entry.StableId, mayLogin: false, _adminToken);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await LoginAsync(entry.Uid!, DirectoryPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeOfAsync(response)).ShouldBe(NotGrantedCode);
    }

    [Test]
    public async Task Login_WrongDirectoryPassword_IsAPlain401WithoutTheNotGrantedCode()
    {
        var entry = NewEntry();
        StubEntry(entry);
        await GrantAsync(entry);

        using var response = await LoginAsync(entry.Uid!, WrongDirectoryPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeOfAsync(response)).ShouldNotBe(NotGrantedCode);
    }

    [Test]
    public async Task EntryWhoseEmailAKlacksAccountUses_IsFlaggedAndCannotBeGranted()
    {
        var entry = NewEntry();
        entry.Mail = LocalEmail("planner");
        StubEntry(entry);

        var row = await GetAccountRowAsync(entry.StableId);
        row.GetProperty("emailInUse").GetBoolean().ShouldBeTrue();
        row.TryGetProperty("linkableUserName", out _).ShouldBeFalse();

        using var response = await PutAccessAsync(_providerId, entry.StableId, mayLogin: true, _adminToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeOfAsync(response)).ShouldBe(EmailTakenCode);
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        (await userManager.FindByEmailAsync(LocalEmail("planner")))!.IdentityProviderId.ShouldBeNull();
    }

    [Test]
    public async Task Revoke_WithoutALinkedAccount_IsACodedBadRequest()
    {
        var entry = NewEntry();
        StubEntry(entry);

        using var response = await PutAccessAsync(_providerId, entry.StableId, mayLogin: false, _adminToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeOfAsync(response)).ShouldBe(NotLinkedCode);
    }

    [Test]
    public async Task Grant_AfterTheOldProviderWasSoftDeleted_MovesTheExistingLinkInsteadOfCreatingAnAccount()
    {
        var oldProviderId = await CreateProviderAsync(useForAuthentication: true);
        var entry = NewEntry();
        StubEntry(entry, oldProviderId);
        StubEntry(entry);
        using (var oldGrant = await PutAccessAsync(oldProviderId, entry.StableId, mayLogin: true, _adminToken))
        {
            oldGrant.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var deleteScope = _factory.Services.CreateScope())
        {
            var repository = deleteScope.ServiceProvider.GetRequiredService<IIdentityProviderRepository>();
            var unitOfWork = deleteScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            (await repository.Delete(oldProviderId)).ShouldNotBeNull();
            await unitOfWork.CompleteAsync();
        }

        string oldUserId;
        using (var lookupScope = _factory.Services.CreateScope())
        {
            var context = lookupScope.ServiceProvider.GetRequiredService<DataBaseContext>();
            oldUserId = await context.AppUser.Where(u => u.DirectoryExternalId == entry.StableId).Select(u => u.Id).SingleAsync();
        }

        var userId = await GrantAsync(entry);

        userId.ShouldBe(oldUserId);
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<DataBaseContext>();
        (await verifyContext.AppUser.AsNoTracking().SingleAsync(u => u.Id == userId)).IdentityProviderId.ShouldBe(_providerId);
        (await verifyContext.AppUser.CountAsync(u => u.DirectoryExternalId == entry.StableId)).ShouldBe(1);
    }

    private static LdapUserEntry NewEntry()
    {
        var id = Guid.NewGuid();
        var name = $"{TestPrefix}{id:N}";
        return new LdapUserEntry
        {
            ObjectGuid = id.ToString(),
            DistinguishedName = $"uid={name},ou=people,dc=example,dc=com",
            Uid = name,
            GivenName = "Integration",
            Surname = "Directory",
            DisplayName = $"Integration Directory {id:N}",
            Mail = $"{name.ToLowerInvariant()}@example.com",
            IsEnabled = true,
        };
    }

    private void StubEntry(LdapUserEntry entry, Guid? providerId = null)
    {
        var id = providerId ?? _providerId;
        var fake = _factory.FakeLdapService;
        fake.FindByExternalIdAsync(Arg.Is<IdentityProvider>(p => p.Id == id), entry.StableId).Returns(entry);
        fake.FindLoginEntryAsync(Arg.Is<IdentityProvider>(p => p.Id == id), entry.Uid!).Returns(entry);
        fake.SearchAccountsAsync(Arg.Is<IdentityProvider>(p => p.Id == id), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new LdapUserSearchResult([entry], IsComplete: true));
        fake.BindAsUserAsync(Arg.Is<IdentityProvider>(p => p.Id == id), entry.DistinguishedName, DirectoryPassword)
            .Returns(true);
    }

    private async Task<string> GrantAsync(LdapUserEntry entry)
    {
        using var response = await PutAccessAsync(_providerId, entry.StableId, mayLogin: true, _adminToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("userId").GetString()!;
    }

    private async Task<JsonElement> GetAccountRowAsync(string externalId)
    {
        using var response = await GetAccountsAsync(_providerId, _adminToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        return document.RootElement.GetProperty("accounts").EnumerateArray()
            .Single(row => row.GetProperty("externalId").GetString() == externalId)
            .Clone();
    }

    private async Task<JsonElement?> FindInUserListAsync(string userId)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, UserListPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var user in document.RootElement.EnumerateArray())
        {
            if (user.GetProperty("id").GetString() == userId)
            {
                return user.Clone();
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage> GetAccountsAsync(Guid providerId, string? token)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ProvidersPath}/{providerId}{AccountsSuffix}");
        WithBearer(request, token);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PutAccessAsync(Guid providerId, string externalId, bool mayLogin, string? token)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{ProvidersPath}/{providerId}{AccessSuffix}")
        {
            Content = Json(new { externalId, mayLogin }),
        };
        WithBearer(request, token);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> LoginAsync(string name, string password)
    {
        using var client = CreateClient();
        return await client.PostAsync(LoginPath, Json(new { email = name, password }));
    }

    private async Task<string> LoginLocalAsync(string kind)
    {
        using var response = await LoginAsync(LocalEmail(kind), LocalPassword);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty(TokenProperty).GetString()!;
    }

    private static async Task<string?> ErrorCodeOfAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty(ErrorCodeProperty, out var code) ? code.GetString() : null;
    }

    private static void WithBearer(HttpRequestMessage request, string? token)
    {
        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, MediaTypeNames.Application.Json);

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(BaseAddress),
        HandleCookies = false,
        AllowAutoRedirect = false,
    });

    private static string LocalEmail(string kind) => $"{TestPrefix}{kind}@example.com".ToLowerInvariant();

    private async Task CreateLocalUserAsync(string kind, string? role)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = new AppUser
        {
            UserName = $"{TestPrefix}{kind}",
            Email = LocalEmail(kind),
            EmailConfirmed = true,
            FirstName = "Integration",
            LastName = kind,
        };
        var created = await userManager.CreateAsync(user, LocalPassword);
        created.Succeeded.ShouldBeTrue(string.Join(", ", created.Errors.Select(e => e.Code)));

        if (role != null)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                (await roleManager.CreateAsync(new IdentityRole(role))).Succeeded.ShouldBeTrue();
            }

            (await userManager.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        }
    }

    private async Task<Guid> CreateProviderAsync(bool useForAuthentication)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var provider = new IdentityProvider
        {
            Id = Guid.NewGuid(),
            Name = $"{TestPrefix}{Guid.NewGuid():N}",
            Type = IdentityProviderType.Ldap,
            IsEnabled = true,
            UseForAuthentication = useForAuthentication,
        };
        context.Add(provider);
        await context.SaveChangesAsync();
        return provider.Id;
    }

    private async Task DeleteDirectoryAccountsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var providerIds = await context.Set<IdentityProvider>()
            .IgnoreQueryFilters()
            .Where(p => p.Name.StartsWith(TestPrefix))
            .Select(p => p.Id)
            .ToListAsync();
        var userIds = await context.AppUser
            .Where(u => u.IdentityProviderId != null && providerIds.Contains(u.IdentityProviderId.Value))
            .Select(u => u.Id)
            .ToListAsync();
        await DeleteUsersAsync(scope, context, userIds);
    }

    private async Task CleanupAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var providerIds = await context.Set<IdentityProvider>()
            .IgnoreQueryFilters()
            .Where(p => p.Name.StartsWith(TestPrefix))
            .Select(p => p.Id)
            .ToListAsync();
        var userIds = await context.AppUser
            .Where(u => u.UserName!.StartsWith(TestPrefix)
                        || (u.IdentityProviderId != null && providerIds.Contains(u.IdentityProviderId.Value)))
            .Select(u => u.Id)
            .ToListAsync();
        await DeleteUsersAsync(scope, context, userIds);

        await context.Set<IdentityProvider>().IgnoreQueryFilters().Where(p => providerIds.Contains(p.Id)).ExecuteDeleteAsync();
    }

    private static async Task DeleteUsersAsync(IServiceScope scope, DataBaseContext context, List<string> userIds)
    {
        if (userIds.Count == 0)
        {
            return;
        }

        await context.RefreshToken.Where(rt => userIds.Contains(rt.AspNetUsersId)).ExecuteDeleteAsync();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        foreach (var id in userIds)
        {
            var user = await userManager.FindByIdAsync(id);
            if (user != null)
            {
                await userManager.DeleteAsync(user);
            }
        }
    }
}
