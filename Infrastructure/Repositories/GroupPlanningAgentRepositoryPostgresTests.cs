// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves GroupPlanningAgentRepository against real PostgreSQL: the recursive group CTE, the membership
/// period bounds (timestamptz, Kind=Utc) and the group-item subquery translate and resolve a sub-group's
/// members including its descendants. Regression for "No agents resolved for group 'Winterthur'", where the
/// planning skills used a root-keyed query and every sub-group resolved to zero agents.
/// All rows carry the INTEGRATION_TEST_ prefix and the cleanup is scoped by it.
/// </summary>

using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services.Groups;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class GroupPlanningAgentRepositoryPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PLANAGENTS_";
    private static readonly DateOnly PeriodFrom = new(2099, 3, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 3, 31);

    private DataBaseContext _context = null!;
    private MemoryCache _cache = null!;
    private GroupPlanningAgentRepository _repository = null!;

    private readonly Guid _root = Guid.NewGuid();
    private readonly Guid _inner = Guid.NewGuid();
    private readonly Guid _leaf = Guid.NewGuid();
    private readonly Guid _siblingLeaf = Guid.NewGuid();
    private readonly Guid _emptyLeaf = Guid.NewGuid();

    private Guid _leafMember;
    private Guid _siblingLeafMember;
    private Guid _innerMember;
    private Guid _expiredLeafMember;

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _cache = new MemoryCache(new MemoryCacheOptions());

        var user = Substitute.For<IUserService>();
        user.GetIdString().Returns(string.Empty);
        var groupClient = new GroupClientService(_context, _cache, Substitute.For<ILogger<GroupClientService>>());
        var filter = new ClientGroupFilterService(
            groupClient,
            Substitute.For<IGroupVisibilityService>(),
            user,
            Substitute.For<ILogger<ClientGroupFilterService>>());
        _repository = new GroupPlanningAgentRepository(_context, filter);

        await CleanupAsync();
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
        _cache.Dispose();
    }

    [Test]
    public async Task Leaf_ResolvesItsActiveMembers()
    {
        var ids = await _repository.GetAgentIdsAsync(_leaf, PeriodFrom, PeriodUntil);

        ids.ShouldBe(new[] { _leafMember }, ignoreOrder: true);
    }

    [Test]
    public async Task InnerGroup_IncludesDescendantMembers()
    {
        var ids = await _repository.GetAgentIdsAsync(_inner, PeriodFrom, PeriodUntil);

        ids.ShouldBe(new[] { _innerMember, _leafMember, _siblingLeafMember }, ignoreOrder: true);
    }

    [Test]
    public async Task Root_ResolvesTheWholeTree()
    {
        var ids = await _repository.GetAgentIdsAsync(_root, PeriodFrom, PeriodUntil);

        ids.ShouldBe(new[] { _innerMember, _leafMember, _siblingLeafMember }, ignoreOrder: true);
    }

    [Test]
    public async Task EmptyLeaf_ResolvesNothing()
    {
        var ids = await _repository.GetAgentIdsAsync(_emptyLeaf, PeriodFrom, PeriodUntil);

        ids.ShouldBeEmpty();
    }

    [Test]
    public async Task MembershipEndingBeforeThePeriod_IsExcluded()
    {
        var ids = await _repository.GetAgentIdsAsync(_leaf, PeriodFrom, PeriodUntil);

        ids.ShouldNotContain(_expiredLeafMember);
    }

    [Test]
    public async Task MembershipEndingBeforeThePeriod_StillCountsForAnOverlappingPeriod()
    {
        var earlier = await _repository.GetAgentIdsAsync(_leaf, new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31));

        earlier.ShouldContain(_expiredLeafMember);
    }

    [Test]
    public async Task MembershipEndingExactlyAtPeriodStart_IsIncluded()
    {
        var memberId = await AddMemberAndSaveAsync(
            membershipUntil: new DateTime(2099, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var ids = await _repository.GetAgentIdsAsync(_leaf, PeriodFrom, PeriodUntil);

        ids.ShouldContain(memberId);
    }

    [Test]
    public async Task MembershipEndingOneDayBeforePeriodStart_IsExcluded()
    {
        var memberId = await AddMemberAndSaveAsync(
            membershipUntil: new DateTime(2099, 2, 28, 0, 0, 0, DateTimeKind.Utc));

        var ids = await _repository.GetAgentIdsAsync(_leaf, PeriodFrom, PeriodUntil);

        ids.ShouldNotContain(memberId);
    }

    [Test]
    public async Task MembershipStartingOnTheLastDayOfThePeriod_IsIncluded()
    {
        var atMidnight = await AddMemberAndSaveAsync(membershipFrom: new DateTime(2099, 3, 31, 0, 0, 0, DateTimeKind.Utc));
        var lateInDay = await AddMemberAndSaveAsync(membershipFrom: new DateTime(2099, 3, 31, 23, 0, 0, DateTimeKind.Utc));

        var ids = await _repository.GetAgentIdsAsync(_leaf, PeriodFrom, PeriodUntil);

        ids.ShouldContain(atMidnight);
        ids.ShouldContain(lateInDay);
    }

    [Test]
    public async Task MembershipStartingTheDayAfterThePeriod_IsExcluded()
    {
        var memberId = await AddMemberAndSaveAsync(membershipFrom: new DateTime(2099, 4, 1, 0, 0, 0, DateTimeKind.Utc));

        var ids = await _repository.GetAgentIdsAsync(_leaf, PeriodFrom, PeriodUntil);

        ids.ShouldNotContain(memberId);
    }

    private async Task<Guid> AddMemberAndSaveAsync(DateTime? membershipFrom = null, DateTime? membershipUntil = null)
    {
        var clientId = AddClient(
            "BoundaryMember", _leaf, EntityTypeEnum.Employee,
            membershipFrom: membershipFrom, membershipUntil: membershipUntil);
        await _context.SaveChangesAsync();

        return clientId;
    }

    private async Task SeedAsync()
    {
        AddGroup(_root, null, 1, 10);
        AddGroup(_inner, _root, 2, 9);
        AddGroup(_leaf, _inner, 3, 4);
        AddGroup(_siblingLeaf, _inner, 5, 6);
        AddGroup(_emptyLeaf, _inner, 7, 8);

        _leafMember = AddClient("LeafMember", _leaf, EntityTypeEnum.Employee);
        _siblingLeafMember = AddClient("SiblingLeafMember", _siblingLeaf, EntityTypeEnum.ExternEmp);
        _innerMember = AddClient("InnerMember", _inner, EntityTypeEnum.Employee);
        AddClient("LeafCustomer", _leaf, EntityTypeEnum.Customer);
        AddClient("LeafScenarioMember", _leaf, EntityTypeEnum.Employee, scenarioToken: Guid.NewGuid());
        _expiredLeafMember = AddClient(
            "LeafExpired", _leaf, EntityTypeEnum.Employee,
            membershipUntil: new DateTime(2099, 2, 28, 0, 0, 0, DateTimeKind.Utc));

        await _context.SaveChangesAsync();
    }

    private void AddGroup(Guid id, Guid? parent, int lft, int rgt)
    {
        _context.Group.Add(new Group
        {
            Id = id,
            Name = TestPrefix + id,
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = parent,
            Root = _root,
            Lft = lft,
            Rgt = rgt
        });
    }

    private Guid AddClient(
        string suffix,
        Guid groupId,
        EntityTypeEnum type,
        Guid? scenarioToken = null,
        DateTime? membershipFrom = null,
        DateTime? membershipUntil = null)
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = clientId,
            Name = TestPrefix + suffix,
            FirstName = "Test",
            Type = type,
            LegalEntity = type == EntityTypeEnum.Customer,
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ValidFrom = membershipFrom ?? new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidUntil = membershipUntil
            }
        });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            AnalyseToken = scenarioToken
        });

        return clientId;
    }

    private async Task CleanupAsync()
    {
        const string sql = @"
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, {0}))
                OR client_id IN (SELECT id FROM client WHERE starts_with(name, {0}));
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, {0}));
            DELETE FROM client WHERE starts_with(name, {0});
            DELETE FROM ""group"" WHERE starts_with(name, {0});
        ";
        await _context.Database.ExecuteSqlRawAsync(sql, TestPrefix);
    }
}
