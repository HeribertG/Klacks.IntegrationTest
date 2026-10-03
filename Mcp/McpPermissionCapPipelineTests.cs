// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Security;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Presentation.Mcp;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Mcp;

/// <summary>
/// Proves the MCP rights ceiling against the real request pipeline: an Admin caller (login JWT or personal
/// access token) must reach a tool call - and every in-process handler that reads HttpContext.User - as a
/// Supervisor (Authorised), never as Admin. The cap used to sit between UseAuthentication and UseAuthorization,
/// where the principal is still the anonymous cookie default; the authorization middleware then replaced it
/// with the uncapped principal of the /mcp policy schemes. A unit test with a preset principal cannot see that.
/// </summary>
[TestFixture]
[NonParallelizable]
public class McpPermissionCapPipelineTests
{
    private const string TestUserPrefix = "INTEGRATION_TEST_mcpcap_";
    private const string McpAcceptHeader = "application/json, text/event-stream";
    private const string ToolCallBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"reopen_period\",\"arguments\":{}}}";

    private McpCapTestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private McpCallRecorder _recorder = null!;
    private AppUser _adminUser = null!;
    private AppUser _supervisorUser = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new McpCapTestWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        _recorder = _factory.Services.GetRequiredService<McpCallRecorder>();

        await DeleteTestUsersAsync();
        _adminUser = await CreateUserAsync("admin", Roles.Admin);
        _supervisorUser = await CreateUserAsync("supervisor", Roles.Authorised);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await DeleteTestUsersAsync();
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _recorder.Reset();
    }

    [Test]
    public async Task AdminJwt_ToolCall_RunsAsSupervisorInHttpContextAndHandler()
    {
        var jwt = MintJwt(_adminUser, Roles.Admin);

        await CallToolAsync(jwt);

        AssertCappedToSupervisor();
        McpAccessModeResolver.Resolve(_recorder.HandlerUser).ShouldBe(PersonalAccessTokenAccessMode.Write);
    }

    [Test]
    public async Task AdminWritePat_ToolCall_RunsAsSupervisorAndKeepsWriteMode()
    {
        var pat = await CreatePatAsync(_adminUser, PersonalAccessTokenAccessMode.Write);

        await CallToolAsync(pat);

        AssertCappedToSupervisor();
        McpAccessModeResolver.Resolve(_recorder.HttpContextUser).ShouldBe(PersonalAccessTokenAccessMode.Write);
        McpAccessModeResolver.Resolve(_recorder.HandlerUser).ShouldBe(PersonalAccessTokenAccessMode.Write);
    }

    [Test]
    public async Task AdminReadPat_ToolCall_RunsAsSupervisorAndKeepsReadMode()
    {
        var pat = await CreatePatAsync(_adminUser, PersonalAccessTokenAccessMode.Read);

        await CallToolAsync(pat);

        AssertCappedToSupervisor();
        McpAccessModeResolver.Resolve(_recorder.HttpContextUser).ShouldBe(PersonalAccessTokenAccessMode.Read);
        McpAccessModeResolver.Resolve(_recorder.HandlerUser).ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public async Task SupervisorJwt_ToolCall_StaysSupervisor()
    {
        var jwt = MintJwt(_supervisorUser, Roles.Authorised);

        await CallToolAsync(jwt);

        AssertCappedToSupervisor();
    }

    private void AssertCappedToSupervisor()
    {
        _recorder.CallCount.ShouldBe(1, "the MCP tool-call handler was not reached");
        _recorder.HttpContextAvailable.ShouldBeTrue(
            "without an HttpContext in-process handlers see no user at all and the cap would be moot");

        var httpUser = _recorder.HttpContextUser.ShouldNotBeNull();
        httpUser.Identity?.IsAuthenticated.ShouldBe(true);
        httpUser.IsInRole(Roles.Admin).ShouldBeFalse("HttpContext.User still carries Admin over MCP");
        httpUser.IsInRole(Roles.Authorised).ShouldBeTrue();

        var handlerUser = _recorder.HandlerUser.ShouldNotBeNull();
        handlerUser.IsInRole(Roles.Admin).ShouldBeFalse("the principal handed to the MCP handler still carries Admin");
        handlerUser.IsInRole(Roles.Authorised).ShouldBeTrue();
    }

    private async Task CallToolAsync(string bearerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpServerConstants.RoutePattern)
        {
            Content = new StringContent(ToolCallBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.TryAddWithoutValidation("Accept", McpAcceptHeader);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
    }

    private string MintJwt(AppUser user, string role)
    {
        var settings = _factory.Services.GetRequiredService<JwtSettings>();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret));
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName!),
            new(ClaimTypes.Role, role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: settings.ValidIssuer,
            audience: settings.ValidAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<string> CreatePatAsync(AppUser owner, PersonalAccessTokenAccessMode accessMode)
    {
        var (plaintext, hash, prefix) = PatTokenGenerator.Generate();

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPersonalAccessTokenRepository>();
        await repository.AddAsync(new PersonalAccessToken
        {
            UserId = owner.Id,
            Name = TestUserPrefix + accessMode,
            TokenHash = hash,
            TokenPrefix = prefix,
            AccessMode = accessMode
        });

        return plaintext;
    }

    private async Task<AppUser> CreateUserAsync(string suffix, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = TestUserPrefix + suffix,
            Email = TestUserPrefix + suffix + "@example.invalid",
            FirstName = TestUserPrefix + suffix,
            LastName = TestUserPrefix + suffix
        };

        var created = await userManager.CreateAsync(user);
        created.Succeeded.ShouldBeTrue(string.Join("; ", created.Errors.Select(error => error.Description)));

        var roleAdded = await userManager.AddToRoleAsync(user, role);
        roleAdded.Succeeded.ShouldBeTrue(string.Join("; ", roleAdded.Errors.Select(error => error.Description)));

        return user;
    }

    private async Task DeleteTestUsersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var users = await userManager.Users
            .Where(user => user.UserName!.StartsWith(TestUserPrefix))
            .ToListAsync();

        foreach (var user in users)
        {
            await context.Set<PersonalAccessToken>()
                .IgnoreQueryFilters()
                .Where(token => token.UserId == user.Id)
                .ExecuteDeleteAsync();

            await userManager.DeleteAsync(user);
        }
    }
}
