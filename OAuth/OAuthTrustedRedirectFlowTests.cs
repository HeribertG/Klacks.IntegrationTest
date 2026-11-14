// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Presentation.Mcp;
using Klacks.IntegrationTest.Mcp;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.OAuth;

/// <summary>
/// Proves the OAuth server against the real request pipeline and Npgsql: registration drops targets the installation
/// does not trust, the consent page names the list entry and host instead of the registrant's name, the authorization
/// code binds the access mode chosen on the consent form, the token exchange records the origin on the personal access
/// token, and switching the connection mode off stops OAuth-issued tokens at the MCP endpoint. An old registration
/// that still carries a foreign redirect target must be refused at authorization.
/// </summary>
[TestFixture]
[NonParallelizable]
public class OAuthTrustedRedirectFlowTests
{
    private const string TestUserPrefix = "INTEGRATION_TEST_oauthflow_";
    private const string SupervisorPassword = "Itest-P@ssw0rt-1x";
    private const string ClaudeRedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string EvilRedirectUri = "https://evil.example.org/callback";
    private const string LoopbackRedirectUri = "http://127.0.0.1:33418/callback";
    private const string AttackerChosenName = "Klacks Official";
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string McpAcceptHeader = "application/json, text/event-stream";
    private const string ToolCallBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"reopen_period\",\"arguments\":{}}}";
    private const string ConsentTokenField = "__RequestVerificationToken";
    private const string DecisionApprove = "approve";
    private const string DecisionInvalid = "maybe";

    private static readonly Regex AntiforgeryFieldPattern = new(
        "name=\"" + ConsentTokenField + "\" value=\"(?<token>[^\"]+)\"",
        RegexOptions.CultureInvariant);

    private OAuthFlowTestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private McpCallRecorder _recorder = null!;
    private AppUser _supervisor = null!;
    private readonly List<string> _createdClientIds = [];
    private int _requestCounter;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new OAuthFlowTestWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        _recorder = _factory.Services.GetRequiredService<McpCallRecorder>();

