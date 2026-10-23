// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Settings;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Pagination;

/// <summary>
/// Pins, against real PostgreSQL, the calendar-rule list of SettingsRepository.GetTruncatedCalendarRuleList end to end
/// (state/country filter, sorting, pagination in one database query): only selected state|country tokens restrict the
/// list, no selected token keeps every row, name and description sort by the requested language
/// (jsonb_extract_path_text), country and state sort by their columns. Every rule carries the
/// INTEGRATION_TEST_CalendarRuleSorting_ prefix in its German name and a seeded-only country, so the selected tokens
/// reach only the seeded rows of the shared database; the cleanup deletes exactly those rows.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class CalendarRuleFilterSortingPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_CalendarRuleSorting_";
    private const string CountryPrefix = "INTEGRATION_TEST_RF_";
    private const string StatePrefix = "INTEGRATION_TEST_RF_S";
    private const string Ascending = "asc";
    private const string Descending = "desc";
    private const int PageSize = 10;

    private DataBaseContext _context = null!;
    private SettingsRepository _repository = null!;
    private Dictionary<Guid, int> _rankById = null!;
    private List<(string Country, string State)> _combinations = null!;

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
        _combinations = [];
        Seed(1, "B", "3", "A", "C", "B");
        Seed(2, "A", "1", "C", "B", "A");
        Seed(3, "C", "2", "B", "A", "C");

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _repository = new SettingsRepository(
            _context,
            new CalendarRuleFilterService(),
            new CalendarRuleSortingService(),
            new CalendarRulePaginationService(),
            Substitute.For<IMacroManagementService>());
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [TestCase("name", Ascending, "de", new[] { 1, 3, 2 })]
    [TestCase("name", Descending, "de", new[] { 2, 3, 1 })]
    [TestCase("name", Ascending, "en", new[] { 3, 2, 1 })]
    [TestCase("country", Ascending, "de", new[] { 2, 1, 3 })]
    [TestCase("country", Descending, "de", new[] { 3, 1, 2 })]
    [TestCase("state", Ascending, "de", new[] { 2, 3, 1 })]
    [TestCase("state", Descending, "de", new[] { 1, 3, 2 })]
    [TestCase("description", Ascending, "de", new[] { 2, 1, 3 })]
    public async Task GetTruncatedCalendarRuleList_SortsTheSelectedRulesInTheDatabase(
        string orderBy, string sortOrder, string language, int[] expectedRanks)
    {
        var filter = Filter(orderBy, sortOrder, language, _combinations.Select(combination => Token(combination, true)));

        var result = await _repository.GetTruncatedCalendarRuleList(filter);

        result.MaxItems.ShouldBe(expectedRanks.Length);
        result.CalendarRules.Select(rule => _rankById[rule.Id]).ShouldBe(expectedRanks);
    }

    [Test]
    public async Task GetTruncatedCalendarRuleList_KeepsOnlySelectedTokens()
    {
        var tokens = new[]
        {
            Token(_combinations[0], false),
            Token(_combinations[1], true),
            Token(_combinations[2], true),
        };

        var result = await _repository.GetTruncatedCalendarRuleList(Filter("country", Ascending, "de", tokens));

        result.MaxItems.ShouldBe(2);
        result.CalendarRules.Select(rule => _rankById[rule.Id]).ShouldBe(new[] { 2, 3 });
    }

    [Test]
    public async Task ApplyFilters_WithoutSelectedToken_KeepsEveryRow()
    {
        var filter = Filter("country", Ascending, "de", _combinations.Select(combination => Token(combination, false)));

        var ids = await new CalendarRuleFilterService().ApplyFilters(SeededQuery(), filter)
            .Select(rule => rule.Id)
            .ToListAsync();

        ids.Select(id => _rankById[id]).Order().ShouldBe(new[] { 1, 2, 3 });
    }

    private void Seed(int rank, string countrySuffix, string stateSuffix, string nameDe, string nameEn, string descriptionDe)
    {
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Country = CountryPrefix + countrySuffix,
            State = StatePrefix + stateSuffix,
            Rule = "R" + rank,
            Name = new MultiLanguage { De = Prefix + nameDe, En = Prefix + nameEn },
            Description = new MultiLanguage { De = Prefix + descriptionDe },
        };
        _context.CalendarRule.Add(rule);
        _rankById[rule.Id] = rank;
        _combinations.Add((rule.Country, rule.State));
    }

    private static CalendarRulesFilter Filter(string orderBy, string sortOrder, string language, IEnumerable<StateCountryToken> tokens)
    {
        return new CalendarRulesFilter
        {
            OrderBy = orderBy,
            SortOrder = sortOrder,
            Language = language,
            RequiredPage = 0,
            NumberOfItemsPerPage = PageSize,
            List = tokens.ToList(),
        };
    }

    private static StateCountryToken Token((string Country, string State) combination, bool select)
    {
        return new StateCountryToken
        {
            Id = Guid.NewGuid(),
            Country = combination.Country,
            State = combination.State,
            CountryName = new MultiLanguage(),
            StateName = new MultiLanguage(),
            Select = select,
        };
    }

    private IQueryable<CalendarRule> SeededQuery()
    {
        var ids = _rankById.Keys.ToList();
        return _context.CalendarRule.AsNoTracking().Where(rule => ids.Contains(rule.Id));
    }

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM calendar_rule WHERE starts_with(name->>'de', {0})", Prefix);
    }
}
