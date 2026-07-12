// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the default geo-translation merge that runs when a language plugin is
/// installed: it adds the installed language to the multilingual name of the pre-seeded default
/// countries and states (which ship with the core languages only), reads from the shared master
/// file, lower-cases mixed-case locale codes (zh-CN), and removes exactly that language on uninstall.
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
public class DefaultGeoTranslationsMergeTests
{
    private const string AbbreviationPrefix = "IT_GEO";

    private string _connectionString = null!;
    private string _pluginDirectory = null!;
    private readonly Guid _countryId = Guid.NewGuid();
    private readonly Guid _stateId = Guid.NewGuid();

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

        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-geo-master-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginDirectory);

        var master = new
        {
            countries = new[]
            {
                new { id = _countryId.ToString(), name = new Dictionary<string, string> { ["pl"] = "TestlandPL", ["zh-cn"] = "测试国" } }
            },
            states = new[]
            {
                new { id = _stateId.ToString(), name = new Dictionary<string, string> { ["pl"] = "TeststaatPL", ["zh-cn"] = "测试州" } }
            }
        };
        File.WriteAllText(
            Path.Combine(_pluginDirectory, "default-geo-translations.json"),
            JsonSerializer.Serialize(master));
    }

    [SetUp]
    public async Task SetUp()
    {
        await CleanupAsync();

        await using var ctx = NewContext();
        ctx.Countries.Add(new Countries
        {
            Id = _countryId,
            Abbreviation = AbbreviationPrefix + "C",
            Prefix = "+999",
            Name = BuildCoreName("Original")
        });
        ctx.State.Add(new State
        {
            Id = _stateId,
            Abbreviation = AbbreviationPrefix + "S",
            CountryPrefix = AbbreviationPrefix,
            Name = BuildCoreName("Original")
        });
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
    public async Task Merge_AddsInstalledLanguage_ToDefaultCountryAndState()
    {
        var installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultGeoTranslationsAsync(ScopeFor(ctx), "pl");
        }

        await using var read = NewContext();
        var country = await read.Countries.IgnoreQueryFilters().FirstAsync(c => c.Id == _countryId);
        var state = await read.State.IgnoreQueryFilters().FirstAsync(s => s.Id == _stateId);

        country.Name.GetValue("pl").ShouldBe("TestlandPL");
        state.Name.GetValue("pl").ShouldBe("TeststaatPL");
        country.Name.En.ShouldBe("Original-en", "core languages must be preserved");
    }

    [Test]
    public async Task Merge_LowerCasesMixedCaseLocale_ForChinese()
    {
        var installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultGeoTranslationsAsync(ScopeFor(ctx), "zh-CN");
        }

        await using var read = NewContext();
        var state = await read.State.IgnoreQueryFilters().FirstAsync(s => s.Id == _stateId);
        state.Name.GetValue("zh-cn").ShouldBe("测试州");
    }

    [Test]
    public async Task Remove_DropsOnlyThatLanguage_KeepingCore()
    {
        var installer = new LanguagePluginContentInstaller(_pluginDirectory, NullLogger.Instance);
        var scopeContext = NewContext();

        await installer.MergeDefaultGeoTranslationsAsync(ScopeFor(scopeContext), "pl");
        await installer.RemoveDefaultGeoTranslationsAsync(ScopeFor(scopeContext), "pl");
        await scopeContext.DisposeAsync();

        await using var read = NewContext();
        var state = await read.State.IgnoreQueryFilters().FirstAsync(s => s.Id == _stateId);

        state.Name.GetValue("pl").ShouldBeNull();
        state.Name.De.ShouldBe("Original-de", "core languages must survive uninstall");
    }

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
            "DELETE FROM state WHERE id = {0} OR abbreviation LIKE {1}", _stateId, AbbreviationPrefix + "%");
        await ctx.Database.ExecuteSqlRawAsync(
            "DELETE FROM countries WHERE id = {0} OR abbreviation LIKE {1}", _countryId, AbbreviationPrefix + "%");
    }
}
