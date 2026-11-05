// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// HTTP-level proof of the refresh token cookie through the real pipeline (model binding, CSRF filter, authorization
/// middleware, JSON serialization) on the shared integration database:
/// - login sets the cookie with its attributes and keeps the refresh token out of the body (Secure only over HTTPS or
///   behind a proxy reporting https);
/// - refresh does not rotate: the cookie keeps its token, repeated refreshes work and renew Max-Age, also with a body of {};
///   a revoked cookie is 401; the cookie wins over a body token; a body token alone (migration) is exchanged for a new
///   cookie token while the old one stays valid only for the short grace, so two tabs migrating at once both succeed;
///   only that body-token answer also carries the new token in the body (clients still on the previous Ui), the cookie
///   answers never do;
/// - no token is 401; neither X-Requested-With nor a JSON content type, or a foreign Origin, is 403 (a non-empty text/plain
///   refresh body is already refused with 415 by model binding);
/// - logout signs out this device only (cookie, else body token), with or without access token, and is 200 without anything;
/// - /health/deep accepts a JWT bearer token.
/// Test users carry the INTEGRATION_TEST_ prefix and are the only rows cleaned up.
/// </summary>

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Security;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Authentication;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class RefreshTokenCookieHttpTests
{
    private const string TestUserPrefix = "INTEGRATION_TEST_COOKIE_";
    private const string Password = "Integration#Cookie2026!xY";
    private const string LoginPath = "/api/backend/Accounts/LoginUser";
    private const string RefreshPath = "/api/backend/Accounts/RefreshToken";
    private const string LogoutPath = "/api/backend/Accounts/Logout";
    private const string HttpsBase = "https://localhost";
    private const string HttpBase = "http://localhost";
    private const string ForeignOrigin = "https://evil.example.org";
    private const string RefreshTokenJsonProperty = "refreshToken";
    private const string TokenJsonProperty = "token";
    private const string JsonMediaType = "application/json";
    private const string TextPlainMediaType = "text/plain";
    private const string HealthDeepPath = "/health/deep";
    private const int RepeatedRefreshCount = 3;
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

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
        var user = new AppUser { UserName = userName, Email = _email, EmailConfirmed = true, FirstName = "Cookie", LastName = "Test" };
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
    public async Task Login_OverHttps_SetsTheCookieAndKeepsTheRefreshTokenOutOfTheBody()
    {
        using var client = CreateClient(HttpsBase);

        using var response = await client.PostAsync(LoginPath, Json(new { email = _email, password = Password }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = RefreshCookie(response);
        cookie.Value.ToString().ShouldNotBeNullOrEmpty();
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        cookie.Path.ToString().ShouldBe(RefreshTokenCookieConstants.CookiePath);
        cookie.SameSite.ShouldBe(Microsoft.Net.Http.Headers.SameSiteMode.Strict);
        cookie.MaxAge.ShouldNotBeNull();
        cookie.Domain.HasValue.ShouldBeFalse();

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(cookie.Value.ToString());
        using var json = JsonDocument.Parse(body);
        json.RootElement.TryGetProperty(RefreshTokenJsonProperty, out _).ShouldBeFalse();
        json.RootElement.GetProperty(TokenJsonProperty).GetString().ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task Login_OverPlainHttpToLocalhost_OmitsSecure()
    {
        using var client = CreateClient(HttpBase);

        using var response = await client.PostAsync(LoginPath, Json(new { email = _email, password = Password }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookie = RefreshCookie(response);
        cookie.Secure.ShouldBeFalse();
        cookie.HttpOnly.ShouldBeTrue();
    }

    [Test]
    public async Task Refresh_FromTheCookieWithEmptyBody_KeepsTheToken_RepeatedRefreshesWork_AndRenewMaxAge()
    {
        var (_, cookie) = await LoginAsync();

        for (var round = 0; round < RepeatedRefreshCount; round++)
        {
            using var refresh = await PostAsync(RefreshPath, content: null, cookie: cookie, requestedWith: true);

            refresh.StatusCode.ShouldBe(HttpStatusCode.OK);
            var renewed = RefreshCookie(refresh);
            Uri.UnescapeDataString(renewed.Value.ToString()).ShouldBe(Uri.UnescapeDataString(cookie));
            renewed.MaxAge.ShouldNotBeNull();
            using var json = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
            json.RootElement.TryGetProperty(RefreshTokenJsonProperty, out _).ShouldBeFalse();
        }
    }

    [Test]
    public async Task Refresh_WithARevokedCookie_Is401AndLeavesTheCookieAlone()
    {
        var (_, cookie) = await LoginAsync();
        await RevokeAsync(Uri.UnescapeDataString(cookie));

        using var response = await PostAsync(RefreshPath, content: null, cookie: cookie, requestedWith: true);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Contains(HeaderNames.SetCookie).ShouldBeFalse();
    }

    [Test]
    public async Task Refresh_FromTheCookieWithAnEmptyJsonObject_Works()
    {
        var (_, cookie) = await LoginAsync();

        using var response = await PostAsync(RefreshPath, Json(new { }), cookie: cookie, requestedWith: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Uri.UnescapeDataString(RefreshCookie(response).Value.ToString()).ShouldBe(Uri.UnescapeDataString(cookie));
    }

    [Test]
    public async Task Refresh_OverHttpBehindATlsProxy_KeepsSecure()
    {
        var (_, cookie) = await LoginAsync();

        using var response = await PostAsync(
            RefreshPath, content: null, cookie: cookie, requestedWith: true, baseAddress: HttpBase, forwardedProto: "https");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        RefreshCookie(response).Secure.ShouldBeTrue();
    }

    [Test]
    public async Task Refresh_WithTheTokenInTheBody_WithoutHeader_MigratesToANewCookieToken()
    {
        var legacyToken = await IssueRefreshTokenAsync();

        using var response = await PostAsync(
            RefreshPath, Json(new { refreshToken = legacyToken }), cookie: null, requestedWith: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookieToken = Uri.UnescapeDataString(RefreshCookie(response).Value.ToString());
        cookieToken.ShouldNotBe(legacyToken);
        (await IsValidAsync(cookieToken)).ShouldBeTrue();
        (await IsValidAsync(legacyToken)).ShouldBeTrue();
        (await ExpiryOfAsync(legacyToken))!.Value.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.Add(RefreshTokenLifetime.LegacyTokenGrace));
    }

    [Test]
    public async Task Refresh_OldClientShape_GetsTheNewTokenInBodyAndCookie_AndCanRefreshWithItAgain()
    {
        var legacyToken = await IssueRefreshTokenAsync();

        using var first = await PostAsync(
            RefreshPath, Json(new { refreshToken = legacyToken }), cookie: null, requestedWith: false);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cookieToken = Uri.UnescapeDataString(RefreshCookie(first).Value.ToString());
        string bodyToken;
        using (var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            bodyToken = json.RootElement.GetProperty(RefreshTokenJsonProperty).GetString()!;
        }

        bodyToken.ShouldBe(cookieToken);
        bodyToken.ShouldNotBe(legacyToken);

        using var second = await PostAsync(
            RefreshPath, Json(new { refreshToken = bodyToken }), cookie: null, requestedWith: false);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        secondJson.RootElement.GetProperty(RefreshTokenJsonProperty).GetString().ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task Refresh_TwoTabsMigratingWithTheSameBodyToken_BothSucceed()
    {
        var legacyToken = await IssueRefreshTokenAsync();

        using var first = await PostAsync(
            RefreshPath, Json(new { refreshToken = legacyToken }), cookie: null, requestedWith: false);
        using var second = await PostAsync(
            RefreshPath, Json(new { refreshToken = legacyToken }), cookie: null, requestedWith: false);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstToken = Uri.UnescapeDataString(RefreshCookie(first).Value.ToString());
        var secondToken = Uri.UnescapeDataString(RefreshCookie(second).Value.ToString());
        (await IsValidAsync(firstToken)).ShouldBeTrue();
        (await IsValidAsync(secondToken)).ShouldBeTrue();
    }

    [Test]
    public async Task Refresh_WithTheCookieAndARevokedBodyToken_UsesTheCookie()
    {
        var staleBodyToken = await IssueRefreshTokenAsync();
        await RevokeAsync(staleBodyToken);
        var (_, cookie) = await LoginAsync();

        using var response = await PostAsync(
            RefreshPath, Json(new { refreshToken = staleBodyToken }), cookie: cookie, requestedWith: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Uri.UnescapeDataString(RefreshCookie(response).Value.ToString()).ShouldBe(Uri.UnescapeDataString(cookie));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.TryGetProperty(RefreshTokenJsonProperty, out _).ShouldBeFalse();
    }

    [Test]
    public async Task Refresh_WithoutBodyAndCookie_Is401()
    {
        using var response = await PostAsync(RefreshPath, content: null, cookie: null, requestedWith: true);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Refresh_FromTheCookieWithoutRequestedWithHeader_Is403()
    {
        var (_, cookie) = await LoginAsync();

        using var response = await PostAsync(RefreshPath, content: null, cookie: cookie, requestedWith: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Refresh_WithAGarbageTextPlainBodyAndTheCookie_WithoutHeader_IsRejectedBeforeTheAction()
    {
        var (_, cookie) = await LoginAsync();
        using var garbage = new StringContent("{\"refreshToken\":\"garbage\"}", Encoding.UTF8, TextPlainMediaType);

        using var response = await PostAsync(RefreshPath, garbage, cookie: cookie, requestedWith: false);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        response.Headers.Contains(HeaderNames.SetCookie).ShouldBeFalse();
    }

    [Test]
    public async Task Refresh_WithAnEmptyTextPlainBodyAndTheCookie_WithoutHeader_Is403AndRenewsNothing()
    {
        var (_, cookie) = await LoginAsync();
        using var empty = new StringContent(string.Empty, Encoding.UTF8, TextPlainMediaType);

        using var response = await PostAsync(RefreshPath, empty, cookie: cookie, requestedWith: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Contains(HeaderNames.SetCookie).ShouldBeFalse();
    }

    [Test]
    public async Task Logout_WithTextPlainAndTheCookie_WithoutHeader_Is403AndRevokesNothing()
    {
        var (_, cookie) = await LoginAsync();
        using var form = new StringContent(string.Empty, Encoding.UTF8, TextPlainMediaType);

        using var response = await PostAsync(LogoutPath, form, cookie: cookie, requestedWith: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await IsValidAsync(Uri.UnescapeDataString(cookie))).ShouldBeTrue();
    }

    [Test]
    public async Task Refresh_WithAForeignOrigin_Is403()
    {
        var (_, cookie) = await LoginAsync();

        using var response = await PostAsync(RefreshPath, content: null, cookie: cookie, requestedWith: true, origin: ForeignOrigin);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Refresh_WithTheWhitelistedOrigin_Works()
    {
        var (_, cookie) = await LoginAsync();

        using var response = await PostAsync(
            RefreshPath, content: null, cookie: cookie, requestedWith: true, origin: RefreshTokenCookieTestWebApplicationFactory.WhitelistedOrigin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Logout_WithAccessToken_RevokesOnlyThisDevice_AndDeletesTheCookie()
    {
        var (accessToken, cookie) = await LoginAsync();
        var otherSession = await IssueRefreshTokenAsync();

        using var response = await PostAsync(LogoutPath, content: null, cookie: cookie, requestedWith: true, bearer: accessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertCookieDeleted(response);
        (await IsValidAsync(Uri.UnescapeDataString(cookie))).ShouldBeFalse();
        (await IsValidAsync(otherSession)).ShouldBeTrue();
    }

    [Test]
    public async Task Logout_WithoutCookie_RevokesTheBodyToken()
    {
        var legacyToken = await IssueRefreshTokenAsync();

        using var response = await PostAsync(LogoutPath, Json(new { refreshToken = legacyToken }), cookie: null, requestedWith: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IsValidAsync(legacyToken)).ShouldBeFalse();
    }

    [Test]
    public async Task HealthDeep_WithAccessToken_IsNotUnauthorized_AndWithoutIsUnauthorized()
    {
        var (accessToken, _) = await LoginAsync();
        using var client = CreateClient(HttpsBase);

        using var anonymous = await client.GetAsync(HealthDeepPath);
        using var request = new HttpRequestMessage(HttpMethod.Get, HealthDeepPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var authenticated = await client.SendAsync(request);

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        authenticated.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        authenticated.StatusCode.ShouldNotBe(HttpStatusCode.Redirect);
    }

    [Test]
    public async Task Logout_WithoutAccessToken_RevokesOnlyTheCookieSession_AndDeletesTheCookie()
    {
        var (_, cookie) = await LoginAsync();
        var otherSession = await IssueRefreshTokenAsync();

        using var response = await PostAsync(LogoutPath, content: null, cookie: cookie, requestedWith: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertCookieDeleted(response);
        (await IsValidAsync(Uri.UnescapeDataString(cookie))).ShouldBeFalse();
        (await IsValidAsync(otherSession)).ShouldBeTrue();
    }

    [Test]
    public async Task Logout_WithAnInvalidAccessToken_ActsLikeWithoutOne()
    {
        var (_, cookie) = await LoginAsync();
        var otherSession = await IssueRefreshTokenAsync();

        using var response = await PostAsync(LogoutPath, content: null, cookie: cookie, requestedWith: true, bearer: "not-a-jwt");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IsValidAsync(Uri.UnescapeDataString(cookie))).ShouldBeFalse();
        (await IsValidAsync(otherSession)).ShouldBeTrue();
    }

    [Test]
    public async Task Logout_WithNothing_Is200()
    {
        using var response = await PostAsync(LogoutPath, content: null, cookie: null, requestedWith: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertCookieDeleted(response);
    }

    [Test]
    public async Task Logout_WithoutRequestedWithHeader_Is403()
    {
        using var response = await PostAsync(LogoutPath, content: null, cookie: null, requestedWith: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private HttpClient CreateClient(string baseAddress) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(baseAddress),
        HandleCookies = false,
        AllowAutoRedirect = false,
    });

    private async Task<(string AccessToken, string Cookie)> LoginAsync()
    {
        using var client = CreateClient(HttpsBase);
        using var response = await client.PostAsync(LoginPath, Json(new { email = _email, password = Password }));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cookie = RefreshCookie(response).Value.ToString();
        (await IsValidAsync(Uri.UnescapeDataString(cookie))).ShouldBeTrue("the issued cookie must hold a stored refresh token");
        return (json.RootElement.GetProperty(TokenJsonProperty).GetString()!, cookie);
    }

    private async Task<HttpResponseMessage> PostAsync(
        string path,
        HttpContent? content,
        string? cookie,
        bool requestedWith,
        string? origin = null,
        string? bearer = null,
        string baseAddress = HttpsBase,
        string? forwardedProto = null)
    {
        using var client = CreateClient(baseAddress);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (cookie != null)
        {
            request.Headers.Add(HeaderNames.Cookie, $"{RefreshTokenCookieConstants.CookieName}={cookie}");
        }

        if (requestedWith)
        {
            request.Headers.Add(CsrfProofConstants.RequestedWithHeader, CsrfProofConstants.RequestedWithValue);
        }

        if (origin != null)
        {
            request.Headers.Add(HeaderNames.Origin, origin);
        }

        if (forwardedProto != null)
        {
            request.Headers.Add(ForwardedProtoHeader, forwardedProto);
        }

        if (bearer != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request);
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, JsonMediaType);

    private static SetCookieHeaderValue RefreshCookie(HttpResponseMessage response)
    {
        response.Headers.TryGetValues(HeaderNames.SetCookie, out var values).ShouldBeTrue("no Set-Cookie header");
        var cookies = SetCookieHeaderValue.ParseList(values!.ToList())
            .Where(c => c.Name.Equals(RefreshTokenCookieConstants.CookieName))
            .ToList();
        cookies.Count.ShouldBe(1);
        return cookies[0];
    }

    private static void AssertCookieDeleted(HttpResponseMessage response)
    {
        var cookie = RefreshCookie(response);
        cookie.Value.ToString().ShouldBeEmpty();
        cookie.Expires!.Value.ShouldBeLessThan(DateTimeOffset.UtcNow);
        cookie.Path.ToString().ShouldBe(RefreshTokenCookieConstants.CookiePath);
    }

    private async Task<string> IssueRefreshTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().CreateRefreshTokenAsync(_userId);
    }

    private async Task<bool> IsValidAsync(string refreshToken)
    {
        var expiry = await ExpiryOfAsync(refreshToken);
        return expiry > DateTime.UtcNow;
    }

    private async Task<DateTime?> ExpiryOfAsync(string refreshToken)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var hashed = RefreshTokenHasher.Hash(refreshToken);
        return await context.RefreshToken
            .Where(rt => rt.AspNetUsersId == _userId && rt.Token == hashed)
            .Select(rt => (DateTime?)rt.ExpiryDate)
            .SingleOrDefaultAsync();
    }

    private async Task RevokeAsync(string refreshToken)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().RemoveRefreshTokenAsync(refreshToken);
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
