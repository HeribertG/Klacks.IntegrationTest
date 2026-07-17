using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.RegionSetup;

/// <summary>
/// Boots a representative sample of the shipped region profiles end-to-end against a fresh throwaway
/// database (never the shared klacks dev database) to prove what the mocked unit tests cannot: the
/// language plugin installs, its geo installer seeds resolvable calendar selections, the country's
/// public-holiday rules are present (the AE case is the regression guard for the empty-calendar bug),
/// the locale section resolves the global calendar selection, and the entity imports write their rows.
/// </summary>
[TestFixture]
[Explicit("Region-profile boot sample — creates and drops its own throwaway databases on localhost:5434")]
public class RegionProfileBootSampleTests
{
    private const string Host = "localhost";
    private const string Port = "5434";
    private const string User = "postgres";
    private const string Password = "admin";

    private static string MaintenanceConnectionString =>
        $"Host={Host};Port={Port};Database=postgres;Username={User};Password={Password};Pooling=false";

    private static string DbConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    // region file, calendar country code (may differ from region), expected DEFAULT_LANGUAGE value,
    // and the plugin whose INSTALLED_LANGUAGE marker must be set (null for core-only, no plugin install).
    // AE keeps English as the UI default but installs the ar plugin purely for its calendar data.
    private static IEnumerable<TestCaseData> Sample()
    {
        // profile file, calendar country, expected DEFAULT_LANGUAGE, INSTALLED_LANGUAGE plugin (null = core),
        // minimum imported scheduling rules (ae.json is the minimal K16 showcase without a 5-industry block).
        yield return new TestCaseData("ae.json", "AE", "en", "ar", 0).SetName("AE (calendar regression guard)");
        yield return new TestCaseData("li.json", "LI", "de", null, 5).SetName("LI (core de, base seed)");
        yield return new TestCaseData("gb.json", "GB", "en", null, 5).SetName("GB (core en, base seed)");
        yield return new TestCaseData("us.json", "USA", "en", null, 5).SetName("US (federal calendar via NY)");
        yield return new TestCaseData("es.json", "ES", "es", "es", 5).SetName("ES");
        yield return new TestCaseData("pt.json", "PT", "pt", "pt", 5).SetName("PT");
        yield return new TestCaseData("cz.json", "CZ", "cs", "cs", 5).SetName("CZ");
        yield return new TestCaseData("pl.json", "PL", "pl", "pl", 5).SetName("PL");
        yield return new TestCaseData("ro.json", "RO", "ro", "ro", 5).SetName("RO");
        yield return new TestCaseData("gr.json", "GR", "el", "el", 5).SetName("GR");
        yield return new TestCaseData("il.json", "IL", "he", "he", 5).SetName("IL (Fri-Sat weekend)");
        yield return new TestCaseData("sa.json", "SA", "ar", "ar", 5).SetName("SA (Fri-Sat weekend)");
        yield return new TestCaseData("cn.json", "CN", "zh-CN", "zh-CN", 5).SetName("CN");
        yield return new TestCaseData("tw.json", "TW", "zh-TW", "zh-TW", 5).SetName("TW");
        yield return new TestCaseData("jp.json", "JP", "ja", "ja", 5).SetName("JP");
        yield return new TestCaseData("kr.json", "KR", "ko", "ko", 5).SetName("KR");
        yield return new TestCaseData("th.json", "TH", "th", "th", 5).SetName("TH");
        yield return new TestCaseData("vn.json", "VN", "vi", "vi", 5).SetName("VN");
        yield return new TestCaseData("id.json", "ID", "id", "id", 5).SetName("ID");
        yield return new TestCaseData("my.json", "MY", "ms", "ms", 5).SetName("MY");
    }

