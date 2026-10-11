// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Groups;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AnalyseScenarios;

/// <summary>
/// End-to-end check of the scenario visibility rule for a REAL group-restricted user against PostgreSQL (IT database
/// klacks): the visibility rows, groups and scenarios live in the database, and the whole chain UserService ->
/// GroupVisibilityService -> GroupVisibilityGuard -> ScenarioTokenAccessGuard runs unmocked; only the Identity user
/// manager is a stub answering "this user exists and holds no role". Covers the two paths without an HTTP request:
/// a skill invocation that carries its user only in SkillExecutionContext (Telegram, inbound, proactive), and by-id
/// schedule row access in a scenario of a hidden group.
///
/// Every row this fixture creates carries the INTEGRATION_TEST_ prefix and the cleanup deletes exactly those rows.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class RestrictedUserScenarioVisibilityIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_RESTRICTED_SCENARIO_";
    private const string SkillName = "read_schedule_state";
    private const string GenericHandlerType = "test_handler";
    private const string GenericHandlerConfig = "{\"endpoint\":\"test\"}";

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AppUser _user = null!;
    private AnalyseScenario _visibleScenario = null!;
    private AnalyseScenario _hiddenScenario = null!;
    private IGenericSkillDispatcher _dispatcher = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        await using var context = CreateContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = CreateContext();

        var visibleGroup = NewGroup("VISIBLE");
        var hiddenGroup = NewGroup("HIDDEN");
        _user = NewUser("PLANNER");
        _context.Group.AddRange(visibleGroup, hiddenGroup);
        _context.AppUser.Add(_user);
        await _context.SaveChangesAsync();

        _context.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = _user.Id, GroupId = visibleGroup.Id });
        _visibleScenario = NewScenario("VISIBLE", visibleGroup.Id);
        _hiddenScenario = NewScenario("HIDDEN", hiddenGroup.Id);
        _context.AnalyseScenarios.AddRange(_visibleScenario, _hiddenScenario);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task SkillWithoutHttpRequest_RunsAsTheContextUser_HiddenScenarioIsRefused_VisibleOneRuns()
    {
        var executor = BuildExecutor(out _);
        var userId = Guid.Parse(_user.Id);

        var hidden = await executor.ExecuteAsync(Invocation(_hiddenScenario.Token), Ctx(userId));
        var visible = await executor.ExecuteAsync(Invocation(_visibleScenario.Token), Ctx(userId));

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldBe($"No scenario found for analyseToken '{_hiddenScenario.Token}'.");
        visible.Success.ShouldBeTrue();
        await _dispatcher.Received(1).ExecuteAsync(
            GenericHandlerType, GenericHandlerConfig, Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ByIdRowAccess_InAHiddenGroupsScenario_IsRefused_MainPlanAndVisibleScenarioPass()
    {
        BuildExecutor(out var tokenGuard);
        var clients = Substitute.For<IClientVisibilityGuard>();
        clients.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var rows = new ScheduleRowAccessGuard(clients, tokenGuard);
        var clientId = Guid.NewGuid();

        using (ExecutionPrincipal.Begin(Guid.Parse(_user.Id)))
        {
            (await rows.IsVisibleAsync(clientId, _hiddenScenario.Token)).ShouldBeFalse();
            (await rows.IsVisibleAsync(clientId, _visibleScenario.Token)).ShouldBeTrue();
            (await rows.IsVisibleAsync(clientId, null)).ShouldBeTrue();
        }
    }

    private SkillExecutorService BuildExecutor(out IScenarioTokenAccessGuard tokenGuard)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var userManager = NonAdminUserManager();
        var userService = new UserService(accessor, userManager, Substitute.For<ILogger<UserService>>());
        var groupGuard = new GroupVisibilityGuard(new GroupVisibilityService(_context, userManager, userService), userService);
        tokenGuard = new ScenarioTokenAccessGuard(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()), groupGuard);

        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName(SkillName).Returns(
            new SkillDescriptor(SkillName, "test", SkillCategory.Query, [], [], [], null)
            {
                HandlerType = GenericHandlerType,
                HandlerConfig = GenericHandlerConfig
            });
        _dispatcher = Substitute.For<IGenericSkillDispatcher>();
        _dispatcher.CanHandle(GenericHandlerType).Returns(true);
        _dispatcher.ExecuteAsync(
                GenericHandlerType, GenericHandlerConfig, Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "ran"));
        var gate = Substitute.For<IAutonomyGate>();
        gate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);

        return new SkillExecutorService(
            registry,
            Substitute.For<ISkillUsageTracker>(),
            Substitute.For<IServiceProvider>(),
            _dispatcher,
            gate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            new SkillScenarioScopeGuard(tokenGuard),
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    private UserManager<AppUser> NonAdminUserManager()
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
        userManager.FindByIdAsync(_user.Id).Returns(_user);
        userManager.GetRolesAsync(_user).Returns(Task.FromResult<IList<string>>(new List<string>()));
        return userManager;
    }

    private static SkillInvocation Invocation(Guid analyseToken) => new()
    {
        SkillName = SkillName,
        Parameters = new Dictionary<string, object> { ["analyseToken"] = analyseToken.ToString() }
    };

    private static SkillExecutionContext Ctx(Guid userId) => new()
    {
        UserId = userId,
        TenantId = Guid.Empty,
        UserName = "Klacksy telegram",
        UserPermissions = new List<string>(),
        BypassAutonomyGate = true
    };

    private DataBaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static Group NewGroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{TestPrefix}{suffix}",
        Description = "Restricted scenario visibility integration test",
        ValidFrom = DateTime.UtcNow.AddYears(-1)
    };

    private static AnalyseScenario NewScenario(string suffix, Guid groupId) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{TestPrefix}{suffix}",
        GroupId = groupId,
        FromDate = new DateOnly(2027, 3, 1),
        UntilDate = new DateOnly(2027, 3, 31),
        Token = Guid.NewGuid(),
        CreatedByUser = TestPrefix,
        Status = AnalyseScenarioStatus.Active
    };

    private static AppUser NewUser(string suffix)
    {
        var email = $"{TestPrefix}{suffix}@example.invalid";
        return new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = TestPrefix,
            LastName = suffix,
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM group_visibility WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_visibility WHERE app_user_id IN (SELECT id FROM ""AspNetUsers"" WHERE email LIKE '{TestPrefix}%');
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""AspNetUsers"" WHERE email LIKE '{TestPrefix}%';
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
