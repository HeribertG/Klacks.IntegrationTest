// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

// Verifies GroupItemRepository.GetVisibilityMembershipAsync against a real PostgreSQL database: the projection
// (Any over the group items plus the non-scenario group-id list) translates and executes, and it reports the
// facts the client visibility rule decides by - group-less, real memberships, scenario-only (counted as "has a
// group item" but without an active group), and unknown/soft-deleted clients as null. Seeds its own prefixed rows
// and cleans up only those.

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Groups;

[TestFixture]
[Category("RealDatabase")]
public class GroupItemVisibilityMembershipPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VISMEMBER_";

    private DataBaseContext _context = null!;
    private GroupItemRepository _repository = null!;

    private readonly Guid _scenarioToken = Guid.NewGuid();
    private readonly Guid _groupAId = Guid.NewGuid();
    private readonly Guid _groupBId = Guid.NewGuid();
    private readonly Guid _memberClientId = Guid.NewGuid();
    private readonly Guid _grouplessClientId = Guid.NewGuid();
    private readonly Guid _scenarioOnlyClientId = Guid.NewGuid();
    private readonly Guid _deletedClientId = Guid.NewGuid();

    [SetUp]
    public async Task Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>());

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
    public async Task ClientWithRealMemberships_ReportsDistinctActiveGroups()
    {
        var membership = await _repository.GetVisibilityMembershipAsync(_memberClientId);

        membership.ShouldNotBeNull();
        membership.HasAnyGroupItem.ShouldBeTrue();
        membership.ActiveGroupIds.ShouldBe(new[] { _groupAId, _groupBId }, ignoreOrder: true);
    }

    [Test]
    public async Task GrouplessClient_HasNoGroupItem()
    {
        var membership = await _repository.GetVisibilityMembershipAsync(_grouplessClientId);

        membership.ShouldNotBeNull();
        membership.HasAnyGroupItem.ShouldBeFalse();
        membership.ActiveGroupIds.ShouldBeEmpty();
    }

    [Test]
    public async Task ScenarioOnlyClient_HasAGroupItemButNoActiveGroup()
    {
        var membership = await _repository.GetVisibilityMembershipAsync(_scenarioOnlyClientId);

        membership.ShouldNotBeNull();
        membership.HasAnyGroupItem.ShouldBeTrue();
        membership.ActiveGroupIds.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownOrSoftDeletedClient_IsNull()
    {
        (await _repository.GetVisibilityMembershipAsync(Guid.NewGuid())).ShouldBeNull();
        (await _repository.GetVisibilityMembershipAsync(_deletedClientId)).ShouldBeNull();
    }

    private async Task SeedTestDataAsync()
    {
        _context.Group.AddRange(
            NewGroup(_groupAId, "A"),
            NewGroup(_groupBId, "B"));

        var deleted = NewClient(_deletedClientId, "Deleted");
        deleted.IsDeleted = true;
        _context.Client.AddRange(
            NewClient(_memberClientId, "Member"),
            NewClient(_grouplessClientId, "Groupless"),
            NewClient(_scenarioOnlyClientId, "ScenarioOnly"),
            deleted);
        await _context.SaveChangesAsync();

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _memberClientId, GroupId = _groupAId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _memberClientId, GroupId = _groupBId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _memberClientId, GroupId = _groupAId, AnalyseToken = _scenarioToken },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _scenarioOnlyClientId, GroupId = _groupAId, AnalyseToken = _scenarioToken },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _deletedClientId, GroupId = _groupAId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Group NewGroup(Guid id, string suffix) => new()
    {
        Id = id,
        Name = TestPrefix + suffix,
        Description = "Integration test group",
        ValidFrom = DateTime.UtcNow.AddYears(-1)
    };

    private static Client NewClient(Guid id, string suffix) => new()
    {
        Id = id,
        Name = TestPrefix + suffix,
        FirstName = "Membership",
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
