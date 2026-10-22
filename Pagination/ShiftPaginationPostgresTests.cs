// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Pagination;

/// <summary>
/// Pins, against real PostgreSQL, the paging numbers and the returned row order of the shift pagination service
/// that ShiftQueryPipelineService ends with: page arithmetic, next/previous navigation from the last page's first
/// item (clamped at zero), a page past the end (FirstItemOnPage -1) and the empty result (MaxPages -1). The input
/// query is restricted to the five seeded shifts, so counts never depend on other rows of the shared database.
/// Every shift carries the INTEGRATION_TEST_ShiftPaging_ prefix and the cleanup deletes exactly those.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class ShiftPaginationPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_ShiftPaging_";
    private const string Abbreviation = "SPG";
    private const int SeededCount = 5;
    private static readonly DateOnly FirstDay = new(2031, 1, 1);

    private DataBaseContext _context = null!;
    private ShiftPaginationService _service = null!;
    private List<Guid> _seededIds = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();

        _seededIds = [];
        for (var rank = 1; rank <= SeededCount; rank++)
        {
            var shift = new Shift
            {
                Id = Guid.NewGuid(),
                Name = Prefix + rank,
                Abbreviation = Abbreviation,
                Description = Prefix,
                Status = ShiftStatus.OriginalShift,
                ShiftType = ShiftType.IsTask,
                FromDate = FirstDay.AddDays(rank),
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AfterShift = new TimeOnly(0, 0),
                BeforeShift = new TimeOnly(0, 0),
                IsMonday = true,
                Quantity = 1,
                SumEmployees = 1,
            };
            _context.Shift.Add(shift);
            _seededIds.Add(shift.Id);
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _service = new ShiftPaginationService();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
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
        var filter = new ShiftFilter
        {
            RequiredPage = requiredPage,
            NumberOfItemsPerPage = pageSize,
            IsNextPage = isNextPage,
            IsPreviousPage = isPreviousPage,
            FirstItemOnLastPage = firstItemOnLastPage,
            NumberOfItemOnPreviousPage = numberOfItemOnPreviousPage,
        };

        var result = await _service.ApplyPaginationAsync(SeededQuery(), filter);

        result.Shifts.Select(shift => shift.Name).ShouldBe(expectedRanks.Select(rank => Prefix + rank));
        result.MaxItems.ShouldBe(SeededCount);
        result.CurrentPage.ShouldBe(requiredPage);
        result.FirstItemOnPage.ShouldBe(expectedFirstItemOnPage);
        result.MaxPages.ShouldBe(expectedMaxPages);
    }

    [Test]
    public async Task ApplyPaginationAsync_EmptyQuery_ReturnsNoRowsWithMinusOneMarkers()
    {
        var filter = new ShiftFilter { RequiredPage = 0, NumberOfItemsPerPage = 2 };
        var query = SeededQuery().Where(shift => shift.FromDate < FirstDay);

        var result = await _service.ApplyPaginationAsync(query, filter);

        result.Shifts.ShouldBeEmpty();
        result.MaxItems.ShouldBe(0);
        result.FirstItemOnPage.ShouldBe(-1);
        result.MaxPages.ShouldBe(-1);
    }

    private IQueryable<Shift> SeededQuery() => _context.Shift
        .AsNoTracking()
        .Where(shift => _seededIds.Contains(shift.Id))
        .OrderBy(shift => shift.FromDate);

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE starts_with(name, {0})", Prefix);
    }
}
