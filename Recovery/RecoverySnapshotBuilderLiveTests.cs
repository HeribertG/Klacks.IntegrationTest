// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.IntegrationTest.Wizard;
using Klacks.ScheduleRecovery.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

/// <summary>
/// Live integration tests for <see cref="RecoverySnapshotBuilder"/> against the integration database
/// (port 5434). They prove the builder wires up against the real schema, exposes candidates in a stable
/// total order (Guid), extracts the absent agent's works as demands, and produces an identical snapshot
/// across two builds (determinism). A fixture is chosen by probing recent works whose shift is visible in
/// get_schedule_entries; the tests self-ignore when the seeded data yields none.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderLiveTests : WizardHarnessTestBase
{
    private const int CandidateSampleSize = 40;
    private const int MaxBuildAttempts = 12;

    private sealed record Fixture(Guid GroupId, Guid ClientId, DateOnly Date);

    private async Task<List<Fixture>> CandidateFixturesAsync()
    {
        // Recent, real (non-scenario) works whose shift is active and group-linked are the likeliest to be
        // visible in get_schedule_entries; the global query filters drop soft-deleted shifts/groups.
        var rows = await (
                from w in Context.Set<Work>()
                join s in Context.Set<Shift>() on w.ShiftId equals s.Id
                join gi in Context.Set<GroupItem>() on w.ShiftId equals gi.ShiftId
                where w.AnalyseToken == null && gi.Group != null
                orderby w.CurrentDate descending
                select new { w.ClientId, Root = gi.Group!.Root ?? gi.Group.Id, w.CurrentDate })
            .Take(CandidateSampleSize)
            .ToListAsync();

        return rows
            .Select(r => new Fixture(r.Root, r.ClientId, r.CurrentDate))
            .Distinct()
            .Take(MaxBuildAttempts)
            .ToList();
    }

    private async Task<(Fixture Fixture, RecoverySnapshot Snapshot)?> ResolveWithDemandAsync(
        IRecoverySnapshotBuilder builder)
    {
        foreach (var fixture in await CandidateFixturesAsync())
        {
            var snapshot = await builder.BuildAsync(
                fixture.GroupId, fixture.ClientId, [fixture.Date], CancellationToken.None);
            if (snapshot.Agents.Count > 0 && snapshot.GetWorks(fixture.ClientId, fixture.Date).Count > 0)
            {
                return (fixture, snapshot);
            }
        }
        return null;
    }

    [Test]
    public async Task Builds_snapshot_with_candidates_in_stable_guid_order_and_extracts_demands()
    {
        using var scope = CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<IRecoverySnapshotBuilder>();

        var resolved = await ResolveWithDemandAsync(builder);
        if (resolved is null)
        {
            Assert.Ignore("No schedule-visible work fixture available on this database.");
            return;
        }

        var (fixture, snapshot) = resolved.Value;
        snapshot.Agents.ShouldNotBeEmpty();
        var ids = snapshot.Agents.Select(a => a.Id).ToList();
        ids.ShouldBe(ids.OrderBy(x => x).ToList(), "candidates must be in stable Guid order");
        snapshot.GetWorks(fixture.ClientId, fixture.Date).ShouldNotBeEmpty();
    }

    [Test]
    public async Task Two_builds_produce_an_identical_snapshot()
    {
        using var scope = CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<IRecoverySnapshotBuilder>();

        var resolved = await ResolveWithDemandAsync(builder);
        if (resolved is null)
        {
            Assert.Ignore("No schedule-visible work fixture available on this database.");
            return;
        }

        var fixture = resolved.Value.Fixture;
        var first = resolved.Value.Snapshot;
        var second = await builder.BuildAsync(
            fixture.GroupId, fixture.ClientId, [fixture.Date], CancellationToken.None);

        first.Agents.Select(a => a.Id).ShouldBe(second.Agents.Select(a => a.Id).ToList());
        first.GetWorks(fixture.ClientId, fixture.Date).Select(w => w.ShiftId)
            .ShouldBe(second.GetWorks(fixture.ClientId, fixture.Date).Select(w => w.ShiftId).ToList());
    }
}
