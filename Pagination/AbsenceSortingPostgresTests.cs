// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Absences;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Pagination;

/// <summary>
/// Pins, against real PostgreSQL, the absence list sorting: name and description are sorted by the text of the
/// requested language (MultiLanguageDbFunctions.ExtractText, mapped to jsonb_extract_path_text, so the ORDER BY runs
/// in the database), the language key is case-insensitive, and an unknown key or an empty sort order keeps the
/// incoming order. The input query is restricted to the three seeded absences; every absence carries the
/// INTEGRATION_TEST_AbsenceSorting_ prefix in its German name and the cleanup deletes exactly those.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class AbsenceSortingPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_AbsenceSorting_";
    private const string Ascending = "asc";
    private const string Descending = "desc";

    private DataBaseContext _context = null!;
    private AbsenceSortingService _service = null!;
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
        Seed(1, "B", "C", "C", "A");
        Seed(2, "A", "B", "B", "C");
        Seed(3, "C", "A", "A", "B");

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _service = new AbsenceSortingService();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [TestCase("name", Ascending, "de", new[] { 2, 1, 3 })]
    [TestCase("name", Descending, "de", new[] { 3, 1, 2 })]
    [TestCase("name", Ascending, "en", new[] { 3, 2, 1 })]
    [TestCase("NAME", Ascending, "DE", new[] { 2, 1, 3 })]
    [TestCase("description", Ascending, "de", new[] { 3, 2, 1 })]
    [TestCase("description", Descending, "en", new[] { 2, 3, 1 })]
    [TestCase("unknown_key", Ascending, "de", new[] { 1, 2, 3 })]
    [TestCase("name", "", "de", new[] { 1, 2, 3 })]
    public async Task ApplySorting_PinsTheOrderInTheDatabase(string orderBy, string sortOrder, string language, int[] expectedRanks)
    {
        var query = _service.ApplySorting(SeededQuery(), orderBy, sortOrder, language);

        var ids = await query.Select(absence => absence.Id).ToListAsync();

        ids.Select(id => _rankById[id]).ShouldBe(expectedRanks);
    }

    [Test]
    public void ApplySorting_ByName_IsTranslatedToSql()
    {
        var sql = _service.ApplySorting(SeededQuery(), "name", Ascending, "de").ToQueryString();

        sql.ShouldContain("ORDER BY jsonb_extract_path_text(");
    }

    private void Seed(int rank, string nameDe, string nameEn, string descriptionDe, string descriptionEn)
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = Prefix + nameDe, En = Prefix + nameEn },
            Description = new MultiLanguage { De = Prefix + descriptionDe, En = Prefix + descriptionEn },
            Abbreviation = new MultiLanguage(),
            DefaultLength = rank,
        };
        _context.Absence.Add(absence);
        _rankById[absence.Id] = rank;
    }

    private IQueryable<Absence> SeededQuery()
    {
        var ids = _rankById.Keys.ToList();
        return _context.Absence
            .AsNoTracking()
            .Where(absence => ids.Contains(absence.Id))
            .OrderBy(absence => absence.DefaultLength);
    }

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM absence WHERE starts_with(name->>'de', {0})", Prefix);
    }
}
