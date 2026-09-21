// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Proves that ScheduleActivityProbe.CountUngroupedPlannableShiftsAsync runs against the REAL Postgres
/// database and counts what it claims to count. Its predicate contains a negated correlated subquery
/// over GroupItem, which is exactly the shape EF InMemory accepts and Npgsql has rejected before (see
/// ShiftGroupScopeReadRepository's own doc comment), so the unit tests in
/// ScheduleActivityProbeUngroupedShiftsTests pin the predicate while only this fixture can pin the
/// translation.
///
/// Asserted as a DELTA around this fixture's own seed, never as an absolute: the count is
/// installation-wide and the reference database carries a real, moving population of duties. The seed
/// therefore contains both a positive group (duties that must raise the count) and a negative group
/// (a grouped, a container, an order, a soft-deleted, a scenario and an expired duty, which must not),
/// and the assertion is that the count rose by exactly the size of the positive group.
///
/// Cleanup removes ONLY rows carrying this fixture's own prefix, in membership -> shift -> group order,
/// because group_item has no name of its own and is deleted through the shift ids it points at.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.Proactive;

[TestFixture]
[Category("RealDatabase")]
public class UngroupedShiftsProbeLiveScanTests
{
    private const string TestPrefix = "INTEGRATION_TEST_UNGROUPED_SHIFTS_";
    private const int ExpectedCountedSeeds = 3;

    private static readonly DateOnly ReferenceDate = new(2026, 9, 21);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
    }

    [Test]
    public async Task CountUngroupedPlannableShiftsAsync_AgainstTheRealDatabase_CountsOnlyTheUngroupedPlannableSeeds()
    {
        var before = await CountAsync();

        await SeedAsync();

        var after = await CountAsync();

        (after - before).ShouldBe(
            ExpectedCountedSeeds,
            "Exactly the ungrouped, staffable, still valid duties of this seed must raise the "
            + "installation-wide count - a grouped, a container, an order, a soft-deleted, a scenario "
            + "and an expired duty were seeded alongside them and must not.");
    }

    private static async Task<int> CountAsync()
    {
        await using var context = NewContext();
        return await new ScheduleActivityProbe(context)
            .CountUngroupedPlannableShiftsAsync(ReferenceDate);
    }

    private static async Task SeedAsync()
    {
        await using var context = NewContext();

        var countedOriginal = PlannableTask("counted-original");
        var countedSplit = PlannableTask("counted-split");
        countedSplit.Status = ShiftStatus.SplitShift;
        var countedEndingToday = PlannableTask("counted-ends-today");
        countedEndingToday.UntilDate = ReferenceDate;

        var grouped = PlannableTask("ignored-grouped");

        var container = PlannableTask("ignored-container");
        container.ShiftType = ShiftType.IsContainer;

        var order = PlannableTask("ignored-order");
        order.Status = ShiftStatus.OriginalOrder;

        var deleted = PlannableTask("ignored-deleted");
        deleted.IsDeleted = true;

        var scenarioClone = PlannableTask("ignored-scenario");
        scenarioClone.AnalyseToken = Guid.NewGuid();

        var expired = PlannableTask("ignored-expired");
        expired.UntilDate = ReferenceDate.AddDays(-1);

        await context.Shift.AddRangeAsync(
            countedOriginal, countedSplit, countedEndingToday,
            grouped, container, order, deleted, scenarioClone, expired);

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "group",
            ValidFrom = ReferenceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Root = null,
            Lft = 0,
            Rgt = 0
        };
        await context.Group.AddAsync(group);

        await context.GroupItem.AddAsync(new GroupItem
        {
            Id = Guid.NewGuid(),
            ShiftId = grouped.Id,
            GroupId = group.Id,
            AnalyseToken = null
        });

        await context.SaveChangesAsync();
    }

    private static Shift PlannableTask(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        Abbreviation = "UGS",
        Status = ShiftStatus.OriginalShift,
        ShiftType = ShiftType.IsTask,
        FromDate = ReferenceDate.AddDays(-30),
        UntilDate = null,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        AnalyseToken = null,
        ScenarioSourceShiftId = null,
        IsDeleted = false
    };

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync()
    {
        await using var context = NewContext();

        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE name LIKE {0}", TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE name LIKE {0}", TestPrefix + "%");
    }
}
