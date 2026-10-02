// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the default qualification merge that runs when a language plugin is installed and
/// on every startup backfill: it adds the pack language to the multilingual name of a pre-seeded qualification
/// (which ships with the core languages only), reads the shared master file, lower-cases mixed-case locale
/// codes (zh-CN), never overwrites a name that already carries the language, and on uninstall removes the
/// language only where the name still equals the pack value.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Staffs;
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
public class DefaultQualificationTranslationsMergeTests
{
    private const string PolishName = "Wozek widlowy";
    private const string ChineseName = "叉车证";
    private const string CustomerName = "Eigener Name";
    private const string NamePrefix = "INTEGRATION_TEST_QUAL";
    private const string OriginalPrefix = NamePrefix + "_ORIGINAL";
    private const string EditedPrefix = NamePrefix + "_EDITED";

    private string _connectionString = null!;
    private string _pluginDirectory = null!;
    private readonly Guid _qualificationId = Guid.NewGuid();
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

        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-qualification-master-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginDirectory);

        var master = new
        {
            qualifications = new[]
            {
                new
                {
                    id = _qualificationId.ToString(),
                    name = new Dictionary<string, string> { ["pl"] = PolishName, ["zh-cn"] = ChineseName }
                },
                new
                {
                    id = _editedId.ToString(),
                    name = new Dictionary<string, string> { ["pl"] = PolishName }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(_pluginDirectory, "default-qualification-translations.json"),
            JsonSerializer.Serialize(master));
    }

    [SetUp]
    public async Task SetUp()
    {
        await CleanupAsync();

        var edited = BuildCoreName(EditedPrefix);
        edited.SetValue("pl", CustomerName);

        await using var ctx = NewContext();
        ctx.Qualification.Add(new Qualification { Id = _qualificationId, Name = BuildCoreName(OriginalPrefix) });
        ctx.Qualification.Add(new Qualification { Id = _editedId, Name = edited });
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
    public async Task Merge_AddsInstalledLanguage_PreservingCoreLanguages()
    {
        var installer = new LanguagePluginQualificationInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultQualificationTranslationsAsync(ScopeFor(ctx), "pl");
        }

        var row = await ReadAsync(_qualificationId);
        row.Name.GetValue("pl").ShouldBe(PolishName);
        row.Name.En.ShouldBe(OriginalPrefix + "-en", "core languages must be preserved");
    }

    [Test]
    public async Task Merge_IsIdempotent_AndNeverOverwritesAnExistingName()
    {
        var installer = new LanguagePluginQualificationInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultQualificationTranslationsAsync(ScopeFor(ctx), "pl");
            await installer.MergeDefaultQualificationTranslationsAsync(ScopeFor(ctx), "pl");
        }

        (await ReadAsync(_editedId)).Name.GetValue("pl").ShouldBe(CustomerName, "a customer-set name wins over the pack");
        (await ReadAsync(_qualificationId)).Name.GetValue("pl").ShouldBe(PolishName);
    }

    [Test]
    public async Task Merge_LowerCasesMixedCaseLocale_ForChinese()
    {
        var installer = new LanguagePluginQualificationInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultQualificationTranslationsAsync(ScopeFor(ctx), "zh-CN");
        }

        (await ReadAsync(_qualificationId)).Name.GetValue("zh-cn").ShouldBe(ChineseName);
    }

    [Test]
    public async Task Merge_IgnoresCoreLanguage()
    {
        var installer = new LanguagePluginQualificationInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultQualificationTranslationsAsync(ScopeFor(ctx), "de");
        }

        (await ReadAsync(_qualificationId)).Name.De.ShouldBe(OriginalPrefix + "-de");
    }

    [Test]
    public async Task Remove_DropsOnlyUntouchedPackValue_KeepingCoreAndCustomerNames()
    {
        var installer = new LanguagePluginQualificationInstaller(_pluginDirectory, NullLogger.Instance);

        await using (var ctx = NewContext())
        {
            await installer.MergeDefaultQualificationTranslationsAsync(ScopeFor(ctx), "pl");
            await installer.RemoveDefaultQualificationTranslationsAsync(ScopeFor(ctx), "pl");
        }

        var untouched = await ReadAsync(_qualificationId);
        untouched.Name.GetValue("pl").ShouldBeNull();
        untouched.Name.De.ShouldBe(OriginalPrefix + "-de", "core languages must survive uninstall");
        (await ReadAsync(_editedId)).Name.GetValue("pl").ShouldBe(CustomerName, "a customer-set name must survive uninstall");
    }

    private async Task<Qualification> ReadAsync(Guid id)
    {
        await using var read = NewContext();
        return await read.Qualification.IgnoreQueryFilters().AsNoTracking().FirstAsync(q => q.Id == id);
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
            "DELETE FROM qualification WHERE name ->> 'de' LIKE {0}", NamePrefix + "%");
    }
}
