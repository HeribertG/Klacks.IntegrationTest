// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Pagination;

/// <summary>
/// Pins, against real PostgreSQL, what the shift list query pipeline (ShiftQueryPipelineService with the status,
/// date-range, search and sorting composers) returns before paging: every list view of the status filter, the
/// active/former/future date buckets, the search forms (single keyword, AND of keywords, '+' OR, first symbol,
/// case-insensitive) and every sort key in both directions including NULLS LAST/FIRST on valid_until. The composed
/// query must stay one server-side SQL statement (WHERE, ORDER BY and lower() in the generated SQL). The input query
/// is restricted to the six seeded shifts; every shift carries the INTEGRATION_TEST_ShiftPipe_ prefix and the
/// cleanup deletes exactly those.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class ShiftQueryPipelinePostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_ShiftPipe_";
    private const string Ascending = "asc";
    private const string Descending = "desc";
    private static readonly DateOnly FirstDay = new(2031, 1, 1);
    private static readonly DateOnly Today = FirstDay.AddDays(12);

    private DataBaseContext _context = null!;
    private ShiftQueryPipelineService _pipeline = null!;
    private Dictionary<Guid, int> _rankById = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();

        _rankById = [];
        Seed(1, "Alpha North", "PA1", "d3", ShiftStatus.OriginalOrder, ShiftType.IsTask, 3, null, false, false);
        Seed(2, "Beta North", "PB2", "d1", ShiftStatus.SealedOrder, ShiftType.IsTask, 1, 10, false, false);
        Seed(3, "Gamma South", "QG3", "d2", ShiftStatus.OriginalShift, ShiftType.IsTask, 5, 30, true, false);
        Seed(4, "Delta South", "QD4", "d5", ShiftStatus.SplitShift, ShiftType.IsTask, 2, 15, false, true);
        Seed(5, "Epsilon", "PZ5", "d4", ShiftStatus.OriginalShift, ShiftType.IsTask, 13, 20, false, false);
        Seed(6, "Omega", "XC6", "d6", ShiftStatus.OriginalShift, ShiftType.IsContainer, 4, 40, false, false);

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _pipeline = new ShiftQueryPipelineService(
            new DateRangeFilterService(),
            new ShiftSearchService(),
            new ShiftSortingService(),
            new ShiftStatusFilterService(),
            new ShiftPaginationService());
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [TestCase(ShiftFilterType.Original, false, true, true, new[] { 1 })]
    [TestCase(ShiftFilterType.Original, true, true, true, new[] { 2 })]
    [TestCase(ShiftFilterType.Shift, false, true, true, new[] { 3, 4, 5 })]
    [TestCase(ShiftFilterType.Shift, false, true, false, new[] { 3 })]
    [TestCase(ShiftFilterType.Shift, false, false, true, new[] { 4 })]
    [TestCase(ShiftFilterType.Shift, false, false, false, new[] { 5 })]
    [TestCase(ShiftFilterType.Container, false, true, true, new[] { 6 })]
    [TestCase(ShiftFilterType.Absence, false, true, true, new int[0])]
    public async Task ApplyStatusFilter_PinsEveryListView(
        ShiftFilterType filterType, bool isSealedOrder, bool isTimeRange, bool isSporadic, int[] expectedRanks)
    {
        var query = _pipeline.ApplyStatusFilter(SeededQuery(), filterType, isSealedOrder, isTimeRange, isSporadic);

        (await RanksAsync(query)).ShouldBe(expectedRanks);
    }

    [TestCase(true, false, false, new[] { 1, 3, 4, 6 })]
    [TestCase(false, true, false, new[] { 2 })]
    [TestCase(false, false, true, new[] { 5 })]
    [TestCase(true, true, true, new[] { 1, 2, 3, 4, 5, 6 })]
    [TestCase(false, false, false, new int[0])]
    public async Task ApplyDateRangeFilter_PinsTheBuckets(bool active, bool former, bool future, int[] expectedRanks)
    {
        var query = _pipeline.ApplyDateRangeFilter(SeededQuery(), active, former, future, Today);

        (await RanksAsync(query)).ShouldBe(expectedRanks);
        (await query.CountAsync()).ShouldBe(expectedRanks.Length);
    }

    [TestCase("north", new[] { 1, 2 })]
    [TestCase("NORTH", new[] { 1, 2 })]
    [TestCase("  sOuTh  ", new[] { 3, 4 })]
    [TestCase("alpha north", new[] { 1 })]
    [TestCase("north south", new int[0])]
    [TestCase("north+south", new[] { 1, 2, 3, 4 })]
    [TestCase("q", new[] { 3, 4 })]
    [TestCase("pb2", new[] { 2 })]
    [TestCase("", new[] { 1, 2, 3, 4, 5, 6 })]
    public async Task ApplySearchFilter_PinsTheSearchForms(string searchString, int[] expectedRanks)
    {
        var query = _pipeline.ApplySearchFilter(SeededQuery(), searchString, includeClient: false);

        (await RanksAsync(query)).ShouldBe(expectedRanks);
    }

    [TestCase("abbreviation", Ascending, new[] { 1, 2, 5, 4, 3, 6 })]
    [TestCase("abbreviation", Descending, new[] { 6, 3, 4, 5, 2, 1 })]
    [TestCase("name", Ascending, new[] { 1, 2, 4, 5, 3, 6 })]
    [TestCase("name", Descending, new[] { 6, 3, 5, 4, 2, 1 })]
    [TestCase("description", Ascending, new[] { 2, 3, 1, 5, 4, 6 })]
    [TestCase("description", Descending, new[] { 6, 4, 5, 1, 3, 2 })]
    [TestCase("valid_from", Ascending, new[] { 2, 4, 1, 6, 3, 5 })]
    [TestCase("valid_from", Descending, new[] { 5, 3, 6, 1, 4, 2 })]
    [TestCase("valid_until", Ascending, new[] { 2, 4, 5, 3, 6, 1 })]
    [TestCase("valid_until", Descending, new[] { 1, 6, 3, 5, 4, 2 })]
    [TestCase("unknown_key", Ascending, new[] { 2, 4, 1, 6, 3, 5 })]
    [TestCase("name", "", new[] { 2, 4, 1, 6, 3, 5 })]
    public async Task ApplySorting_PinsEverySortKey(string orderBy, string sortOrder, int[] expectedRanks)
    {
        var query = _pipeline.ApplySorting(SeededQuery().OrderBy(shift => shift.FromDate), orderBy, sortOrder);

        (await OrderedRanksAsync(query)).ShouldBe(expectedRanks);
    }

    [Test]
    public async Task ComposedPipeline_RunsAsOneServerSideStatementAndPages()
    {
        var query = _pipeline.ApplyStatusFilter(SeededQuery(), ShiftFilterType.Shift);
        query = _pipeline.ApplyDateRangeFilter(query, true, false, true, Today);
        query = _pipeline.ApplySearchFilter(query, "integration", includeClient: false);
        query = _pipeline.ApplySorting(query, "abbreviation", Descending);

        var sql = query.ToQueryString();
        sql.ShouldContain("WHERE");
        sql.ShouldContain("ORDER BY");
        sql.ShouldContain("lower(");

        var page = await _pipeline.ApplyPaginationAsync(query, new ShiftFilter { RequiredPage = 0, NumberOfItemsPerPage = 2 });

        page.MaxItems.ShouldBe(3);
        page.Shifts.Select(shift => _rankById[shift.Id]).ShouldBe(new[] { 3, 4 });
    }

    private void Seed(
        int rank,
        string name,
        string abbreviation,
        string description,
        ShiftStatus status,
        ShiftType shiftType,
        int fromOffset,
        int? untilOffset,
        bool isTimeRange,
        bool isSporadic)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = Prefix + name,
            Abbreviation = abbreviation,
            Description = description,
            Status = status,
            ShiftType = shiftType,
            FromDate = FirstDay.AddDays(fromOffset),
            UntilDate = untilOffset.HasValue ? FirstDay.AddDays(untilOffset.Value) : null,
            IsTimeRange = isTimeRange,
            IsSporadic = isSporadic,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            Quantity = 1,
            SumEmployees = 1,
        };
        _context.Shift.Add(shift);
        _rankById[shift.Id] = rank;
    }

    private IQueryable<Shift> SeededQuery()
    {
        var ids = _rankById.Keys.ToList();
        return _context.Shift.AsNoTracking().Where(shift => ids.Contains(shift.Id));
    }

    private async Task<int[]> RanksAsync(IQueryable<Shift> query)
    {
        var ids = await query.Select(shift => shift.Id).ToListAsync();
        return ids.Select(id => _rankById[id]).Order().ToArray();
    }

    private async Task<int[]> OrderedRanksAsync(IQueryable<Shift> query)
    {
        var ids = await query.Select(shift => shift.Id).ToListAsync();
        return ids.Select(id => _rankById[id]).ToArray();
    }

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE starts_with(name, {0})", Prefix);
    }
}
