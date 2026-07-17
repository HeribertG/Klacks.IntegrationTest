using System.Data;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.AspNetCore.Identity;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.RegionSetup;

/// <summary>
/// End-to-end pilot for the Netherlands region-setup profile. Drives the REAL
/// RegionSetupService.ApplyAsync against a throwaway database (klacks_nl_pilot) — never the
/// shared klacks dev database — to prove what the unit-test mocks hide: the nl language plugin
/// install seeds NL calendar_selection rows, the locale section resolves calendarSelection
/// (NL,NL), and the entity-import sections write period_cap_rule / scheduling_rule /
/// qualification rows plus the region-setup settings. Explicit so it never runs in the normal
/// suite; it requires the dedicated throwaway database to exist.
/// </summary>
[TestFixture]
[Explicit("NL region-setup pilot — runs only against the throwaway database klacks_nl_pilot")]
public class NlRegionSetupPilotTests
{
    private const string ThrowawayDatabaseName = "klacks_nl_pilot";
    private const string ConnectionString =
        "Host=localhost;Port=5434;Database=klacks_nl_pilot;Username=postgres;Password=admin;Pooling=false";

    [Test]
    public async Task ApplyNlProfile_AgainstThrowawayDb_SeedsCalendarSettingsAndEntities()
    {
        // Safety gate: refuse to touch anything but the throwaway database.
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        target.Database.ShouldBe(ThrowawayDatabaseName,
            "pilot must only ever run against the throwaway database, never the shared klacks db");

        var repoRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
        var nlProfilePath = Path.Combine(repoRoot, "Klacks.Api", "deploy", "onprem", "regions", "nl.json");
        var pluginsDirectory = Path.Combine(repoRoot, "Klacks.Api", "Plugins", "Languages");
        File.Exists(nlProfilePath).ShouldBeTrue($"nl.json not found at {nlProfilePath}");
        Directory.Exists(pluginsDirectory).ShouldBeTrue($"language plugins not found at {pluginsDirectory}");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ConnectionString,
                ["RegionSetup:File"] = nlProfilePath,
                ["LanguagePlugins:Directory"] = pluginsDirectory,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddDbContext<DataBaseContext>(options => options
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddIdentity<AppUser, IdentityRole>(options => options.SignIn.RequireConfirmedAccount = true)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<DataBaseContext>()
            .AddDefaultTokenProviders();
        services.AddApplicationServices(configuration);

        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
            await context.Database.MigrateAsync();
        }

        // The nl plugin must be discovered before ApplyAsync installs it (populates _manifests).
        var pluginService = provider.GetRequiredService<ILanguagePluginService>();
        await pluginService.InitializeAsync();

        await using (var scope = provider.CreateAsyncScope())
        {
            var regionSetup = scope.ServiceProvider.GetRequiredService<IRegionSetupService>();
            await regionSetup.ApplyAsync();
        }

        await VerifyDatabaseAsync();
    }

    private static async Task VerifyDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        (await ScalarAsync(connection, "SELECT current_database()")).ShouldBe(ThrowawayDatabaseName);

        // 1. NL calendar selection seeded by the nl plugin geo installer (country-wide (NL,NL) pair).
        var nlCalendarCount = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT count(*) FROM selected_calendar WHERE country = 'NL' AND state = 'NL'"));
        nlCalendarCount.ShouldBeGreaterThan(0L, "nl plugin must seed at least one (NL,NL) calendar pair");

        // 2. The two statutory rolling-average period caps (16w/48h and 4w/55h).
        var periodCapCount = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT count(*) FROM period_cap_rule WHERE import_source_key LIKE 'region-setup:%' AND is_deleted = false"));
        periodCapCount.ShouldBe(2L, "nl.json defines exactly two rolling-average period caps");

        // 3. Industry scheduling-rule presets imported (5 industries, one preset each).
        var presetCount = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT count(*) FROM scheduling_rules WHERE import_source_key LIKE 'region-setup:%' AND is_deleted = false"));
        presetCount.ShouldBeGreaterThanOrEqualTo(5L, "each of the five NL industry blocks imports one preset");

        // 4. Qualifications imported.
        var qualificationCount = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT count(*) FROM qualification WHERE import_source_key LIKE 'region-setup:%' AND is_deleted = false"));
        qualificationCount.ShouldBeGreaterThan(0L, "NL industry blocks import qualification catalog rows");

        // 5. Region-setup settings written: default language, resolved calendar selection, applied markers.
        var defaultLanguage = (string?)await ScalarAsync(connection,
            "SELECT value FROM settings WHERE type = 'DEFAULT_LANGUAGE' LIMIT 1");
        defaultLanguage.ShouldBe("nl");

        var calendarSelectionSetting = await ScalarAsync(connection,
            "SELECT value FROM settings WHERE type = 'globalCalendarSelectionId' LIMIT 1");
        calendarSelectionSetting.ShouldNotBeNull();

        var appliedMarkerCount = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT count(*) FROM settings WHERE type LIKE 'REGION_SETUP_APPLIED%'"));
        appliedMarkerCount.ShouldBeGreaterThan(0L, "region setup must write its applied markers");
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
}