        await DeleteTestDataAsync();
        _supervisor = await CreateSupervisorAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await DeleteTestDataAsync();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _factory.Overrides.Clear();
        _recorder.Reset();
    }

    [Test]
    public async Task Register_OnlyForeignTarget_IsRejectedNamingTheHost()
    {
        using var response = await RegisterAsync([EvilRedirectUri], AttackerChosenName);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain(OAuthConstants.ErrorInvalidRedirectUri);
        body.ShouldContain("https://evil.example.org");
    }

    [Test]
    public async Task Register_TrustedAndForeignTargets_KeepsTheTrustedOneAndNamesItAfterTheList()
    {
        using var response = await RegisterAsync([EvilRedirectUri, ClaudeRedirectUri, "cursor://anysphere.cursor-retrieval/oauth"], AttackerChosenName);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _createdClientIds.Add(document.RootElement.GetProperty("client_id").GetString()!);
        document.RootElement.GetProperty("redirect_uris").EnumerateArray().Select(e => e.GetString()).ShouldBe([ClaudeRedirectUri]);
        document.RootElement.GetProperty("client_name").GetString().ShouldBe("Claude");
    }

    [Test]
    public async Task Flow_DefaultsToReadOnly_ShowsListNameAndHost_AndRecordsTheOriginOnTheToken()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, AttackerChosenName);

        var page = await GetConsentPageAsync(clientId, ClaudeRedirectUri);
        page.Html.ShouldContain("Claude");
        page.Html.ShouldContain("claude.ai");
        page.Html.ShouldNotContain(AttackerChosenName);

        var code = await ApproveAsync(page, clientId, ClaudeRedirectUri, accessMode: null);
        var token = await ExchangeAsync(clientId, code, ClaudeRedirectUri);

        token.GetProperty("scope").GetString().ShouldBe(OAuthConstants.McpToolsReadScope);

        var row = await LoadIssuedTokenAsync();
        row.IsOAuthIssued.ShouldBeTrue();
        row.Name.ShouldBe(OAuthConstants.AccessTokenNamePrefix + "Claude");
        row.OAuthToolName.ShouldBe("Claude");
        row.OAuthRedirectHost.ShouldBe("claude.ai");
        row.AccessMode.ShouldBe(PersonalAccessTokenAccessMode.Read);

        (await CallMcpAsync(token.GetProperty("access_token").GetString()!)).ShouldBe(HttpStatusCode.OK);
        McpAccessModeResolver.Resolve(_recorder.HandlerUser).ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public async Task Flow_WriteChosenOnTheForm_BindsWriteToCodeAndToken()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, AttackerChosenName);
        var page = await GetConsentPageAsync(clientId, ClaudeRedirectUri);

        var code = await ApproveAsync(page, clientId, ClaudeRedirectUri, OAuthConstants.AccessModeWrite);
        var token = await ExchangeAsync(clientId, code, ClaudeRedirectUri);

        token.GetProperty("scope").GetString().ShouldBe(OAuthConstants.McpToolsScope);
        (await LoadIssuedTokenAsync()).AccessMode.ShouldBe(PersonalAccessTokenAccessMode.Write);
        (await CallMcpAsync(token.GetProperty("access_token").GetString()!)).ShouldBe(HttpStatusCode.OK);
        McpAccessModeResolver.Resolve(_recorder.HandlerUser).ShouldBe(PersonalAccessTokenAccessMode.Write);
    }

    [Test]
    public async Task Flow_WriteSwitchOff_WriteChoiceIsIgnoredAndTheTokenIsReadOnly()
    {
        _factory.Overrides.Set(SettingKeys.OAuthAllowWrite, "false");
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);
        var page = await GetConsentPageAsync(clientId, ClaudeRedirectUri);

        var code = await ApproveAsync(page, clientId, ClaudeRedirectUri, OAuthConstants.AccessModeWrite);
        await ExchangeAsync(clientId, code, ClaudeRedirectUri);

        (await LoadIssuedTokenAsync()).AccessMode.ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public async Task Flow_WriteSwitchTurnedOffBetweenCodeAndToken_ExchangeAnswersInvalidGrant()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);
        var page = await GetConsentPageAsync(clientId, ClaudeRedirectUri);
        var code = await ApproveAsync(page, clientId, ClaudeRedirectUri, OAuthConstants.AccessModeWrite);

        _factory.Overrides.Set(SettingKeys.OAuthAllowWrite, "false");
        using var response = await _client.PostAsync(
            "/oauth/token",
            new FormUrlEncodedContent(TokenForm(clientId, code, ClaudeRedirectUri)),
            CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(OAuthConstants.ErrorInvalidGrant);
    }

    [Test]
    public async Task ConsentPage_SendsTheSecurityHeadersOnce_EvenThoughTheAntiforgeryServiceWritesItsOwn()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);

        using var response = await _client.SendAsync(WithIp(HttpMethod.Get, AuthorizeUrl(clientId, ClaudeRedirectUri)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["frame-ancestors 'none'"]);
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.CacheControl.NoCache.ShouldBeFalse();
    }

    [Test]
    public async Task ConsentPage_OffersReadPreselectedAndWriteOnlyWhileTheSwitchIsOn()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);

        var withSwitch = await GetConsentPageAsync(clientId, ClaudeRedirectUri);
        _factory.Overrides.Set(SettingKeys.OAuthAllowWrite, "false");
        var withoutSwitch = await GetConsentPageAsync(clientId, ClaudeRedirectUri);

        withSwitch.Html.ShouldContain("name=\"access_mode\" value=\"read\" checked");
        withSwitch.Html.ShouldContain("name=\"access_mode\" value=\"write\"");
        withoutSwitch.Html.ShouldContain("name=\"access_mode\" value=\"read\" checked");
        withoutSwitch.Html.ShouldNotContain("value=\"write\"");
    }

    [Test]
    public async Task ConsentPage_FollowsTheAcceptLanguageHeader_AndFallsBackToEnglish()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);

        _factory.Overrides.Set(SettingKeys.DefaultLanguage, "fr");

        using var german = await _client.SendAsync(WithLanguage("de-CH,de;q=0.9,en;q=0.5", clientId));
        using var unknownBrowser = await _client.SendAsync(WithLanguage("xx", clientId));
        _factory.Overrides.Set(SettingKeys.DefaultLanguage, "xx");
        using var unknownEverything = await _client.SendAsync(WithLanguage("yy", clientId));

        var germanHtml = await german.Content.ReadAsStringAsync();
        germanHtml.ShouldContain("<html lang=\"de\" dir=\"ltr\">");
        germanHtml.ShouldContain("Externes KI-Werkzeug mit Klacks verbinden");
        (await unknownBrowser.Content.ReadAsStringAsync()).ShouldContain("<html lang=\"fr\" dir=\"ltr\">");
        var english = await unknownEverything.Content.ReadAsStringAsync();
        english.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
        english.ShouldContain("Connect an external AI tool to Klacks");
    }

    [Test]
    public async Task ConsentPage_ArabicBrowser_GetsARightToLeftPageWhileTheHostStaysLeftToRight()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);

        using var response = await _client.SendAsync(WithLanguage("ar", clientId));

        var html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("<html lang=\"ar\" dir=\"rtl\">");
        html.ShouldContain("<div class=\"toolhost\" dir=\"ltr\">claude.ai</div>");
    }

    [Test]
    public async Task ConsentPage_ThirtyDayLifetimeOutsideThePlayground()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);

        using var response = await _client.SendAsync(WithLanguage("en", clientId));

        (await response.Content.ReadAsStringAsync()).ShouldContain("valid for 30 days");
    }

    [Test]
    public async Task Flow_LoopbackProgram_ShowsProgramOnThisComputerWithHostAndPort()
    {
        var clientId = await RegisterClientAsync(LoopbackRedirectUri, AttackerChosenName);

        var page = await GetConsentPageAsync(clientId, LoopbackRedirectUri);

        page.Html.ShouldContain("Program on this computer");
        page.Html.ShouldContain("127.0.0.1:33418");
        page.Html.ShouldNotContain(AttackerChosenName);
    }

    [Test]
    public async Task Flow_LocalProgramsSwitchOff_RegistrationOfALoopbackProgramIsRefused()
    {
        _factory.Overrides.Set(SettingKeys.OAuthAllowLoopback, "false");

        using var response = await RegisterAsync([LoopbackRedirectUri], null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Authorize_OldRegistrationWithForeignTarget_IsRefusedOnGetAndPostWithoutRedirect()
    {
        var clientId = await InsertOldClientAsync(EvilRedirectUri);
        var trustedClientId = await RegisterClientAsync(ClaudeRedirectUri, null);
        var trustedPage = await GetConsentPageAsync(trustedClientId, ClaudeRedirectUri);

        using var get = await _client.SendAsync(WithIp(HttpMethod.Get, AuthorizeUrl(clientId, EvilRedirectUri)));
        get.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        get.Headers.Location.ShouldBeNull();

        var form = ConsentForm(trustedPage, clientId, EvilRedirectUri, DecisionApprove, null);
        using var post = await _client.SendAsync(WithIp(HttpMethod.Post, "/oauth/authorize", new FormUrlEncodedContent(form)));
        post.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        post.Headers.Location.ShouldBeNull();
    }

    [Test]
    public async Task Authorize_OldRegistrationWithForeignTargetAndNoRedirectParameter_IsRefused()
    {
        var clientId = await InsertOldClientAsync(EvilRedirectUri);

        using var get = await _client.SendAsync(WithIp(HttpMethod.Get, AuthorizeUrl(clientId, null)));

        get.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        get.Headers.Location.ShouldBeNull();
    }

    [Test]
    public async Task Authorize_DecisionOtherThanApproveOrDeny_IsRefusedAndIssuesNoCode()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);
        var page = await GetConsentPageAsync(clientId, ClaudeRedirectUri);
        var form = ConsentForm(page, clientId, ClaudeRedirectUri, DecisionInvalid, null);

        using var response = await _client.SendAsync(WithIp(HttpMethod.Post, "/oauth/authorize", new FormUrlEncodedContent(form)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
    }

    [Test]
    public async Task Authorize_PostWithoutAntiforgeryToken_IsRefused()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);
        var form = new Dictionary<string, string>
        {
            [OAuthConstants.ParameterEmail] = _supervisor.Email!,
            [OAuthConstants.ParameterPassword] = SupervisorPassword,
            [OAuthConstants.ParameterDecision] = DecisionApprove,
            [OAuthConstants.ParameterClientId] = clientId,
            [OAuthConstants.ParameterRedirectUri] = ClaudeRedirectUri,
            [OAuthConstants.ParameterResponseType] = OAuthConstants.ResponseTypeCode,
            [OAuthConstants.ParameterCodeChallenge] = Challenge(),
            [OAuthConstants.ParameterCodeChallengeMethod] = OAuthConstants.CodeChallengeMethodS256
        };

        using var response = await _client.SendAsync(WithIp(HttpMethod.Post, "/oauth/authorize", new FormUrlEncodedContent(form)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ModeOff_RefusesNewRegistrationsAndStopsIssuedOAuthTokensAtTheMcpEndpoint()
    {
        var clientId = await RegisterClientAsync(ClaudeRedirectUri, null);
        var page = await GetConsentPageAsync(clientId, ClaudeRedirectUri);
        var code = await ApproveAsync(page, clientId, ClaudeRedirectUri, null);
        var accessToken = (await ExchangeAsync(clientId, code, ClaudeRedirectUri)).GetProperty("access_token").GetString()!;
        (await CallMcpAsync(accessToken)).ShouldBe(HttpStatusCode.OK);

        _factory.Overrides.Set(SettingKeys.OAuthConnectionsMode, "off");

        (await CallMcpAsync(accessToken)).ShouldBe(HttpStatusCode.Unauthorized);
        using var registration = await RegisterAsync([ClaudeRedirectUri], null);
        registration.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await registration.Content.ReadAsStringAsync()).ShouldContain(OAuthConstants.ErrorAccessDenied);

        _factory.Overrides.Set(SettingKeys.OAuthConnectionsMode, "trusted");
        (await CallMcpAsync(accessToken)).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task ModeOff_DoesNotStopAManuallyCreatedPersonalAccessToken()
    {
        var manual = await CreateManualTokenAsync();
        _factory.Overrides.Set(SettingKeys.OAuthConnectionsMode, "off");

        (await CallMcpAsync(manual)).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Schema_HasTheOriginColumnsAndTheAccessModeColumn()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();

        var columns = await context.Database
            .SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'personal_access_tokens' AND column_name IN ('is_o_auth_issued','o_auth_tool_name','o_auth_redirect_host')")
            .ToListAsync();
        var codeColumns = await context.Database
            .SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'oauth_authorization_codes' ORDER BY column_name")
            .ToListAsync();

        columns.Count.ShouldBe(3);
        codeColumns.ShouldContain("access_mode");
        codeColumns.ShouldNotContain("client_name");
        codeColumns.ShouldNotContain("scope");
    }

    private async Task<HttpResponseMessage> RegisterAsync(string[] redirectUris, string? clientName)
    {
        var body = new Dictionary<string, object?>
        {
            ["redirect_uris"] = redirectUris,
            ["token_endpoint_auth_method"] = OAuthConstants.TokenEndpointAuthMethodNone
        };
        if (clientName != null)
        {
            body["client_name"] = clientName;
        }

        using var request = WithIp(HttpMethod.Post, "/oauth/register", JsonContent.Create(body));
        return await _client.SendAsync(request);
    }

    private async Task<string> RegisterClientAsync(string redirectUri, string? clientName)
    {
        using var response = await RegisterAsync([redirectUri], clientName);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        var clientId = document.RootElement.GetProperty("client_id").GetString()!;
        _createdClientIds.Add(clientId);
        return clientId;
    }

    private async Task<string> InsertOldClientAsync(string redirectUri)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOAuthClientRepository>();
        var clientId = Guid.NewGuid().ToString("N");
        await repository.AddAsync(new OAuthClient
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            RedirectUrisJson = JsonSerializer.Serialize(new[] { redirectUri })
        });
        _createdClientIds.Add(clientId);
        return clientId;
    }

    private static string Challenge()
    {
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(CodeVerifier)));
    }

    private static string AuthorizeUrl(string clientId, string? redirectUri)
    {
        var url = $"/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&response_type=code" +
                  $"&code_challenge={Challenge()}&code_challenge_method=S256&state=xyz";
        return redirectUri == null ? url : url + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
    }

    private HttpRequestMessage WithLanguage(string acceptLanguage, string clientId)
    {
        var request = WithIp(HttpMethod.Get, AuthorizeUrl(clientId, ClaudeRedirectUri));
        request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        return request;
    }

    private async Task<ConsentPage> GetConsentPageAsync(string clientId, string redirectUri)
    {
        using var response = await _client.SendAsync(WithIp(HttpMethod.Get, AuthorizeUrl(clientId, redirectUri)));
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, html);

        var match = AntiforgeryFieldPattern.Match(html);
        match.Success.ShouldBeTrue("the consent page carries no anti-forgery token");
        return new ConsentPage(html, WebUtility.HtmlDecode(match.Groups["token"].Value));
    }

    private Dictionary<string, string> ConsentForm(ConsentPage page, string clientId, string redirectUri, string decision, string? accessMode)
    {
        var form = new Dictionary<string, string>
        {
            [ConsentTokenField] = page.AntiforgeryToken,
            [OAuthConstants.ParameterEmail] = _supervisor.Email!,
            [OAuthConstants.ParameterPassword] = SupervisorPassword,
            [OAuthConstants.ParameterDecision] = decision,
            [OAuthConstants.ParameterClientId] = clientId,
            [OAuthConstants.ParameterRedirectUri] = redirectUri,
            [OAuthConstants.ParameterResponseType] = OAuthConstants.ResponseTypeCode,
            [OAuthConstants.ParameterCodeChallenge] = Challenge(),
            [OAuthConstants.ParameterCodeChallengeMethod] = OAuthConstants.CodeChallengeMethodS256,
            [OAuthConstants.ParameterState] = "xyz"
        };
        if (accessMode != null)
        {
            form[OAuthConstants.ParameterAccessMode] = accessMode;
        }

        return form;
    }

    private async Task<string> ApproveAsync(ConsentPage page, string clientId, string redirectUri, string? accessMode)
    {
        var form = ConsentForm(page, clientId, redirectUri, DecisionApprove, accessMode);

        using var response = await _client.SendAsync(WithIp(HttpMethod.Post, "/oauth/authorize", new FormUrlEncodedContent(form)));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        var location = response.Headers.Location!.ToString();
        location.ShouldStartWith(redirectUri);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(location).Query);
        query["state"].ShouldBe("xyz");
        return query["code"]!;
    }

    private static Dictionary<string, string> TokenForm(string clientId, string code, string redirectUri)
    {
        return new Dictionary<string, string>
        {
            [OAuthConstants.ParameterGrantType] = OAuthConstants.GrantTypeAuthorizationCode,
            [OAuthConstants.ParameterCode] = code,
            [OAuthConstants.ParameterClientId] = clientId,
            [OAuthConstants.ParameterRedirectUri] = redirectUri,
            [OAuthConstants.ParameterCodeVerifier] = CodeVerifier
        };
    }

    private async Task<JsonElement> ExchangeAsync(string clientId, string code, string redirectUri)
    {
        using var request = WithIp(HttpMethod.Post, "/oauth/token", new FormUrlEncodedContent(TokenForm(clientId, code, redirectUri)));
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpStatusCode> CallMcpAsync(string bearerToken)
    {
        using var request = WithIp(HttpMethod.Post, McpServerConstants.RoutePattern, new StringContent(ToolCallBody, Encoding.UTF8, MediaTypeNames.Application.Json));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.TryAddWithoutValidation("Accept", McpAcceptHeader);

        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    private HttpRequestMessage WithIp(HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        var counter = Interlocked.Increment(ref _requestCounter);
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.RemoteIpHeader, $"10.{counter / 65025 % 250}.{counter / 255 % 255}.{counter % 255 + 1}");
        return request;
    }

    private async Task<PersonalAccessToken> LoadIssuedTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        return await context.PersonalAccessTokens
            .AsNoTracking()
            .Where(token => token.UserId == _supervisor.Id && token.IsOAuthIssued)
            .OrderByDescending(token => token.CreateTime)
            .FirstAsync();
    }

    private async Task<string> CreateManualTokenAsync()
    {
        var (plaintext, hash, prefix) = Klacks.Api.Domain.Security.PatTokenGenerator.Generate();
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPersonalAccessTokenRepository>();
        await repository.AddAsync(new PersonalAccessToken
        {
            UserId = _supervisor.Id,
            Name = TestUserPrefix + "manual",
            TokenHash = hash,
            TokenPrefix = prefix,
            AccessMode = PersonalAccessTokenAccessMode.Read
        });
        return plaintext;
    }

    private async Task<AppUser> CreateSupervisorAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = TestUserPrefix + "supervisor",
            Email = TestUserPrefix + "supervisor@example.invalid",
            FirstName = TestUserPrefix + "supervisor",
            LastName = TestUserPrefix + "supervisor"
        };

        var created = await userManager.CreateAsync(user, SupervisorPassword);
        created.Succeeded.ShouldBeTrue(string.Join("; ", created.Errors.Select(error => error.Description)));

        var roleAdded = await userManager.AddToRoleAsync(user, Roles.Authorised);
        roleAdded.Succeeded.ShouldBeTrue(string.Join("; ", roleAdded.Errors.Select(error => error.Description)));

        return user;
    }

    private async Task DeleteTestDataAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var users = await userManager.Users
            .Where(user => user.UserName!.StartsWith(TestUserPrefix))
            .ToListAsync();

        foreach (var user in users)
        {
            await context.OAuthAuthorizationCodes
                .Where(row => row.UserId == user.Id)
                .ExecuteDeleteAsync();

            await context.Set<PersonalAccessToken>()
                .IgnoreQueryFilters()
                .Where(token => token.UserId == user.Id)
                .ExecuteDeleteAsync();

            await userManager.DeleteAsync(user);
        }

        var clientIds = _createdClientIds.ToList();
        await context.OAuthClients
            .IgnoreQueryFilters()
            .Where(client => clientIds.Contains(client.ClientId))
            .ExecuteDeleteAsync();
    }

    private sealed record ConsentPage(string Html, string AntiforgeryToken);
}
