// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Groups;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Groups;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Pagination;

/// <summary>
/// Pins, against real PostgreSQL, the group list pipeline GroupRepository.Truncated runs (ApplyFilters, then
/// ApplyPaginationAsync): the date-range buckets relative to a fixed company day, the case-insensitive substring
/// search with AND over keywords, every sort key in both directions (PostgreSQL's NULLS LAST ascending and NULLS
/// FIRST descending on valid_until), the paging numbers and the known failure when no date range is selected. The
/// base query is restricted to the five seeded groups, so results never depend on other rows of the shared database.
/// Every group carries the INTEGRATION_TEST_GroupSearch_ prefix and the cleanup deletes exactly those.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class GroupSearchPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_GroupSearch_";
    private const int SeededCount = 5;
    private const int NestedSetOffset = 900000;
    private const string Ascending = "asc";
    private const string Descending = "desc";
    private static readonly DateOnly Today = new(2031, 1, 10);

    private DataBaseContext _context = null!;
    private GroupSearchService _service = null!;
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
        AddGroup(1, "Alpha North", "d3", new DateTime(2030, 1, 1), null);
        AddGroup(2, "Beta North", "d1", new DateTime(2029, 1, 1), new DateTime(2030, 6, 30));
        AddGroup(3, "Gamma South", "d5", new DateTime(2032, 1, 1), new DateTime(2033, 1, 1));
        AddGroup(4, "Delta South", "d2", new DateTime(2031, 1, 5), new DateTime(2031, 1, 20));
        AddGroup(5, "Epsilon North", "d4", new DateTime(2030, 5, 5), new DateTime(2031, 1, 9));

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var validityService = new GroupValidityService(_context, NullLogger<GroupValidityService>.Instance);
        _service = new GroupSearchService(NullLogger<GroupSearchService>.Instance, validityService);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [TestCase(true, false, false, new[] { 1, 4 })]
    [TestCase(false, true, false, new[] { 2, 5 })]
    [TestCase(false, false, true, new[] { 3 })]
    [TestCase(true, true, false, new[] { 1, 2, 4, 5 })]
    [TestCase(true, true, true, new[] { 1, 2, 3, 4, 5 })]
    public async Task ApplyFilters_DateRanges_SelectTheBucketsRelativeToTheCompanyDay(
        bool active, bool former, bool future, int[] expectedRanks)
    {
        var filter = PageFilter();
        filter.ActiveDateRange = active;
        filter.FormerDateRange = former;
        filter.FutureDateRange = future;

        var result = await RunAsync(filter);

        Ranks(result).ShouldBe(expectedRanks);
        result.MaxItems.ShouldBe(expectedRanks.Length);
    }

    [TestCase("north", new[] { 1, 2, 5 })]
    [TestCase("NORTH", new[] { 1, 2, 5 })]
    [TestCase("  sOuTh  ", new[] { 3, 4 })]
    [TestCase("alpha north", new[] { 1 })]
    [TestCase("north south", new int[0])]
    [TestCase("lta", new[] { 4 })]
    public async Task ApplyFilters_SearchString_MatchesEveryKeywordCaseInsensitively(string search, int[] expectedRanks)
    {
        var filter = AllRanges(PageFilter());
        filter.SearchString = search;

        var result = await RunAsync(filter);

        Ranks(result).ShouldBe(expectedRanks);
    }

    [TestCase("name", Ascending, new[] { 1, 2, 4, 5, 3 })]
    [TestCase("name", Descending, new[] { 3, 5, 4, 2, 1 })]
    [TestCase("description", Ascending, new[] { 2, 4, 1, 5, 3 })]
    [TestCase("description", Descending, new[] { 3, 5, 1, 4, 2 })]
    [TestCase("valid_from", Ascending, new[] { 2, 1, 5, 4, 3 })]
    [TestCase("valid_from", Descending, new[] { 3, 4, 5, 1, 2 })]
    [TestCase("valid_until", Ascending, new[] { 2, 5, 4, 3, 1 })]
    [TestCase("valid_until", Descending, new[] { 1, 3, 4, 5, 2 })]
    [TestCase("unknown_key", Ascending, new[] { 1, 2, 3, 4, 5 })]
    [TestCase("name", "", new[] { 1, 2, 3, 4, 5 })]
    public async Task ApplyFilters_Sorting_OrdersByTheRequestedKeyOrKeepsTheBaseOrder(
        string orderBy, string sortOrder, int[] expectedRanks)
    {
        var filter = AllRanges(PageFilter());
        filter.OrderBy = orderBy;
        filter.SortOrder = sortOrder;

        var result = await RunAsync(filter);

        Ranks(result).ShouldBe(expectedRanks);
    }

    [TestCase(0, 2, null, null, null, null, new[] { 1, 2 }, 0, 2)]
    [TestCase(2, 2, null, null, null, null, new[] { 5 }, 4, 2)]
    [TestCase(3, 2, null, null, null, null, new int[0], -1, 2)]
    [TestCase(0, 2, true, null, 2, null, new[] { 5 }, 4, 2)]
    [TestCase(0, 2, null, true, 1, null, new[] { 1, 2 }, 0, 2)]
    [TestCase(0, 2, null, true, 4, 3, new[] { 2, 3 }, 1, 2)]
    [TestCase(0, 5, null, null, null, null, new[] { 1, 2, 3, 4, 5 }, 0, 0)]
    [TestCase(1, 5, true, null, 0, null, new int[0], -1, 0)]
    public async Task ApplyPaginationAsync_PinsPageArithmeticAndOrder(
        int requiredPage,
        int pageSize,
        bool? isNextPage,
        bool? isPreviousPage,
        int? firstItemOnLastPage,
        int? numberOfItemOnPreviousPage,
        int[] expectedRanks,
        int expectedFirstItemOnPage,
        int expectedMaxPages)
    {
        var filter = AllRanges(new GroupFilter
        {
            RequiredPage = requiredPage,
            NumberOfItemsPerPage = pageSize,
            IsNextPage = isNextPage,
            IsPreviousPage = isPreviousPage,
            FirstItemOnLastPage = firstItemOnLastPage,
            NumberOfItemOnPreviousPage = numberOfItemOnPreviousPage,
        });

        var result = await RunAsync(filter);

        Ranks(result).ShouldBe(expectedRanks);
        result.MaxItems.ShouldBe(SeededCount);
        result.CurrentPage.ShouldBe(requiredPage);
        result.FirstItemOnPage.ShouldBe(expectedFirstItemOnPage);
        result.MaxPages.ShouldBe(expectedMaxPages);
    }

    [Test]
    public async Task ApplyPaginationAsync_NoMatch_ReturnsAnEmptyPageWithMinusOneMarkers()
    {
        var filter = AllRanges(new GroupFilter { RequiredPage = 0, NumberOfItemsPerPage = 2, SearchString = "nomatch" });

        var result = await RunAsync(filter);

        result.Groups.ShouldBeEmpty();
        result.MaxItems.ShouldBe(0);
        result.FirstItemOnPage.ShouldBe(-1);
        result.MaxPages.ShouldBe(-1);
    }

    [Test]
    public async Task KnownGap_NoDateRangeSelected_FailsBecauseTheEmptyInMemoryQueryCannotBeCountedAsync()
    {
        var filter = PageFilter();

        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(filter));
    }

    private async Task<TruncatedGroup> RunAsync(GroupFilter filter)
    {
        var filtered = _service.ApplyFilters(SeededQuery(), filter, Today);
        return await _service.ApplyPaginationAsync(filtered, filter);
    }

    private IQueryable<Group> SeededQuery()
    {
        var ids = _rankById.Keys.ToList();
        return _context.Group
            .Include(group => group.GroupItems)
            .ThenInclude(item => item.Client)
            .AsNoTracking()
            .Where(group => ids.Contains(group.Id))
            .OrderBy(group => group.Lft);
    }

    private static GroupFilter PageFilter() => new() { RequiredPage = 0, NumberOfItemsPerPage = SeededCount * 2 };

    private static GroupFilter AllRanges(GroupFilter filter)
    {
        filter.ActiveDateRange = true;
        filter.FormerDateRange = true;
        filter.FutureDateRange = true;
        return filter;
    }

    private int[] Ranks(TruncatedGroup result) => result.Groups.Select(group => _rankById[group.Id]).ToArray();

    private void AddGroup(int rank, string name, string description, DateTime validFrom, DateTime? validUntil)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = Prefix + name,
            Description = description,
            ValidFrom = DateTime.SpecifyKind(validFrom, DateTimeKind.Utc),
            ValidUntil = validUntil is { } until ? DateTime.SpecifyKind(until, DateTimeKind.Utc) : null,
            Lft = NestedSetOffset + (rank * 2),
            Rgt = NestedSetOffset + (rank * 2) + 1,
        };
        _context.Group.Add(group);
        _rankById[group.Id] = rank;
    }

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE group_id IN (SELECT id FROM \"group\" WHERE starts_with(name, {0}))", Prefix);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM \"group\" WHERE starts_with(name, {0})", Prefix);
    }
}
