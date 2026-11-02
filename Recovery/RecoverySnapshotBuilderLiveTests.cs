// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.IntegrationTest.Wizard;
using Klacks.ScheduleRecovery.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

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
    private const string SeedPrefix = "INTEGRATION_TEST_SNAPABSENCE_";

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
                fixture.GroupId, fixture.ClientId, [fixture.Date], null, new HashSet<Guid>(), CancellationToken.None);
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
    public async Task A_blocking_absence_of_a_candidate_on_the_next_day_reaches_the_snapshot_with_its_real_span()
    {
        await CleanupSeededAsync();
        try
        {
            var group = new Group
            {
                Id = Guid.NewGuid(),
                Name = SeedPrefix + "G",
                Description = string.Empty,
                ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Parent = null,
            };
            var shift = new Shift
            {
                Id = Guid.NewGuid(),
                Name = SeedPrefix + "S",
                Abbreviation = "TST",
                Description = "snapshot absence window",
                Status = ShiftStatus.OriginalShift,
                FromDate = new DateOnly(2099, 1, 1),
                StartShift = new TimeOnly(22, 0),
                EndShift = new TimeOnly(6, 0),
                IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
                ShiftType = ShiftType.IsTask,
                Quantity = 1,
                WorkTime = 8m,
            };
            var absent = NewClient("ABSENT");
            var candidate = NewClient("CANDIDATE");
            var absence = new Absence
            {
                Id = Guid.NewGuid(),
                Name = new MultiLanguage { De = SeedPrefix + "A" },
                Abbreviation = new MultiLanguage { De = "TST" },
                Description = new MultiLanguage { De = "snapshot absence window" },
                Color = "#cccccc",
                IsOnCall = false,
            };
            var day = new DateOnly(2099, 7, 6);
            var nextDay = day.AddDays(1);
            await Context.Set<Group>().AddAsync(group);
            await Context.Shift.AddAsync(shift);
            await Context.Set<Client>().AddRangeAsync(absent, candidate);
            await Context.Set<Absence>().AddAsync(absence);
            await Context.Set<GroupItem>().AddRangeAsync(
                new GroupItem { Id = Guid.NewGuid(), GroupId = group.Id, ShiftId = shift.Id },
                new GroupItem { Id = Guid.NewGuid(), GroupId = group.Id, ClientId = absent.Id },
                new GroupItem { Id = Guid.NewGuid(), GroupId = group.Id, ClientId = candidate.Id });
            await Context.Work.AddAsync(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = absent.Id,
                ShiftId = shift.Id,
                CurrentDate = day,
                StartTime = new TimeOnly(22, 0),
                EndTime = new TimeOnly(6, 0),
                WorkTime = 8m,
            });
            await Context.Break.AddAsync(new Break
            {
                Id = Guid.NewGuid(),
                ClientId = candidate.Id,
                AbsenceId = absence.Id,
                CurrentDate = nextDay,
                StartTime = new TimeOnly(14, 0),
                EndTime = new TimeOnly(18, 0),
                WorkTime = 4m,
            });
            await Context.SaveChangesAsync();

            using var scope = CreateScope();
            var builder = scope.ServiceProvider.GetRequiredService<IRecoverySnapshotBuilder>();
            var snapshot = await builder.BuildAsync(group.Id, absent.Id, [day], null, CancellationToken.None);

            var night = snapshot.GetWorks(absent.Id, day).ShouldHaveSingleItem();
            night.StartAt.ShouldBe(day.ToDateTime(new TimeOnly(22, 0)));
            night.EndAt.ShouldBe(nextDay.ToDateTime(new TimeOnly(6, 0)));
            var window = snapshot.GetBlockingAbsences(candidate.Id, nextDay).ShouldHaveSingleItem();
            window.Start.ShouldBe(nextDay.ToDateTime(new TimeOnly(14, 0)));
            window.End.ShouldBe(nextDay.ToDateTime(new TimeOnly(18, 0)));
            snapshot.GetAvailability(candidate.Id, nextDay).HasBreakBlocker.ShouldBeTrue();
            snapshot.GetBlockingAbsences(candidate.Id, day).ShouldBeEmpty();
        }
        finally
        {
            await CleanupSeededAsync();
        }
    }

    private static Client NewClient(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = SeedPrefix + suffix,
        FirstName = "Test",
        Company = string.Empty,
        LegalEntity = false,
        Membership = new Membership
        {
            Id = Guid.NewGuid(),
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = null,
        },
    };

    private Task CleanupSeededAsync() => Context.Database.ExecuteSqlRawAsync($@"
        DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{SeedPrefix}%'));
        DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{SeedPrefix}%');
        DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{SeedPrefix}%')
            OR client_id IN (SELECT id FROM client WHERE name LIKE '{SeedPrefix}%');
        DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{SeedPrefix}%');
        DELETE FROM shift WHERE name LIKE '{SeedPrefix}%';
        DELETE FROM ""group"" WHERE name LIKE '{SeedPrefix}%';
        DELETE FROM absence WHERE name->>'de' LIKE '{SeedPrefix}%';
        DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{SeedPrefix}%');
        DELETE FROM client WHERE name LIKE '{SeedPrefix}%';");

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
            fixture.GroupId, fixture.ClientId, [fixture.Date], null, new HashSet<Guid>(), CancellationToken.None);

        first.Agents.Select(a => a.Id).ShouldBe(second.Agents.Select(a => a.Id).ToList());
        first.GetWorks(fixture.ClientId, fixture.Date).Select(w => w.ShiftId)
            .ShouldBe(second.GetWorks(fixture.ClientId, fixture.Date).Select(w => w.ShiftId).ToList());
    }
}
