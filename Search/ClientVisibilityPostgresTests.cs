// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

// Verifies the client visibility checks of ClientSearchRepository against a real PostgreSQL database: the
// group filter composed onto a by-id and an id-set query translates and executes, a group-restricted caller
// sees clients of a visible group and clients without any group but not clients of a foreign group, and the
// duplicate check FindList stays deliberately unscoped. Seeds its own prefixed rows and cleans up only those.

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Search;

[TestFixture]
[Category("RealDatabase")]
public class ClientVisibilityPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_CLIENTVIS_";
    private const string RestrictedUserId = "integration-test-restricted-user";

    private DataBaseContext _context = null!;
    private ClientSearchRepository _repository = null!;
    private ClientVisibilityGuard _guard = null!;

    private readonly Guid _visibleGroupId = Guid.NewGuid();
    private readonly Guid _foreignGroupId = Guid.NewGuid();
    private readonly Guid _ownClientId = Guid.NewGuid();
    private readonly Guid _foreignClientId = Guid.NewGuid();
    private readonly Guid _grouplessClientId = Guid.NewGuid();

    [SetUp]
    public async Task Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var groupVisibility = Substitute.For<IGroupVisibilityService>();
        groupVisibility.GetVisibilityScopeAsync()
            .Returns(GroupVisibilityScope.Restricted([_visibleGroupId], [_visibleGroupId]));
        var userService = Substitute.For<IUserService>();
        userService.GetIdString().Returns(RestrictedUserId);

        var groupFilter = new ClientGroupFilterService(
            Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>(),
            groupVisibility,
            userService,
            Substitute.For<ILogger<ClientGroupFilterService>>());

        _repository = new ClientSearchRepository(
            _context, groupFilter, Substitute.For<IClientFuzzySearchService>(), TestCompanyClock.Utc());
        _guard = new ClientVisibilityGuard(_repository, groupFilter);

        await CleanupTestDataAsync();
        await SeedTestDataAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataAsync();
        _context.Dispose();
    }

    [Test]
    public async Task IsVisibleToCallerAsync_TranslatesAndSeparatesOwnForeignAndGrouplessClients()
    {
        (await _repository.IsVisibleToCallerAsync(_ownClientId)).ShouldBeTrue();
        (await _repository.IsVisibleToCallerAsync(_grouplessClientId)).ShouldBeTrue();
        (await _repository.IsVisibleToCallerAsync(_foreignClientId)).ShouldBeFalse();
        (await _repository.IsVisibleToCallerAsync(Guid.NewGuid())).ShouldBeFalse();
    }

    [Test]
    public async Task FilterVisibleToCallerAsync_TranslatesTheIdSetQuery()
    {
        var result = await _repository.FilterVisibleToCallerAsync(
            [_ownClientId, _foreignClientId, _grouplessClientId, Guid.NewGuid()]);

        result.ShouldBe(new[] { _ownClientId, _grouplessClientId }, ignoreOrder: true);
    }

    [Test]
    public async Task Guard_AreAllVisible_IsFalseAsSoonAsOneClientIsHidden()
    {
        (await _guard.AreAllVisibleAsync([_ownClientId, _grouplessClientId])).ShouldBeTrue();
        (await _guard.AreAllVisibleAsync([_ownClientId, _foreignClientId])).ShouldBeFalse();
    }

    [Test]
    public async Task FindList_DuplicateCheck_StillFindsTheForeignClient()
    {
        var result = await _repository.FindList(company: null, name: TestPrefix, firstname: null);

        result.Select(c => c.Id).ShouldBe(new[] { _ownClientId, _foreignClientId, _grouplessClientId }, ignoreOrder: true);
    }

    [Test]
    public async Task FindList_AllParametersBlank_ReturnsNothing()
    {
        (await _repository.FindList(" ", " ", " ")).ShouldBeEmpty();
    }

    private async Task SeedTestDataAsync()
    {
        _context.Group.AddRange(
            new Group
            {
                Id = _visibleGroupId,
                Name = TestPrefix + "Visible",
                Description = "Integration test visible group",
                ValidFrom = DateTime.UtcNow.AddYears(-1)
            },
            new Group
            {
                Id = _foreignGroupId,
                Name = TestPrefix + "Foreign",
                Description = "Integration test foreign group",
                ValidFrom = DateTime.UtcNow.AddYears(-1)
            });

        _context.Client.AddRange(
            NewClient(_ownClientId, "Own"),
            NewClient(_foreignClientId, "Foreign"),
            NewClient(_grouplessClientId, "Groupless"));
        await _context.SaveChangesAsync();

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _ownClientId, GroupId = _visibleGroupId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _foreignClientId, GroupId = _foreignGroupId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Client NewClient(Guid id, string suffix) => new()
    {
        Id = id,
        Name = TestPrefix + suffix,
        FirstName = "Visibility",
        Type = EntityTypeEnum.Employee,
        Gender = GenderEnum.Female,
        LegalEntity = false
    };

    private async Task CleanupTestDataAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE name LIKE {0})",
            TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE name LIKE {0}", TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE name LIKE {0}", TestPrefix + "%");
    }
}
