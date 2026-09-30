// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the calendar rule merge of a language pack against PostgreSQL (raw SQL, so InMemory
/// cannot cover it): the pack's non-core translations reach its own row by id or, after a renumbering, by
/// country/state and English name, but never a row of another pack that holds the same id, and an entry
/// without English name only matches by id. Also proves the geo installer's calendar rule lookup translates
/// on Npgsql. Every row uses the INTEGRATION_TEST_ country prefix and is removed afterwards.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using System.Collections.Concurrent;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class LanguagePluginCalendarRuleMergeTests
{
    private const string Code = "pl";
    private const string PackCountry = "INTEGRATION_TEST_CR_PACK";
    private const string ForeignCountry = "INTEGRATION_TEST_CR_FOREIGN";
    private const string CleanupPattern = "INTEGRATION_TEST_CR_%";
    private const string PackHoliday = "Pack Holiday";
    private const string PackTranslation = "Święto testowe";

    private string _connectionString = null!;
    private string _pluginDirectory = null!;

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static IServiceScope ScopeFor(DataBaseContext context)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(DataBaseContext)).Returns(context);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        return scope;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [SetUp]
    public async Task SetUp()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-rule-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, Code));
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Merge_OwnRowById_AddsNonCoreTranslation()
    {
        var id = Guid.NewGuid();
        await GivenRuleAsync(id, PackCountry, PackHoliday);
        GivenPackRule(id, PackCountry, PackHoliday);

        await MergeAsync();

        (await ReadPolishNameAsync(id)).ShouldBe(PackTranslation);
    }

    [Test]
    public async Task Merge_IdHeldByForeignRow_LeavesForeignRowUntouched()
    {
        var id = Guid.NewGuid();
        await GivenRuleAsync(id, ForeignCountry, "Foreign Holiday");
        GivenPackRule(id, PackCountry, PackHoliday);

        await MergeAsync();

        (await ReadPolishNameAsync(id)).ShouldBeNull();
    }

    [Test]
    public async Task Merge_RenumberedEntry_ReachesOwnRowByEnglishName()
    {
        var storedId = Guid.NewGuid();
        await GivenRuleAsync(storedId, PackCountry, PackHoliday);
        GivenPackRule(Guid.NewGuid(), PackCountry, PackHoliday);

        await MergeAsync();

        (await ReadPolishNameAsync(storedId)).ShouldBe(PackTranslation);
    }

    [Test]
    public async Task Merge_EntryWithoutEnglishName_MatchesOnlyById()
    {
        var ownId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await GivenRuleAsync(ownId, PackCountry, null);
        await GivenRuleAsync(otherId, PackCountry, null);
        GivenPackRule(ownId, PackCountry, null);

        await MergeAsync();

        (await ReadPolishNameAsync(ownId)).ShouldBe(PackTranslation);
        (await ReadPolishNameAsync(otherId)).ShouldBeNull();
    }

    [Test]
    public async Task InstallGeoData_IdHeldByForeignRule_InsertsPackRuleUnderFreshId()
    {
        var id = Guid.NewGuid();
        await GivenRuleAsync(id, ForeignCountry, "Foreign Holiday");
        GivenPackRule(id, PackCountry, PackHoliday);
        var installer = new LanguagePluginGeoDataInstaller(
            _pluginDirectory, new ConcurrentDictionary<string, LanguagePluginManifest>(), NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.InstallGeoDataAsync(ScopeFor(ctx), Code);
            await ctx.SaveChangesAsync();
        }

        await using var read = NewContext();
        var foreign = await read.CalendarRule.AsNoTracking().SingleAsync(r => r.Id == id);
        foreign.Country.ShouldBe(ForeignCountry);
        var inserted = await read.CalendarRule.AsNoTracking().SingleAsync(r => r.Country == PackCountry);
        inserted.Id.ShouldNotBe(id);
        inserted.Name.En.ShouldBe(PackHoliday);
    }

    private async Task MergeAsync()
    {
        var installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);
        await using var ctx = NewContext();
        await installer.MergeNonCoreTranslationsAsync(ScopeFor(ctx), Code);
    }

    private async Task GivenRuleAsync(Guid id, string country, string? englishName)
    {
        await using var ctx = NewContext();
        var name = new MultiLanguage { De = "Test", En = englishName };
        ctx.CalendarRule.Add(new CalendarRule { Id = id, Country = country, State = country, Rule = "01/01", Name = name });
        await ctx.SaveChangesAsync();
    }

    private void GivenPackRule(Guid id, string country, string? englishName)
    {
        var english = englishName == null ? string.Empty : $"\"en\": \"{englishName}\", ";
        var json = $$"""
            [ { "id": "{{id}}", "rule": "01/01", "subRule": "", "isMandatory": false, "isPaid": false,
                "state": "{{country}}", "country": "{{country}}",
                "name": { {{english}}"de": "Test", "pl": "{{PackTranslation}}" }, "description": { "pl": "Opis" } } ]
            """;
        File.WriteAllText(Path.Combine(_pluginDirectory, Code, "calendar-rules.json"), json);
    }

    private async Task<string?> ReadPolishNameAsync(Guid id)
    {
        await using var ctx = NewContext();
        var rule = await ctx.CalendarRule.AsNoTracking().SingleAsync(r => r.Id == id);
        return rule.Name.GetValue(Code);
    }

    private async Task CleanupAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM calendar_rule WHERE country LIKE {0}", CleanupPattern);
    }
}
