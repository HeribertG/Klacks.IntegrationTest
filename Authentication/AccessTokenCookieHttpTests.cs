// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// HTTP-level proof of the klacks_access cookie through the real pipeline (JwtBearer events, authorization middleware,
/// global access-cookie guard, MVC, SignalR negotiate) on the shared integration database:
/// - login and refresh set the cookie next to the refresh cookie (HttpOnly, Path=/, Secure over HTTPS, SameSite from
///   configuration, Max-Age = access token lifetime) and keep returning the token in the body; logout deletes both;
/// - a GET with only the cookie authenticates; the Authorization header always wins over the cookie;
/// - a cookie-authenticated unsafe request without X-Requested-With and without JSON is 403 before model binding, the same
///   request with X-Requested-With or with a header instead of the cookie is not;
/// - a cookie-authenticated request with a foreign Origin is 403, also the hub negotiate and the hub GET (the WebSocket
///   upgrade path); the whitelisted origin and header-authenticated requests pass;
/// - a real WebSocket upgrade to a hub with only the cookie connects with Sec-Fetch-Site same-origin, is 403 from another
///   port of the same host (same-site, no CORS match) and from a foreign site, and without Sec-Fetch-Site falls back to the
///   hostname rule;
/// - JwtBearer is wired with EventsType = KlacksJwtBearerEvents.
/// Access tokens are issued through the authentication service instead of the login endpoint where the login itself is
/// not under test, which keeps the fixture below the login rate limit. Test users carry the INTEGRATION_TEST_ prefix and
/// are the only rows cleaned up.
/// </summary>

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Presentation.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Authentication;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class AccessTokenCookieHttpTests
{
    private const string TestUserPrefix = "INTEGRATION_TEST_ACCESSCOOKIE_";
    private const string Password = "Integration#Access2026!xY";
    private const string LoginPath = "/api/backend/Accounts/LoginUser";
    private const string RefreshPath = "/api/backend/Accounts/RefreshToken";
    private const string LogoutPath = "/api/backend/Accounts/Logout";
    private const string ValidateTokenPath = "/api/backend/Accounts/ValidateToken";
    private const string UnsafePostPath = "/api/backend/Groups/GetSimpleList";
    private const string HubNegotiatePath = "/hubs/work-notifications/negotiate?negotiateVersion=1";
    private const string HubConnectPath = "/hubs/work-notifications?id=unknown-connection";
    private const string HubWebSocketPathPrefix = "hubs/work-notifications?id=";
    private const string ConnectionTokenJsonProperty = "connectionToken";
    private const string TestServerOrigin = "http://localhost";
    private const string SameHostOtherPortOrigin = "http://localhost:9999";
    private const string SameSiteFetch = "same-site";
    private const string CrossSiteFetch = "cross-site";
    private const string ForbiddenStatusText = "403";
    private const string HttpsBase = "https://localhost";
    private const string ForeignOrigin = "https://evil.example.org";
    private const string TokenJsonProperty = "token";
    private const string JsonMediaType = "application/json";
    private const string TextPlainMediaType = "text/plain";
    private const string BearerScheme = "Bearer";
    private const string GarbageToken = "not-a-jwt";

    private RefreshTokenCookieTestWebApplicationFactory _factory = null!;
    private string _email = null!;
    private string _userId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RefreshTokenCookieTestWebApplicationFactory();
        await CleanupAsync();

        var userName = TestUserPrefix + Guid.NewGuid().ToString("N");
        _email = userName.ToLowerInvariant() + "@example.com";
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName, Email = _email, EmailConfirmed = true, FirstName = "Access", LastName = "Cookie" };
        var created = await userManager.CreateAsync(user, Password);
        created.Succeeded.ShouldBeTrue(string.Join(", ", created.Errors.Select(e => e.Code)));
        _userId = user.Id;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await CleanupAsync();
        _factory.Dispose();
    }

    [Test]
    public async Task Login_SetsTheAccessCookieWithItsAttributes_AndKeepsTheTokenInTheBody()
    {
        using var client = CreateClient();

        using var response = await client.PostAsync(LoginPath, Json(new { email = _email, password = Password }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = CookieNamed(response, AccessTokenCookieConstants.CookieName);
        AssertAccessCookieAttributes(cookie);
        CookieNamed(response, RefreshTokenCookieConstants.CookieName).Value.ToString().ShouldNotBeNullOrEmpty();
        Uri.UnescapeDataString(cookie.Value.ToString()).ShouldBe(await BodyTokenAsync(response));
    }

    [Test]
    public async Task Refresh_SetsAFreshAccessCookie()
    {
        var refreshToken = await IssueRefreshTokenAsync();
        using var request = Request(HttpMethod.Post, RefreshPath, requestedWith: true);
        request.Headers.Add(HeaderNames.Cookie, $"{RefreshTokenCookieConstants.CookieName}={Uri.EscapeDataString(refreshToken)}");

        using var response = await CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = CookieNamed(response, AccessTokenCookieConstants.CookieName);
        AssertAccessCookieAttributes(cookie);
        Uri.UnescapeDataString(cookie.Value.ToString()).ShouldBe(await BodyTokenAsync(response));
    }

    [Test]
    public async Task Get_WithOnlyTheAccessCookie_Authenticates_AndWithoutAnythingIs401()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var withCookie = await SendAsync(HttpMethod.Get, ValidateTokenPath, accessCookie: accessToken);
        using var anonymous = await SendAsync(HttpMethod.Get, ValidateTokenPath);

        withCookie.StatusCode.ShouldBe(HttpStatusCode.OK);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AnInvalidAuthorizationHeader_BeatsAValidCookie()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var response = await SendAsync(HttpMethod.Get, ValidateTokenPath, accessCookie: accessToken, bearer: GarbageToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AValidAuthorizationHeader_BeatsAnInvalidCookie()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var response = await SendAsync(HttpMethod.Get, ValidateTokenPath, accessCookie: GarbageToken, bearer: accessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task CookieAuthenticatedPost_WithTextPlainAndWithoutRequestedWith_Is403()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var response = await SendAsync(HttpMethod.Post, UnsafePostPath, accessCookie: accessToken, content: TextPlain());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CookieAuthenticatedPost_WithTextPlainAndRequestedWith_PassesTheGuard()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var response = await SendAsync(
            HttpMethod.Post, UnsafePostPath, accessCookie: accessToken, content: TextPlain(), requestedWith: true);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task CookieAuthenticatedPost_WithJson_PassesTheGuard()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var response = await SendAsync(HttpMethod.Post, UnsafePostPath, accessCookie: accessToken, content: Json(new { }));

        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task HeaderAuthenticatedPost_WithTextPlainAndWithoutRequestedWith_IsNotGuarded()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var response = await SendAsync(
            HttpMethod.Post, UnsafePostPath, accessCookie: accessToken, bearer: accessToken, content: TextPlain(), origin: ForeignOrigin);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task CookieAuthenticatedGet_WithAForeignOrigin_Is403_AndWithTheWhitelistedOriginIs200()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var foreign = await SendAsync(HttpMethod.Get, ValidateTokenPath, accessCookie: accessToken, origin: ForeignOrigin);
        using var whitelisted = await SendAsync(
            HttpMethod.Get, ValidateTokenPath, accessCookie: accessToken, origin: RefreshTokenCookieTestWebApplicationFactory.WhitelistedOrigin);

        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        whitelisted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task HubNegotiate_WithTheCookie_Works_AndWithAForeignOriginIs403()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var sameOrigin = await SendAsync(HttpMethod.Post, HubNegotiatePath, accessCookie: accessToken, requestedWith: true);
        using var foreign = await SendAsync(
            HttpMethod.Post, HubNegotiatePath, accessCookie: accessToken, requestedWith: true, origin: ForeignOrigin);

        sameOrigin.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task HubNegotiate_WithTheHeader_AndAForeignOrigin_IsNotGuarded()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var response = await SendAsync(
            HttpMethod.Post, HubNegotiatePath, bearer: accessToken, requestedWith: true, origin: ForeignOrigin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task HubConnectGet_WithTheCookieAndAForeignOrigin_Is403_AndWithTheWhitelistedOriginIsNot()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var foreign = await SendAsync(HttpMethod.Get, HubConnectPath, accessCookie: accessToken, origin: ForeignOrigin);
        using var whitelisted = await SendAsync(
            HttpMethod.Get, HubConnectPath, accessCookie: accessToken, origin: RefreshTokenCookieTestWebApplicationFactory.WhitelistedOrigin);

        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        whitelisted.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        whitelisted.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Logout_DeletesBothCookies()
    {
        var accessToken = await IssueAccessTokenAsync();
        var refreshToken = await IssueRefreshTokenAsync();
        using var request = Request(HttpMethod.Post, LogoutPath, requestedWith: true);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{RefreshTokenCookieConstants.CookieName}={Uri.EscapeDataString(refreshToken)}; {AccessTokenCookieConstants.CookieName}={accessToken}");

        using var response = await CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertDeleted(CookieNamed(response, AccessTokenCookieConstants.CookieName), AccessTokenCookieConstants.CookiePath);
        AssertDeleted(CookieNamed(response, RefreshTokenCookieConstants.CookieName), RefreshTokenCookieConstants.CookiePath);
    }

    [Test]
    public void JwtBearer_IsWiredThroughTheEventsType()
    {
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.EventsType.ShouldBe(typeof(KlacksJwtBearerEvents));
    }

    [Test]
    public async Task HubWebSocket_WithTheCookieAndSameOriginFetchSite_Connects()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var socket = await ConnectHubWebSocketAsync(accessToken, TestServerOrigin, CsrfProofConstants.FetchSiteSameOrigin);

        socket.State.ShouldBe(System.Net.WebSockets.WebSocketState.Open);
    }

    [Test]
    public async Task HubWebSocket_WithTheCookieFromAnotherPortOfTheHost_SameSite_Is403()
    {
        var accessToken = await IssueAccessTokenAsync();

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => ConnectHubWebSocketAsync(accessToken, SameHostOtherPortOrigin, SameSiteFetch));

        failure.Message.ShouldContain(ForbiddenStatusText);
    }

    [Test]
    public async Task HubWebSocket_WithTheCookieFromAForeignSite_Is403()
    {
        var accessToken = await IssueAccessTokenAsync();

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => ConnectHubWebSocketAsync(accessToken, ForeignOrigin, CrossSiteFetch));

        failure.Message.ShouldContain(ForbiddenStatusText);
    }

    [Test]
    public async Task HubWebSocket_WithoutFetchSite_FallsBackToTheHostnameRule()
    {
        var accessToken = await IssueAccessTokenAsync();

        using var sameHost = await ConnectHubWebSocketAsync(accessToken, SameHostOtherPortOrigin, fetchSite: null);
        var foreign = await Should.ThrowAsync<InvalidOperationException>(
            () => ConnectHubWebSocketAsync(accessToken, ForeignOrigin, fetchSite: null));

        sameHost.State.ShouldBe(System.Net.WebSockets.WebSocketState.Open);
        foreign.Message.ShouldContain(ForbiddenStatusText);
    }

    private async Task<System.Net.WebSockets.WebSocket> ConnectHubWebSocketAsync(string accessToken, string origin, string? fetchSite)
    {
        string connectionToken;
        using (var negotiate = await SendAsync(HttpMethod.Post, HubNegotiatePath, accessCookie: accessToken, requestedWith: true))
        {
            negotiate.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync());
            connectionToken = json.RootElement.GetProperty(ConnectionTokenJsonProperty).GetString()!;
        }

        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers[HeaderNames.Cookie] = $"{AccessTokenCookieConstants.CookieName}={accessToken}";
            request.Headers[HeaderNames.Origin] = origin;
            if (fetchSite != null)
            {
                request.Headers[CsrfProofConstants.FetchSiteHeader] = fetchSite;
            }
        };

        var uri = new Uri(_factory.Server.BaseAddress, HubWebSocketPathPrefix + Uri.EscapeDataString(connectionToken));
        return await client.ConnectAsync(uri, CancellationToken.None);
    }

    private static void AssertAccessCookieAttributes(SetCookieHeaderValue cookie)
    {
        cookie.Value.ToString().ShouldNotBeNullOrEmpty();
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        cookie.Path.ToString().ShouldBe(AccessTokenCookieConstants.CookiePath);
        cookie.SameSite.ShouldBe(Microsoft.Net.Http.Headers.SameSiteMode.Strict);
        cookie.MaxAge.ShouldBe(AccessTokenLifetime.Duration);
        cookie.Domain.HasValue.ShouldBeFalse();
    }

    private static void AssertDeleted(SetCookieHeaderValue cookie, string path)
    {
        cookie.Value.ToString().ShouldBeEmpty();
        cookie.Expires!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow);
        cookie.Path.ToString().ShouldBe(path);
    }

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(HttpsBase),
        HandleCookies = false,
        AllowAutoRedirect = false,
    });

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? accessCookie = null,
        string? bearer = null,
        HttpContent? content = null,
        bool requestedWith = false,
        string? origin = null)
    {
        using var client = CreateClient();
        using var request = Request(method, path, requestedWith);
        request.Content = content;
        if (accessCookie != null)
        {
            request.Headers.Add(HeaderNames.Cookie, $"{AccessTokenCookieConstants.CookieName}={accessCookie}");
        }

        if (bearer != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, bearer);
        }

        if (origin != null)
        {
            request.Headers.Add(HeaderNames.Origin, origin);
        }

        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, bool requestedWith)
    {
        var request = new HttpRequestMessage(method, path);
        if (requestedWith)
        {
            request.Headers.Add(CsrfProofConstants.RequestedWithHeader, CsrfProofConstants.RequestedWithValue);
        }

        return request;
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, JsonMediaType);

    private static StringContent TextPlain() =>
        new("{}", Encoding.UTF8, TextPlainMediaType);

    private static async Task<string> BodyTokenAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty(TokenJsonProperty).GetString()!;
    }

    private static SetCookieHeaderValue CookieNamed(HttpResponseMessage response, string name)
    {
        response.Headers.TryGetValues(HeaderNames.SetCookie, out var values).ShouldBeTrue("no Set-Cookie header");
        var cookies = SetCookieHeaderValue.ParseList(values!.ToList())
            .Where(c => c.Name.Equals(name))
            .ToList();
        cookies.Count.ShouldBe(1, name);
        return cookies[0];
    }

    private async Task<string> IssueAccessTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(_userId);
        var result = await scope.ServiceProvider.GetRequiredService<IAccountAuthenticationService>()
            .GenerateAuthenticationAsync(user!, withRefreshToken: false);
        result.Token.ShouldNotBeNullOrEmpty();
        return result.Token;
    }

    private async Task<string> IssueRefreshTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().CreateRefreshTokenAsync(_userId);
    }

    private async Task CleanupAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var userIds = await context.AppUser
            .Where(u => u.UserName!.StartsWith(TestUserPrefix))
            .Select(u => u.Id)
            .ToListAsync();
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
