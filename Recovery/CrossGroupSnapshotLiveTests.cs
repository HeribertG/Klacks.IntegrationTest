// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

/// <summary>
/// Live test that the snapshot builder feeds the engine borrowable cross-group candidates when the
/// receiving group is a subgroup: members of the receiving subgroup are tagged in-group, while members of
/// sibling subgroups under the same root appear as cross-group candidates (IsInGroup == false). Probes the
/// seeded data for a suitable subgroup and self-ignores when none exists.
/// </summary>
[TestFixture]
public sealed class CrossGroupSnapshotLiveTests : WizardHarnessTestBase
{
    private const int MaxProbe = 15;

    private async Task<List<(Guid SubgroupId, Guid ClientId, DateOnly Date)>> SubgroupFixturesAsync()
    {
        // Works whose shift sits in a SUBGROUP (parent not null), recent first, are candidates for a
        // cross-group borrow: their root spans sibling subgroups whose members become borrowable.
        var rows = await (
                from w in Context.Set<Work>()
                join s in Context.Set<Shift>() on w.ShiftId equals s.Id
                join gi in Context.Set<GroupItem>() on w.ShiftId equals gi.ShiftId
                join g in Context.Set<Group>() on gi.GroupId equals g.Id
                where w.AnalyseToken == null && gi.AnalyseToken == null && g.Parent != null
                orderby w.CurrentDate descending
                select new { w.ClientId, SubgroupId = g.Id, w.CurrentDate })
            .Take(40)
            .ToListAsync();

        return rows
            .Select(r => (r.SubgroupId, r.ClientId, r.CurrentDate))
            .Distinct()
            .Take(MaxProbe)
            .ToList();
    }

    [Test]
    public async Task Builder_feeds_cross_group_candidates_for_a_subgroup()
    {
        using var scope = CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<IRecoverySnapshotBuilder>();

        foreach (var (subgroupId, clientId, date) in await SubgroupFixturesAsync())
        {
            var snapshot = await builder.BuildAsync(subgroupId, clientId, [date], CancellationToken.None);
            if (snapshot.GetWorks(clientId, date).Count == 0)
            {
                continue;
            }

            var hasCrossGroup = snapshot.Agents.Any(a => !a.IsInGroup);
            var hasInGroup = snapshot.Agents.Any(a => a.IsInGroup);
            if (hasCrossGroup)
            {
                hasInGroup.ShouldBeTrue("a borrow scenario must still expose the receiving group's own members");
                snapshot.ReceivingGroupId.ShouldBe(subgroupId);
                return;
            }
        }

        Assert.Ignore("No subgroup fixture with a sibling cross-group pool available on this database.");
    }
}
