// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for ShiftGroupScopeReadRepository against the real PostgreSQL database. They
/// exist because the unit tests structurally cannot cover the failure this class already had: the EF
/// InMemory provider evaluates an untranslatable LINQ shape client-side and passes, while Npgsql
/// throws "The LINQ expression ... could not be translated" at runtime and the trigger tick swallows
/// it as a failed detector. Every proactive notification audience is derived from this reader, so a
/// query that does not translate silently costs the whole group-scoping mechanism.
///
/// Strictly read-only: no rows are inserted, updated or deleted. The fixture reads ids that already
/// exist and compares the reader's answer against the same predicate expressed in raw SQL, so it is
/// safe against the shared dev database and needs no INTEGRATION_TEST_ cleanup.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Shifts;

[TestFixture]
[Category("RealDatabase")]
public class ShiftGroupScopeReadRepositoryIntegrationTests
{
    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ShiftGroupScopeReadRepository _repository = null!;

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [SetUp]
    public void SetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        _context = NewContext();
        _repository = new ShiftGroupScopeReadRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    /// <summary>Shift ids that really carry live, non-scenario group memberships, most-groups first.</summary>
    private async Task<List<Guid>> LiveShiftIdsAsync(int take)
    {
        return await _context.GroupItem
            .Where(groupItem => groupItem.ShiftId != null
                && !groupItem.IsDeleted
                && groupItem.AnalyseToken == null
                && groupItem.ScenarioSourceGroupItemId == null)
            .GroupBy(groupItem => groupItem.ShiftId!.Value)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .Take(take)
            .ToListAsync();
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_TranslatesToSql_AndMatchesTheRawPredicate()
    {
        var shiftIds = await LiveShiftIdsAsync(25);
        if (shiftIds.Count == 0)
        {
            Assert.Ignore("No shift-linked group_item rows in the test database.");
        }

        var actual = await _repository.GetGroupIdsByShiftIdsAsync(shiftIds);

        await using var verificationContext = NewContext();
        var expected = await verificationContext.GroupItem
            .Where(groupItem => groupItem.ShiftId != null
                && !groupItem.IsDeleted
                && groupItem.AnalyseToken == null
                && groupItem.ScenarioSourceGroupItemId == null
                && shiftIds.Contains(groupItem.ShiftId!.Value))
            .Select(groupItem => new { ShiftId = groupItem.ShiftId!.Value, groupItem.GroupId })
            .ToListAsync();

        var expectedByShift = expected
            .GroupBy(row => row.ShiftId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.GroupId).Distinct().OrderBy(id => id).ToList());

        actual.Count.ShouldBe(expectedByShift.Count);
        foreach (var (shiftId, expectedGroupIds) in expectedByShift)
        {
            actual.ShouldContainKey(shiftId);
            actual[shiftId].ShouldBe(expectedGroupIds);
        }
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_ReturnsEveryGroupOfAMultiGroupShift()
    {
        // The whole reason GroupIds replaced a single GroupId: a shift is a member of many groups, and
        // keeping one would deny the finding to the planners of the others.
        var shiftIds = await LiveShiftIdsAsync(1);
        if (shiftIds.Count == 0)
        {
            Assert.Ignore("No shift-linked group_item rows in the test database.");
        }

        var mostConnectedShiftId = shiftIds[0];
        var result = await _repository.GetGroupIdsByShiftIdsAsync(new[] { mostConnectedShiftId });

        await using var verificationContext = NewContext();
        var expectedCount = await verificationContext.GroupItem
            .Where(groupItem => groupItem.ShiftId == mostConnectedShiftId
                && !groupItem.IsDeleted
                && groupItem.AnalyseToken == null
                && groupItem.ScenarioSourceGroupItemId == null)
            .Select(groupItem => groupItem.GroupId)
            .Distinct()
            .CountAsync();

        result.ShouldContainKey(mostConnectedShiftId);
        result[mostConnectedShiftId].Count.ShouldBe(expectedCount);
        result[mostConnectedShiftId].ShouldBe(result[mostConnectedShiftId].OrderBy(id => id).ToList());
    }

    [Test]
    public async Task GetGroupIdsByShiftIdsAsync_UnknownShiftId_TranslatesAndReturnsNothing()
    {
        var result = await _repository.GetGroupIdsByShiftIdsAsync(new[] { Guid.NewGuid() });

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetGroupIdsByWorkIdsAsync_TranslatesToSql_AndResolvesThroughTheWorksShift()
    {
        await using var readContext = NewContext();
        var workRows = await readContext.Work
            .Where(work => !work.IsDeleted)
            .Select(work => new { work.Id, work.ShiftId })
            .Take(25)
            .ToListAsync();

        if (workRows.Count == 0)
        {
            Assert.Ignore("No work rows in the test database.");
        }

        var actual = await _repository.GetGroupIdsByWorkIdsAsync(workRows.Select(row => row.Id).ToList());

        var groupsByShift = await _repository.GetGroupIdsByShiftIdsAsync(
            workRows.Select(row => row.ShiftId).Distinct().ToList());

        foreach (var row in workRows)
        {
            if (groupsByShift.TryGetValue(row.ShiftId, out var expectedGroupIds))
            {
                actual.ShouldContainKey(row.Id);
                actual[row.Id].ShouldBe(expectedGroupIds);
            }
            else
            {
                actual.ContainsKey(row.Id).ShouldBeFalse();
            }
        }
    }

    [Test]
    public async Task GetGroupIdsByWorkIdsAsync_UnknownWorkId_TranslatesAndReturnsNothing()
    {
        var result = await _repository.GetGroupIdsByWorkIdsAsync(new[] { Guid.NewGuid() });

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task BothLookups_WithNoIds_ShortCircuitWithoutQuerying()
    {
        (await _repository.GetGroupIdsByShiftIdsAsync(Array.Empty<Guid>())).ShouldBeEmpty();
        (await _repository.GetGroupIdsByWorkIdsAsync(Array.Empty<Guid>())).ShouldBeEmpty();
    }
}
