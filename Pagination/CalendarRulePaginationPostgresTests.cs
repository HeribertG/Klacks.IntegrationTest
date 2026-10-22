// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Pagination;

/// <summary>
/// Pins, against real PostgreSQL, the paging numbers and the returned row order of the calendar rule pagination
/// service, which deliberately differs from the other list services: page 1 starts at item 0 like page 0,
/// FirstItemOnPage is the raw first item (never -1) and MaxPages is the plain quotient. The input query is
/// restricted to the five seeded rules, so counts never depend on other rows of the shared database. Every rule
/// carries the INTEGRATION_TEST_CalendarRulePaging_ prefix in its German name and the cleanup deletes exactly those.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class CalendarRulePaginationPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_CalendarRulePaging_";
    private const string Country = "INTEGRATION_TEST_RP";
    private const string RulePrefix = "R";
    private const int SeededCount = 5;

    private DataBaseContext _context = null!;
    private CalendarRulePaginationService _service = null!;
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
            var rule = new CalendarRule
            {
                Id = Guid.NewGuid(),
                Country = Country,
                State = Country,
                Rule = RulePrefix + rank,
                Name = new MultiLanguage { De = Prefix + rank },
            };
            _context.CalendarRule.Add(rule);
            _seededIds.Add(rule.Id);
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _service = new CalendarRulePaginationService();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [TestCase(0, 2, null, null, null, null, new[] { 1, 2 }, 0, 2)]
    [TestCase(1, 2, null, null, null, null, new[] { 1, 2 }, 0, 2)]
    [TestCase(2, 2, null, null, null, null, new[] { 5 }, 4, 2)]
    [TestCase(3, 2, null, null, null, null, new int[0], 6, 2)]
    [TestCase(0, 2, true, null, 2, null, new[] { 5 }, 4, 2)]
    [TestCase(0, 2, null, true, 1, null, new[] { 1, 2 }, 0, 2)]
    [TestCase(0, 2, null, true, 4, 3, new[] { 2, 3 }, 1, 2)]
    [TestCase(0, 5, null, null, null, null, new[] { 1, 2, 3, 4, 5 }, 0, 1)]
    [TestCase(1, 5, null, null, null, null, new[] { 1, 2, 3, 4, 5 }, 0, 1)]
    [TestCase(2, 5, null, null, null, null, new int[0], 10, 1)]
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
        var filter = new CalendarRulesFilter
        {
            RequiredPage = requiredPage,
            NumberOfItemsPerPage = pageSize,
            IsNextPage = isNextPage,
            IsPreviousPage = isPreviousPage,
            FirstItemOnLastPage = firstItemOnLastPage,
            NumberOfItemOnPreviousPage = numberOfItemOnPreviousPage,
        };

        var result = await _service.ApplyPaginationAsync(SeededQuery(), filter);

        result.CalendarRules.Select(rule => rule.Rule).ShouldBe(expectedRanks.Select(rank => RulePrefix + rank));
        result.MaxItems.ShouldBe(SeededCount);
        result.CurrentPage.ShouldBe(requiredPage);
        result.FirstItemOnPage.ShouldBe(expectedFirstItemOnPage);
        result.MaxPages.ShouldBe(expectedMaxPages);
    }

    [TestCase(0, 0)]
    [TestCase(1, 0)]
    [TestCase(2, 4)]
    public async Task ApplyPaginationAsync_EmptyQuery_ReturnsNoRowsWithRawFirstItemAndZeroPages(
        int requiredPage, int expectedFirstItemOnPage)
    {
        var filter = new CalendarRulesFilter { RequiredPage = requiredPage, NumberOfItemsPerPage = 2 };
        var query = SeededQuery().Where(rule => rule.Rule == string.Empty);

        var result = await _service.ApplyPaginationAsync(query, filter);

        result.CalendarRules.ShouldBeEmpty();
        result.MaxItems.ShouldBe(0);
        result.FirstItemOnPage.ShouldBe(expectedFirstItemOnPage);
        result.MaxPages.ShouldBe(0);
    }

    private IQueryable<CalendarRule> SeededQuery() => _context.CalendarRule
        .AsNoTracking()
        .Where(rule => _seededIds.Contains(rule.Id))
        .OrderBy(rule => rule.Rule);

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM calendar_rule WHERE starts_with(name->>'de', {0})", Prefix);
    }
}
