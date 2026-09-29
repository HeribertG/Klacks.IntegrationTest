// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The AddUnofficialHolidayDescriptions data migration against real Postgres, up and down, on a throwaway
/// database (never on the shared one: the migration addresses fixed seed ids that cannot carry the
/// INTEGRATION_TEST_ prefix). The database is migrated to the migration before it and the two calendar rule
/// seeds are inserted the way an older build left them (empty descriptions). One described row is given an
/// administrator's own text and one is stored in the empty-object shape the API writes. After Up the empty
/// rows carry the new text, the edited row is untouched, and a mandatory row stays empty. After Down the
/// described rows are empty again while the edited row still keeps its own text; a second Up is idempotent.
/// </summary>

using Klacks.Api.Data.Seed;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class UnofficialHolidayDescriptionsMigrationTests
{
    private const string PreviousMigration = "20260927094736_RouteEmptyContainerDispatchesToContainerTemplate";
    private const string TheMigration = "20260929120000_AddUnofficialHolidayDescriptions";
    private const string ThrowawayPrefix = "klacks_boot_";

    private const string SwissChristmasEveAargau = "0ab12401-0001-0001-0001-000000000001";
    private const string SwissChristmasEveZurich = "0ab12401-0001-0001-0001-000000000026";
    private const string SwissNewYearsEveBern = "01231001-0001-0001-0001-000000000004";
    private const string SardiniaDay = "100bb001-0001-0001-0001-000000000001";
    private const string SwissNewYear = "613c22be-e39f-4a40-be5a-e1202d21678f";
    private const string OwnDescription = @"{""de"": ""INTEGRATION_TEST_ eigene Beschreibung""}";

    private const string Host = "localhost";
    private const int Port = 5434;
    private const string User = "postgres";
    private const string Password = "admin";

    private static string MaintenanceConnectionString =>
        $"Host={Host};Port={Port};Database=postgres;Username={User};Password={Password};Pooling=false";

    private static string DbConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    [Test]
    public async Task TheMigration_FillsOnlyEmptyDescriptions_AndDownRevertsOnlyItsOwnText()
    {
        var dbName = ThrowawayPrefix + "unofficial_holiday_" + Guid.NewGuid().ToString("N")[..8];
        dbName.ShouldStartWith(ThrowawayPrefix);
        await RecreateDatabaseAsync(dbName);
        try
        {
            await using var context = NewContext(dbName);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync(PreviousMigration);
            await SeedCalendarRulesAsync(dbName);
            await ExecuteAsync(dbName, $"UPDATE calendar_rule SET description = '{OwnDescription}'::jsonb WHERE id = '{SwissChristmasEveZurich}'");
            await ExecuteAsync(dbName, $"UPDATE calendar_rule SET description = '{{}}'::jsonb WHERE id = '{SwissNewYearsEveBern}'");

            await migrator.MigrateAsync(TheMigration);

            var eveHalfDay = UnofficialHolidayDescriptionTexts.EveHalfWorkday;
            (await DescriptionAsync(dbName, SwissChristmasEveAargau, "de")).ShouldBe(eveHalfDay["de"]);
            (await DescriptionAsync(dbName, SwissChristmasEveAargau, "zh-cn")).ShouldBe(eveHalfDay["zh-cn"]);
            (await DescriptionAsync(dbName, SwissNewYearsEveBern, "en")).ShouldBe(eveHalfDay["en"]);
            (await DescriptionAsync(dbName, SardiniaDay, "it")).ShouldBe(UnofficialHolidayDescriptionTexts.RegionalHoliday["it"]);
            (await DescriptionAsync(dbName, SwissChristmasEveZurich, "de")).ShouldBe("INTEGRATION_TEST_ eigene Beschreibung");
            (await DescriptionAsync(dbName, SwissChristmasEveZurich, "en")).ShouldBeNull();
            (await DescriptionAsync(dbName, SwissNewYear, "de")).ShouldBe(string.Empty);
            (await ScalarAsync(dbName, NonMandatoryWithoutDescriptionCount())).ShouldBe(6L);

            await migrator.MigrateAsync(PreviousMigration);

            (await DescriptionAsync(dbName, SwissChristmasEveAargau, "de")).ShouldBe(string.Empty);
            (await DescriptionAsync(dbName, SwissChristmasEveAargau, "zh-cn")).ShouldBeNull();
            (await DescriptionAsync(dbName, SardiniaDay, "it")).ShouldBe(string.Empty);
            (await DescriptionAsync(dbName, SwissChristmasEveZurich, "de")).ShouldBe("INTEGRATION_TEST_ eigene Beschreibung");

            await migrator.MigrateAsync(TheMigration);
            await ApplyAgainAsync(dbName);
            (await DescriptionAsync(dbName, SwissChristmasEveAargau, "de")).ShouldBe(eveHalfDay["de"]);
            (await DescriptionAsync(dbName, SwissChristmasEveZurich, "de")).ShouldBe("INTEGRATION_TEST_ eigene Beschreibung");
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static string NonMandatoryWithoutDescriptionCount() =>
        "SELECT count(*) FROM calendar_rule WHERE is_mandatory = false "
        + "AND NOT EXISTS (SELECT 1 FROM jsonb_each_text(description) AS d(key, value) WHERE d.value <> '')";

    private static async Task SeedCalendarRulesAsync(string dbName)
    {
        var builder = new MigrationBuilder(activeProvider: null);
        CalendarRulesSeed.SeedData(builder);
        AdditionalCalendarRulesSeed.SeedData(builder);
        foreach (var operation in builder.Operations.OfType<SqlOperation>())
        {
            await ExecuteAsync(dbName, operation.Sql);
        }
    }

    private static async Task ApplyAgainAsync(string dbName)
    {
        var builder = new MigrationBuilder(activeProvider: null);
        UnofficialHolidayDescriptionsSql.Apply(builder);
        foreach (var operation in builder.Operations.OfType<SqlOperation>())
        {
            await ExecuteAsync(dbName, operation.Sql);
        }
    }

    private static DataBaseContext NewContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(DbConnectionString(dbName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task<object?> DescriptionAsync(string dbName, string id, string language)
    {
        await using var connection = new NpgsqlConnection(DbConnectionString(dbName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT description ->> @language FROM calendar_rule WHERE id = @id::uuid", connection);
        command.Parameters.AddWithValue("language", language);
        command.Parameters.AddWithValue("id", id);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task<object?> ScalarAsync(string dbName, string sql)
    {
        await using var connection = new NpgsqlConnection(DbConnectionString(dbName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task ExecuteAsync(string dbName, string sql)
    {
        await using var connection = new NpgsqlConnection(DbConnectionString(dbName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RecreateDatabaseAsync(string dbName)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string dbName)
    {
        dbName.ShouldStartWith(ThrowawayPrefix);
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}
