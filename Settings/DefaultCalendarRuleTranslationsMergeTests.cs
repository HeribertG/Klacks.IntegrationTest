// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the default holiday name merge that runs when a language plugin is installed and on
/// every startup backfill: it adds the pack language to the multilingual name of the pre-seeded holiday rules
/// listed under one master entry (which ship with the core languages only), lower-cases mixed-case locale codes
/// (zh-CN), fills a language that is present but empty, never overwrites a name that already carries the
/// language, and on uninstall removes the language only where the name still equals the pack value.
/// </summary>

using System.Text.Json;
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

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class DefaultCalendarRuleTranslationsMergeTests
{
    private const string JapaneseName = "クリスマス";
    private const string ChineseName = "圣诞节";
    private const string CustomerName = "Eigener Name";
    private const string NamePrefix = "INTEGRATION_TEST_HOLIDAY";
    private const string OriginalPrefix = NamePrefix + "_ORIGINAL";
    private const string EditedPrefix = NamePrefix + "_EDITED";
    private const string TestCountry = "CH";
    private const string TestState = "ZH";
    private const string ChristmasRule = "12/25";
    private const string EmptyJapaneseName = "{\"ja\":\"\"}";

    private string _connectionString = null!;
    private string _pluginDirectory = null!;
    private readonly Guid _firstCantonId = Guid.NewGuid();
    private readonly Guid _secondCantonId = Guid.NewGuid();
    private readonly Guid _editedId = Guid.NewGuid();

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private IServiceScope ScopeFor(DataBaseContext context)
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

        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-calendar-rule-master-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginDirectory);

        var master = new
        {
            calendarRules = new[]
            {
                new
                {
                    en = "Christmas Day",
                    ids = new[] { _firstCantonId.ToString(), _secondCantonId.ToString(), _editedId.ToString() },
                    name = new Dictionary<string, string> { ["ja"] = JapaneseName, ["zh-cn"] = ChineseName }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(_pluginDirectory, "default-calendar-rule-translations.json"),
            JsonSerializer.Serialize(master));
    }

    [SetUp]
    public async Task SetUp()
    {
        await CleanupAsync();

        var edited = BuildCoreName(EditedPrefix);
        edited.SetValue("ja", CustomerName);

        await using var ctx = NewContext();
        ctx.CalendarRule.Add(BuildRule(_firstCantonId, BuildCoreName(OriginalPrefix)));
        ctx.CalendarRule.Add(BuildRule(_secondCantonId, BuildCoreName(OriginalPrefix)));
        ctx.CalendarRule.Add(BuildRule(_editedId, edited));
        await ctx.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Merge_AddsInstalledLanguageToEveryListedRule_PreservingCoreLanguages()
    {
        var installer = new LanguagePluginCalendarRuleNameInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultCalendarRuleTranslationsAsync(ScopeFor(ctx), "ja");
        }

        foreach (var id in new[] { _firstCantonId, _secondCantonId })
        {
            var rule = await ReadAsync(id);
            rule.Name.GetValue("ja").ShouldBe(JapaneseName);
            rule.Name.En.ShouldBe(OriginalPrefix + "-en", "core languages must be preserved");
        }
    }

    [Test]
    public async Task Merge_IsIdempotent_AndNeverOverwritesAnExistingName()
    {
        var installer = new LanguagePluginCalendarRuleNameInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultCalendarRuleTranslationsAsync(ScopeFor(ctx), "ja");
            await installer.MergeDefaultCalendarRuleTranslationsAsync(ScopeFor(ctx), "ja");
        }

        (await ReadAsync(_editedId)).Name.GetValue("ja").ShouldBe(CustomerName, "a customer-set name wins over the pack");
        (await ReadAsync(_firstCantonId)).Name.GetValue("ja").ShouldBe(JapaneseName);
    }

    [Test]
    public async Task Merge_FillsALanguageThatIsPresentButEmpty()
    {
        await using (var ctx = NewContext())
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE calendar_rule SET name = name || {1}::jsonb WHERE id = {0}", _firstCantonId, EmptyJapaneseName);
        }

        var installer = new LanguagePluginCalendarRuleNameInstaller(_pluginDirectory, NullLogger.Instance);
        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultCalendarRuleTranslationsAsync(ScopeFor(ctx), "ja");
        }

        (await ReadAsync(_firstCantonId)).Name.GetValue("ja").ShouldBe(JapaneseName);
    }

    [Test]
    public async Task Merge_LowerCasesMixedCaseLocale_ForChinese()
    {
        var installer = new LanguagePluginCalendarRuleNameInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultCalendarRuleTranslationsAsync(ScopeFor(ctx), "zh-CN");
        }

        (await ReadAsync(_firstCantonId)).Name.GetValue("zh-cn").ShouldBe(ChineseName);
    }

    [Test]
    public async Task Merge_IgnoresCoreLanguage()
    {
        var installer = new LanguagePluginCalendarRuleNameInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultCalendarRuleTranslationsAsync(ScopeFor(ctx), "de");
        }

        (await ReadAsync(_firstCantonId)).Name.De.ShouldBe(OriginalPrefix + "-de");
    }

    [Test]
    public async Task Remove_DropsOnlyUntouchedPackValue_KeepingCoreAndCustomerNames()
    {
        var installer = new LanguagePluginCalendarRuleNameInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultCalendarRuleTranslationsAsync(ScopeFor(ctx), "ja");
            await installer.RemoveDefaultCalendarRuleTranslationsAsync(ScopeFor(ctx), "ja");
        }

        var untouched = await ReadAsync(_firstCantonId);
        untouched.Name.GetValue("ja").ShouldBeNull();
        untouched.Name.De.ShouldBe(OriginalPrefix + "-de", "core languages must survive uninstall");
        (await ReadAsync(_editedId)).Name.GetValue("ja").ShouldBe(CustomerName, "a customer-set name must survive uninstall");
    }

    private async Task<CalendarRule> ReadAsync(Guid id)
    {
        await using var read = NewContext();
        return await read.CalendarRule.AsNoTracking().FirstAsync(r => r.Id == id);
    }

    private static CalendarRule BuildRule(Guid id, MultiLanguage name) => new()
    {
        Id = id,
        Rule = ChristmasRule,
        SubRule = string.Empty,
        Country = TestCountry,
        State = TestState,
        IsMandatory = true,
        IsPaid = true,
        Name = name,
        Description = new MultiLanguage(),
    };

    private static MultiLanguage BuildCoreName(string prefix)
    {
        var name = new MultiLanguage();
        name.De = prefix + "-de";
        name.En = prefix + "-en";
        name.Fr = prefix + "-fr";
        name.It = prefix + "-it";
        return name;
    }

    private async Task CleanupAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "DELETE FROM calendar_rule WHERE name ->> 'de' LIKE {0}", NamePrefix + "%");
    }
}
