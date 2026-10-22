// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Database fixture and real visibility pipeline for tests that run skills as a group-restricted Supervisor against
/// PostgreSQL (IT database klacks): a VISIBLE and a HIDDEN group, a Supervisor who sees only VISIBLE through a
/// group_visibility row, and one client in each group. Builds UserService, GroupVisibilityService,
/// ClientGroupFilterService, ClientSearchRepository and ClientVisibilityGuard unmocked and exposes the group visibility
/// service and the client search for skills that resolve groups or clients by name; only the Identity user manager
/// is a stub answering "this user exists and holds no role", and the fuzzy search and the company clock, which the
/// visibility check never reaches, are substitutes. Every row carries the prefix the caller passes in and the cleanup
/// removes exactly the rows whose name starts with it (starts_with, not LIKE, so the underscores of the prefix are not
/// wildcards). The prefix must start with INTEGRATION_TEST_: the IT database is shared with other fixtures, and a cleanup
/// keyed on a production-plausible prefix would delete real rows.
/// </summary>
/// <param name="prefix">Prefix every group, client, user, e-mail and e-mail folder of the fixture starts with</param>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services.Groups;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Klacks.Api.Infrastructure.Services.Clients;

namespace Klacks.IntegrationTest.TestHelpers;

internal sealed class SupervisorVisibilityScenario
{
    private const string DefaultConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    private const string ConnectionStringVariable = "DATABASE_URL";
    private const string RequiredPrefixStart = "INTEGRATION_TEST_";

    private readonly string _prefix;

    private SupervisorVisibilityScenario(string prefix, DataBaseContext context)
    {
        _prefix = prefix;
        Context = context;
    }

    public DataBaseContext Context { get; }

    public AppUser Supervisor { get; private set; } = null!;

    public Group VisibleGroup { get; private set; } = null!;

    public Group HiddenGroup { get; private set; } = null!;

    public Client VisibleClient { get; private set; } = null!;

    public Client HiddenClient { get; private set; } = null!;

    public IClientVisibilityGuard Guard { get; private set; } = null!;

    public IClientSearchRepository ClientSearch { get; private set; } = null!;

    public IGroupVisibilityService GroupVisibility { get; private set; } = null!;

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable) ?? DefaultConnectionString;

    public static DbContextOptions<DataBaseContext> BuildOptions() =>
        new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    public static async Task<SupervisorVisibilityScenario> CreateAsync(string prefix, DataBaseContext context)
    {
        EnsureIntegrationTestPrefix(prefix);
        var scenario = new SupervisorVisibilityScenario(prefix, context);
        await scenario.PopulateAsync();
        scenario.BuildVisibilityPipeline();
        return scenario;
    }

    public static async Task CleanupAsync(DataBaseContext context, string prefix)
    {
        EnsureIntegrationTestPrefix(prefix);
        var pattern = prefix.Replace("'", "''");
        var sql = $@"
            DELETE FROM received_emails WHERE starts_with(message_id, '{pattern}');
            DELETE FROM email_folders WHERE starts_with(imap_folder_name, '{pattern}');
            DELETE FROM messenger_contact WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{pattern}'));
            DELETE FROM communication WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{pattern}'));
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{pattern}'));
            DELETE FROM group_visibility WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{pattern}'));
            DELETE FROM group_visibility WHERE app_user_id IN (SELECT id FROM ""AspNetUsers"" WHERE starts_with(email, '{pattern}'));
            DELETE FROM ""group"" WHERE starts_with(name, '{pattern}');
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{pattern}'));
            DELETE FROM client WHERE starts_with(name, '{pattern}');
            DELETE FROM ""AspNetUsers"" WHERE starts_with(email, '{pattern}');
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }

    public UserManager<AppUser> NonAdminUserManager()
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
        userManager.FindByIdAsync(Supervisor.Id).Returns(Supervisor);
        userManager.GetRolesAsync(Supervisor).Returns(Task.FromResult<IList<string>>(new List<string>()));
        return userManager;
    }

    private async Task PopulateAsync()
    {
        VisibleGroup = NewGroup("VISIBLE");
        HiddenGroup = NewGroup("HIDDEN");
        Supervisor = NewUser("SUPERVISOR");
        Context.Group.AddRange(VisibleGroup, HiddenGroup);
        Context.AppUser.Add(Supervisor);
        await Context.SaveChangesAsync();

        Context.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = Supervisor.Id, GroupId = VisibleGroup.Id });
        VisibleClient = await CreateClientAsync("VISIBLE_CLIENT", VisibleGroup.Id);
        HiddenClient = await CreateClientAsync("HIDDEN_CLIENT", HiddenGroup.Id);
    }

    private void BuildVisibilityPipeline()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var userManager = NonAdminUserManager();
        var userService = new UserService(accessor, userManager, Substitute.For<ILogger<UserService>>());
        GroupVisibility = new GroupVisibilityService(Context, userManager, userService);
        var groupClient = new GroupClientService(Context, new MemoryCache(new MemoryCacheOptions()), Substitute.For<ILogger<GroupClientService>>());
        var filter = new ClientGroupFilterService(groupClient, GroupVisibility, userService, Substitute.For<ILogger<ClientGroupFilterService>>());
        ClientSearch = new ClientSearchRepository(Context, filter, Substitute.For<IClientFuzzySearchService>(), Substitute.For<ICompanyClock>());
        Guard = new ClientVisibilityGuard(ClientSearch, filter);
    }

    public static void EnsureIntegrationTestPrefix(string prefix)
    {
        if (!prefix.StartsWith(RequiredPrefixStart, StringComparison.Ordinal) || prefix.Length <= RequiredPrefixStart.Length)
        {
            throw new ArgumentException(
                $"The fixture prefix '{prefix}' must start with {RequiredPrefixStart} and name the fixture after it; the " +
                "cleanup deletes every row whose key starts with the prefix.", nameof(prefix));
        }
    }

    public async Task<Client> CreateClientAsync(string suffix, Guid groupId)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = _prefix + suffix,
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false,
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidUntil = null
            }
        };
        await Context.Set<Client>().AddAsync(client);
        await Context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ClientId = client.Id });
        await Context.SaveChangesAsync();
        return client;
    }

    private Group NewGroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{_prefix}{suffix}",
        Description = "Supervisor skill visibility integration test",
        ValidFrom = DateTime.UtcNow.AddYears(-1)
    };

    private AppUser NewUser(string suffix)
    {
        var email = $"{_prefix}{suffix}@example.invalid";
        return new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = _prefix,
            LastName = suffix,
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }
}
