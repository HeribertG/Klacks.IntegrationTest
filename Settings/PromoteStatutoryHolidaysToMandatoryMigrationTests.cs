// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The PromoteStatutoryHolidaysToMandatory data migration against real Postgres, up and down, on a throwaway
/// database (never on the shared one: the migration addresses fixed seed ids that cannot carry the
/// INTEGRATION_TEST_ prefix). The calendar rule seeds are inserted and the promoted rows are put back to the
/// state an older build left them in (is_mandatory = false), then AddUnofficialHolidayDescriptions writes the
/// US federal "unofficial" text. One US row is given an administrator's own text. After Up all promoted rows
/// are mandatory, the generated US text is gone, the edited row is untouched, is_paid is unchanged and the
/// rows that stay unofficial keep their text. Down reverts the flag and refills the US text; rolling back
/// the previous migration too removes it again; a second Up is idempotent.
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
public class PromoteStatutoryHolidaysToMandatoryMigrationTests
{
    private const string BeforeDescriptionsMigration = "20260927094736_RouteEmptyContainerDispatchesToContainerTemplate";
    private const string DescriptionsMigration = "20260929120000_AddUnofficialHolidayDescriptions";
    private const string TheMigration = "20260930120000_PromoteStatutoryHolidaysToMandatory";
    private const string ThrowawayPrefix = "klacks_boot_";

    private const string UsLaborDay = "05a00001-0001-0001-0001-000000000006";
    private const string UsColumbusDay = "05a00001-0001-0001-0001-000000000007";
    private const string TicinoJosefstag = "00319001-0001-0001-0001-000000000006";
    private const string GraubuendenJosefstag = "00319001-0001-0001-0001-000000000001";
    private const string GraubuendenPeterAndPaul = "00629001-0001-0001-0001-000000000001";
    private const string OwnDescription = @"{""en"": ""INTEGRATION_TEST_ own description""}";
    private const string OwnDescriptionText = "INTEGRATION_TEST_ own description";
    private const string English = "en";

    private const string Host = "localhost";
    private const int Port = 5434;
    private const string User = "postgres";
    private const string Password = "admin";

    private static string MaintenanceConnectionString =>
        $"Host={Host};Port={Port};Database=postgres;Username={User};Password={Password};Pooling=false";

    private static string DbConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    [Test]
    public async Task TheMigration_PromotesTheStatutoryRows_AndClearsOnlyTheGeneratedUsText()
    {
        var dbName = ThrowawayPrefix + "statutory_holiday_" + Guid.NewGuid().ToString("N")[..8];
        dbName.ShouldStartWith(ThrowawayPrefix);
        await RecreateDatabaseAsync(dbName);
        try
        {
            await using var context = NewContext(dbName);
            var migrator = context.GetService<IMigrator>();
            var usText = UnofficialHolidayDescriptionTexts.UsFederalHoliday[English];

            await migrator.MigrateAsync(BeforeDescriptionsMigration);
            await SeedCalendarRulesAsync(dbName);
            await ExecuteAsync(dbName, $"UPDATE calendar_rule SET is_mandatory = false WHERE id IN ({PromotedIdList()})");
            await migrator.MigrateAsync(DescriptionsMigration);

            (await DescriptionAsync(dbName, UsLaborDay, English)).ShouldBe(usText);
            await ExecuteAsync(dbName, $"UPDATE calendar_rule SET description = '{OwnDescription}'::jsonb WHERE id = '{UsColumbusDay}'");

            await migrator.MigrateAsync(TheMigration);

            (await PromotedMandatoryCountAsync(dbName)).ShouldBe(StatutoryHolidayPromotionSql.PromotedIds.Count);
            (await DescriptionAsync(dbName, UsLaborDay, English)).ShouldBe(string.Empty);
            (await DescriptionAsync(dbName, UsColumbusDay, English)).ShouldBe(OwnDescriptionText);
            (await ScalarAsync(dbName, $"SELECT is_paid FROM calendar_rule WHERE id = '{UsLaborDay}'")).ShouldBe(true);
            (await ScalarAsync(dbName, $"SELECT is_mandatory FROM calendar_rule WHERE id = '{GraubuendenJosefstag}'")).ShouldBe(false);
            (await ScalarAsync(dbName, $"SELECT is_mandatory FROM calendar_rule WHERE id = '{GraubuendenPeterAndPaul}'")).ShouldBe(false);
            (await DescriptionAsync(dbName, GraubuendenJosefstag, English))
                .ShouldBe(UnofficialHolidayDescriptionTexts.SomeMunicipalitiesOnly[English]);
            (await ScalarAsync(dbName, NonMandatoryWithoutDescriptionCount())).ShouldBe(0L);
            (await ScalarAsync(dbName, MandatoryWithUsTextCount())).ShouldBe(0L);

            await migrator.MigrateAsync(DescriptionsMigration);

            (await PromotedMandatoryCountAsync(dbName)).ShouldBe(0);
            (await ScalarAsync(dbName, $"SELECT is_mandatory FROM calendar_rule WHERE id = '{TicinoJosefstag}'")).ShouldBe(false);
            (await DescriptionAsync(dbName, UsLaborDay, English)).ShouldBe(usText);
            (await DescriptionAsync(dbName, UsColumbusDay, English)).ShouldBe(OwnDescriptionText);

            await migrator.MigrateAsync(BeforeDescriptionsMigration);

            (await DescriptionAsync(dbName, UsLaborDay, English)).ShouldBe(string.Empty);
            (await DescriptionAsync(dbName, UsColumbusDay, English)).ShouldBe(OwnDescriptionText);

            await migrator.MigrateAsync(TheMigration);
            await ApplyAgainAsync(dbName);

            (await PromotedMandatoryCountAsync(dbName)).ShouldBe(StatutoryHolidayPromotionSql.PromotedIds.Count);
            (await DescriptionAsync(dbName, UsLaborDay, English)).ShouldBe(string.Empty);
            (await DescriptionAsync(dbName, UsColumbusDay, English)).ShouldBe(OwnDescriptionText);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static string PromotedIdList() =>
        string.Join(", ", StatutoryHolidayPromotionSql.PromotedIds.Select(id => $"'{id}'::uuid"));

    private static async Task<int> PromotedMandatoryCountAsync(string dbName) =>
        Convert.ToInt32(await ScalarAsync(
            dbName, $"SELECT count(*) FROM calendar_rule WHERE is_mandatory = true AND id IN ({PromotedIdList()})"));

    private static string NonMandatoryWithoutDescriptionCount() =>
        "SELECT count(*) FROM calendar_rule WHERE is_mandatory = false "
        + "AND NOT EXISTS (SELECT 1 FROM jsonb_each_text(description) AS d(key, value) WHERE d.value <> '')";

    private static string MandatoryWithUsTextCount() =>
        "SELECT count(*) FROM calendar_rule WHERE is_mandatory = true AND description = '"
        + CalendarRuleDescriptionSql.SqlLiteral(
            CalendarRuleDescriptionSql.DescriptionJson(UnofficialHolidayDescriptionTexts.UsFederalHoliday))
        + "'::jsonb";

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
        StatutoryHolidayPromotionSql.Apply(builder);
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
