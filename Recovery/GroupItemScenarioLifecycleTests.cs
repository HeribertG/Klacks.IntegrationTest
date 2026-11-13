// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

/// <summary>
/// Live integration tests for the scenario-scoped group_item lifecycle (port 5434): a temporary
/// cross-group membership is discarded on Reject, promoted to a real temporary membership on Promote, two parallel
/// scenarios can hold the same membership without a unique conflict, and a temporary membership never
/// leaks into the real-member resolution used by the destructive accept path. The tests work on rows of their own only:
/// a borrowed person with a regular membership in a home group, a second person who is a regular member of the receiving
/// group, and the two groups, all named with the INTEGRATION_TEST_GIL_ marker. The group_item rows of those groups and
/// persons and then the groups and persons themselves are removed by that marker before and after each test.
/// </summary>
[TestFixture]
public sealed class GroupItemScenarioLifecycleTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_GIL_";
    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly Until = new(2026, 7, 1);
    private static readonly DateTime GroupsValidFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] PurgeStatements =
    [
        "DELETE FROM group_item WHERE group_id IN (SELECT id FROM \"group\" WHERE starts_with(name, {0}))",
        "DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, {0}))",
        "DELETE FROM \"group\" WHERE starts_with(name, {0})",
        "DELETE FROM client WHERE starts_with(name, {0})",
    ];

    private Guid _clientId;
    private Guid _memberId;
    private Guid _groupId;
    private Guid _homeGroupId;

    [SetUp]
    public async Task SeedSetUp()
    {
        await PurgeAsync();

        _clientId = Guid.NewGuid();
        _memberId = Guid.NewGuid();
        _groupId = Guid.NewGuid();
        _homeGroupId = Guid.NewGuid();

        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = GroupsValidFrom });
        Context.Group.Add(new Group { Id = _homeGroupId, Name = Prefix + "HOME", ValidFrom = GroupsValidFrom });
        Context.Client.Add(new Client { Id = _clientId, FirstName = Prefix, Name = Prefix + "Borrowed", Type = EntityTypeEnum.Employee });
        Context.Client.Add(new Client { Id = _memberId, FirstName = Prefix, Name = Prefix + "Member", Type = EntityTypeEnum.Employee });
        await Context.SaveChangesAsync();

        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = _clientId, GroupId = _homeGroupId, ValidFrom = GroupsValidFrom });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = _memberId, GroupId = _groupId, ValidFrom = GroupsValidFrom });
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
    }

    [TearDown]
    public async Task SeedTearDown()
    {
        Context.ChangeTracker.Clear();
        await PurgeAsync();
    }

    [Test]
    public async Task Reject_discards_a_temporary_membership()
    {
        // Arrange
        var token = Guid.NewGuid();

        // Act
        using (var scope = CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await svc.AddScenarioMembershipAsync(token, _clientId, _groupId, From, Until, CancellationToken.None);
            await uow.CompleteAsync();
            await svc.SoftDeleteScenarioDataAsync(token, CancellationToken.None);
            await uow.CompleteAsync();
        }

        // Assert
        var membership = await FindMembershipAsync(_clientId, _groupId, token);
        membership.ShouldNotBeNull();
        membership!.IsDeleted.ShouldBeTrue("a rejected temporary membership must be soft-deleted");
    }

    [Test]
    public async Task Promote_turns_a_temporary_membership_into_a_real_one()
    {
        // Arrange
        var token = Guid.NewGuid();

        // Act
        using (var scope = CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await svc.AddScenarioMembershipAsync(token, _clientId, _groupId, From, Until, CancellationToken.None);
            await uow.CompleteAsync();
            await svc.PromoteScenarioWorksAsync(token, From, Until, CancellationToken.None);
            await uow.CompleteAsync();
        }

        // Assert
        (await FindMembershipAsync(_clientId, _groupId, token)).ShouldBeNull("token must be cleared on promote");
        var real = await FindMembershipAsync(_clientId, _groupId, null);
        real.ShouldNotBeNull("a promoted membership must become a real (null-token) membership");
        real!.IsDeleted.ShouldBeFalse();
        real.IsTemporary.ShouldBeTrue("a promoted membership of a borrowed day stays temporary in the real plan");
    }

    [Test]
    public async Task Two_parallel_scenarios_hold_the_same_membership_without_a_unique_conflict()
    {
        // Arrange
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();
        using var scope = CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Act
        await svc.AddScenarioMembershipAsync(tokenA, _clientId, _groupId, From, Until, CancellationToken.None);
        await svc.AddScenarioMembershipAsync(tokenB, _clientId, _groupId, From, Until, CancellationToken.None);

        // Assert
        await Should.NotThrowAsync(async () => await uow.CompleteAsync());
        (await FindMembershipAsync(_clientId, _groupId, tokenA)).ShouldNotBeNull();
        (await FindMembershipAsync(_clientId, _groupId, tokenB)).ShouldNotBeNull();
    }

    [Test]
    public async Task Temporary_membership_does_not_leak_into_real_member_resolution()
    {
        // Arrange
        var token = Guid.NewGuid();
        using (var scope = CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await svc.AddScenarioMembershipAsync(token, _clientId, _groupId, From, Until, CancellationToken.None);
            await uow.CompleteAsync();
        }

        // Act
        var groupId = _groupId;
        var realMemberIds = await Context.Set<GroupItem>()
            .Where(gi => !gi.IsDeleted && gi.AnalyseToken == null && gi.GroupId == groupId && gi.ClientId != null)
            .Select(gi => gi.ClientId!.Value)
            .ToListAsync();

        // Assert
        realMemberIds.ShouldContain(_memberId, "the regular member of the group is a real member");
        realMemberIds.ShouldNotContain(_clientId);
    }

    private async Task<GroupItem?> FindMembershipAsync(Guid clientId, Guid groupId, Guid? token)
        => await Context.Set<GroupItem>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(gi => gi.ClientId == clientId && gi.GroupId == groupId && gi.AnalyseToken == token);

    private async Task PurgeAsync()
    {
        foreach (var statement in PurgeStatements)
        {
            await Context.Database.ExecuteSqlRawAsync(statement, Prefix);
        }
    }
}
