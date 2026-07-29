// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Interfaces;
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
/// cross-group membership is discarded on Reject, promoted to a real membership on Promote, two parallel
/// scenarios can hold the same membership without a unique conflict, and a temporary membership never
/// leaks into the real-member resolution used by the destructive accept path. Each test cleans up its
/// rows. The tests self-ignore when the seeded data has no suitable client/group.
/// </summary>
[TestFixture]
public sealed class GroupItemScenarioLifecycleTests : WizardHarnessTestBase
{
    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly Until = new(2026, 7, 1);

    private async Task<(Guid ClientId, Guid GroupId)?> AnyClientAndGroupAsync()
    {
        var clientId = await Context.Set<Client>().OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync();
        var groupId = await Context.Set<Group>().OrderBy(g => g.Id).Select(g => g.Id).FirstOrDefaultAsync();
        return clientId == Guid.Empty || groupId == Guid.Empty ? null : (clientId, groupId);
    }

    private async Task<(Guid ClientId, Guid GroupId)?> ClientNotInGroupAsync()
    {
        var clientId = await Context.Set<Client>().OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync();
        if (clientId == Guid.Empty)
        {
            return null;
        }
        var ownGroups = await Context.Set<GroupItem>()
            .Where(gi => gi.ClientId == clientId).Select(gi => gi.GroupId).Distinct().ToListAsync();
        var groupId = await Context.Set<Group>()
            .Where(g => !ownGroups.Contains(g.Id)).OrderBy(g => g.Id).Select(g => g.Id).FirstOrDefaultAsync();
        return groupId == Guid.Empty ? null : (clientId, groupId);
    }

    private async Task<GroupItem?> FindMembershipAsync(Guid clientId, Guid groupId, Guid? token)
        => await Context.Set<GroupItem>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(gi => gi.ClientId == clientId && gi.GroupId == groupId && gi.AnalyseToken == token);

    private async Task HardDeleteMembershipsAsync(Guid clientId, Guid groupId)
    {
        var rows = await Context.Set<GroupItem>().IgnoreQueryFilters()
            .Where(gi => gi.ClientId == clientId && gi.GroupId == groupId
                && (gi.AnalyseToken != null || gi.ScenarioSourceGroupItemId != null
                    || gi.ValidFrom == From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)))
            .ToListAsync();
        if (rows.Count > 0)
        {
            Context.Set<GroupItem>().RemoveRange(rows);
            await Context.SaveChangesAsync();
        }
    }

    [Test]
    public async Task Reject_discards_a_temporary_membership()
    {
        var fixture = await AnyClientAndGroupAsync();
        if (fixture is null)
        {
            Assert.Ignore("No client/group fixture available.");
            return;
        }
        var (clientId, groupId) = fixture.Value;
        var token = Guid.NewGuid();

        try
        {
            using (var scope = CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await svc.AddScenarioMembershipAsync(token, clientId, groupId, From, Until, CancellationToken.None);
                await uow.CompleteAsync();
                await svc.SoftDeleteScenarioDataAsync(token, CancellationToken.None);
                await uow.CompleteAsync();
            }

            var membership = await FindMembershipAsync(clientId, groupId, token);
            membership.ShouldNotBeNull();
            membership!.IsDeleted.ShouldBeTrue("a rejected temporary membership must be soft-deleted");
        }
        finally
        {
            await HardDeleteMembershipsAsync(clientId, groupId);
        }
    }

    [Test]
    public async Task Promote_turns_a_temporary_membership_into_a_real_one()
    {
        var fixture = await ClientNotInGroupAsync();
        if (fixture is null)
        {
            Assert.Ignore("No client-not-in-group fixture available.");
            return;
        }
        var (clientId, groupId) = fixture.Value;
        var token = Guid.NewGuid();

        try
        {
            using (var scope = CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await svc.AddScenarioMembershipAsync(token, clientId, groupId, From, Until, CancellationToken.None);
                await uow.CompleteAsync();
                await svc.PromoteScenarioWorksAsync(token, From, Until, CancellationToken.None);
                await uow.CompleteAsync();
            }

            (await FindMembershipAsync(clientId, groupId, token)).ShouldBeNull("token must be cleared on promote");
            var real = await FindMembershipAsync(clientId, groupId, null);
            real.ShouldNotBeNull("a promoted membership must become a real (null-token) membership");
            real!.IsDeleted.ShouldBeFalse();
        }
        finally
        {
            await HardDeleteMembershipsAsync(clientId, groupId);
        }
    }

    [Test]
    public async Task Two_parallel_scenarios_hold_the_same_membership_without_a_unique_conflict()
    {
        var fixture = await AnyClientAndGroupAsync();
        if (fixture is null)
        {
            Assert.Ignore("No client/group fixture available.");
            return;
        }
        var (clientId, groupId) = fixture.Value;
        var tokenA = Guid.NewGuid();
        var tokenB = Guid.NewGuid();

        try
        {
            using var scope = CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await svc.AddScenarioMembershipAsync(tokenA, clientId, groupId, From, Until, CancellationToken.None);
            await svc.AddScenarioMembershipAsync(tokenB, clientId, groupId, From, Until, CancellationToken.None);

            await Should.NotThrowAsync(async () => await uow.CompleteAsync());

            (await FindMembershipAsync(clientId, groupId, tokenA)).ShouldNotBeNull();
            (await FindMembershipAsync(clientId, groupId, tokenB)).ShouldNotBeNull();
        }
        finally
        {
            await HardDeleteMembershipsAsync(clientId, groupId);
        }
    }

    [Test]
    public async Task Temporary_membership_does_not_leak_into_real_member_resolution()
    {
        var fixture = await ClientNotInGroupAsync();
        if (fixture is null)
        {
            Assert.Ignore("No client-not-in-group fixture available.");
            return;
        }
        var (clientId, groupId) = fixture.Value;
        var token = Guid.NewGuid();

        try
        {
            using (var scope = CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await svc.AddScenarioMembershipAsync(token, clientId, groupId, From, Until, CancellationToken.None);
                await uow.CompleteAsync();
            }

            // The accept/clone paths resolve a group's REAL members with this exact filter; the borrowed
            // client (whose only membership in the group is the temporary one) must be excluded, so their
            // real schedule data is never touched.
            var realMemberIds = await Context.Set<GroupItem>()
                .Where(gi => !gi.IsDeleted && gi.AnalyseToken == null && gi.GroupId == groupId && gi.ClientId != null)
                .Select(gi => gi.ClientId!.Value)
                .ToListAsync();

            realMemberIds.ShouldNotContain(clientId);
        }
        finally
        {
            await HardDeleteMembershipsAsync(clientId, groupId);
        }
    }
}