    [TestCaseSource(nameof(Sample))]
    public async Task ShippedProfile_BootsAndSeedsCalendarLanguageAndRules(
        string profileFile, string calendarCountry, string defaultLanguage, string? markerLanguage, int minImportedRules)
    {
        var dbName = $"klacks_boot_{Path.GetFileNameWithoutExtension(profileFile)}";
        new NpgsqlConnectionStringBuilder(DbConnectionString(dbName)).Database.ShouldStartWith("klacks_boot_");

        await RecreateDatabaseAsync(dbName);
        try
        {
            var repoRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
            var profilePath = Path.Combine(repoRoot, "Klacks.Api", "deploy", "onprem", "regions", profileFile);
            var pluginsDirectory = Path.Combine(repoRoot, "Klacks.Api", "Plugins", "Languages");
            File.Exists(profilePath).ShouldBeTrue($"profile not found: {profilePath}");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = DbConnectionString(dbName),
                    ["RegionSetup:File"] = profilePath,
                    ["LanguagePlugins:Directory"] = pluginsDirectory,
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddDataProtection();
            services.AddHttpContextAccessor();
            services.AddDbContext<DataBaseContext>(o => o
                .UseNpgsql(DbConnectionString(dbName))
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
            services.AddIdentity<AppUser, IdentityRole>(o => o.SignIn.RequireConfirmedAccount = true)
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<DataBaseContext>()
                .AddDefaultTokenProviders();
            services.AddApplicationServices(configuration);
            services.AddDatabaseInitializer();

            await using var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DataBaseContext>().Database.MigrateAsync();
            }

            // Base calendar selections (LI/GB/USA and the core countries) are seeded at runtime by the
            // DatabaseInitializer, not by the migration; mirror that so base-seed countries resolve too.
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().SeedDataAsync();
            }

            await provider.GetRequiredService<ILanguagePluginService>().InitializeAsync();

            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IRegionSetupService>().ApplyAsync();
            }

            await VerifyAsync(dbName, calendarCountry, defaultLanguage, markerLanguage, minImportedRules);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static async Task VerifyAsync(
        string dbName, string calendarCountry, string defaultLanguage, string? markerLanguage, int minImportedRules)
    {
        await using var connection = new NpgsqlConnection(DbConnectionString(dbName));
        await connection.OpenAsync();

        var calendarPairs = Convert.ToInt64(await ScalarAsync(connection,
            $"SELECT count(*) FROM selected_calendar WHERE country = '{calendarCountry}'"));
        calendarPairs.ShouldBeGreaterThan(0L, $"no calendar selection seeded for {calendarCountry}");

        var holidays = Convert.ToInt64(await ScalarAsync(connection,
            $"SELECT count(*) FROM calendar_rule WHERE country = '{calendarCountry}'"));
        holidays.ShouldBeGreaterThan(0L, $"no public-holiday rules present for {calendarCountry}");

        var calendarResolved = await ScalarAsync(connection,
            "SELECT value FROM settings WHERE type = 'globalCalendarSelectionId' AND value <> ''");
        calendarResolved.ShouldNotBeNull("locale section did not resolve a global calendar selection");

        (await ScalarAsync(connection, "SELECT value FROM settings WHERE type = 'DEFAULT_LANGUAGE'"))
            .ShouldBe(defaultLanguage);

        if (markerLanguage is not null)
        {
            (await ScalarAsync(connection,
                $"SELECT value FROM settings WHERE type = 'INSTALLED_LANGUAGE_{markerLanguage.ToUpperInvariant()}'"))
                .ShouldBe("true");
        }

        var importedRules = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT count(*) FROM scheduling_rules WHERE import_source_key LIKE 'region-setup:%' AND is_deleted = false"));
        importedRules.ShouldBeGreaterThanOrEqualTo((long)minImportedRules, "unexpected number of imported industry presets");
    }

    private static async Task RecreateDatabaseAsync(string dbName)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        await ExecuteAsync(connection, $"CREATE DATABASE \"{dbName}\"");
    }

    private static async Task DropDatabaseAsync(string dbName)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
}
