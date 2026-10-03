// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the SQL of the BackfillSeededBerchtoldstagNames migration against real Postgres. The
/// migration addresses the fixed seed ids of the 13 Berchtoldstag rules, so its statement builder is exercised on
/// throwaway calendar_rule rows whose German name starts with INTEGRATION_TEST_ (country ZZ, created and deleted by
/// this fixture): a name equal to the faulty text is replaced, an administrator's own text and the other languages
/// of the same row stay untouched, a second run changes nothing, and apostrophes and non-Latin scripts survive the
/// round trip. One more test applies the real migration statements to the seeded rows inside a transaction that is
/// always rolled back, so no seed row of the shared database is ever changed.
/// </summary>

using Klacks.Api.Data.Seed;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class BackfillSeededBerchtoldstagNamesSqlTests
{
    private const string NamePrefix = "INTEGRATION_TEST_BERCHTOLD_NAMEFIX";
    private const string TestCountry = "ZZ";
    private const string English = "en";
    private const string Japanese = "ja";
    private const string French = "fr";
    private const string FaultyEnglish = "St. Berchtold's Day";
    private const string CorrectedEnglish = "Berchtold's Day";
    private const string FaultyJapanese = "聖ベルヒトルトの日";
    private const string CorrectedJapanese = "ベルヒトルトの日";
    private const string KeptFrench = "Saint-Berchtold";
    private const string AdministratorsOwnText = "Eigener Name";
    private const string DefaultConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

    private readonly Guid _faultyId = Guid.NewGuid();
    private readonly Guid _editedId = Guid.NewGuid();
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? DefaultConnectionString;
    }

    [SetUp]
    public async Task SetUp()
    {
        await CleanupAsync();
        await InsertAsync(_faultyId, FaultyEnglish, FaultyJapanese);
        await InsertAsync(_editedId, AdministratorsOwnText, AdministratorsOwnText);
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Statement_ReplacesAFaultyName_AndLeavesTheOtherLanguagesOfTheRowAlone()
    {
        await ApplyAsync(English, FaultyEnglish, CorrectedEnglish, _faultyId);

        (await NameAsync(_faultyId, English)).ShouldBe(CorrectedEnglish);
        (await NameAsync(_faultyId, Japanese)).ShouldBe(FaultyJapanese, "a correction touches one language key only");
        (await NameAsync(_faultyId, French)).ShouldBe(KeptFrench);
        (await NameAsync(_faultyId, "de")).ShouldBe(NamePrefix);
    }

    [Test]
    public async Task Statement_TwoCorrectionsOnTheSameRow_BothApply()
    {
        await ApplyAsync(English, FaultyEnglish, CorrectedEnglish, _faultyId);
        await ApplyAsync(Japanese, FaultyJapanese, CorrectedJapanese, _faultyId);

        (await NameAsync(_faultyId, English)).ShouldBe(CorrectedEnglish);
        (await NameAsync(_faultyId, Japanese)).ShouldBe(CorrectedJapanese);
    }

    [Test]
    public async Task Statement_NeverOverwritesAnAdministratorsOwnText()
    {
        await ApplyAsync(English, FaultyEnglish, CorrectedEnglish, _editedId);
        await ApplyAsync(Japanese, FaultyJapanese, CorrectedJapanese, _editedId);

        (await NameAsync(_editedId, English)).ShouldBe(AdministratorsOwnText);
        (await NameAsync(_editedId, Japanese)).ShouldBe(AdministratorsOwnText);
    }

    [Test]
    public async Task Statement_IsIdempotent()
    {
        await ApplyAsync(English, FaultyEnglish, CorrectedEnglish, _faultyId);
        var again = await ApplyAsync(English, FaultyEnglish, CorrectedEnglish, _faultyId);

        again.ShouldBe(0);
        (await NameAsync(_faultyId, English)).ShouldBe(CorrectedEnglish);
    }

    [Test]
    public async Task Statement_OnlyTouchesTheAddressedRow()
    {
        var otherId = Guid.NewGuid();
        await InsertAsync(otherId, FaultyEnglish, FaultyJapanese);

        await ApplyAsync(English, FaultyEnglish, CorrectedEnglish, _faultyId);

        (await NameAsync(otherId, English)).ShouldBe(FaultyEnglish);
    }

    [Test]
    public async Task TheRealStatements_RepairTheFaultySeedRows_AndAreRolledBack()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var seedRows = (long)(await ScalarAsync(connection, transaction, SeedRowCountSql()))!;
        if (seedRows < CalendarRuleNameCorrectionSql.BerchtoldstagRuleIds.Count)
        {
            await transaction.RollbackAsync();
            Assert.Ignore("The seeded Berchtoldstag rows are not present in this database.");
        }

        foreach (var correction in CalendarRuleNameCorrectionSql.Corrections)
        {
            await ExecuteAsync(connection, transaction, ForceSql(correction, correction.FaultyName));
            var changed = await ExecuteAsync(connection, transaction, SeededNameCorrectionSql.BuildStatement(correction));
            changed.ShouldBe(1, $"{correction.RowId} / {correction.Language}");
            (await ScalarAsync(connection, transaction, ReadSql(correction.RowId, correction.Language))).ShouldBe(correction.CorrectedName);
        }

        foreach (var id in CalendarRuleNameCorrectionSql.BerchtoldstagRuleIds)
        {
            (await ScalarAsync(connection, transaction, ReadSql(id, "de"))).ShouldBe("Berchtoldstag");
        }

        var again = 0;
        foreach (var correction in CalendarRuleNameCorrectionSql.Corrections)
        {
            again += await ExecuteAsync(connection, transaction, SeededNameCorrectionSql.BuildStatement(correction));
        }

        again.ShouldBe(0, "a second run finds nothing");
        await transaction.RollbackAsync();
    }

    private static string SeedRowCountSql() =>
        $"SELECT count(*) FROM {CalendarRuleNameCorrectionSql.CalendarRuleTable} WHERE id IN ("
        + string.Join(", ", CalendarRuleNameCorrectionSql.BerchtoldstagRuleIds.Select(id => $"'{id}'")) + ")";

    private static string ReadSql(string id, string language) =>
        $"SELECT name ->> '{language}' FROM {CalendarRuleNameCorrectionSql.CalendarRuleTable} WHERE id = '{id}'";

    private static string ForceSql(SeededNameCorrection correction, string text) =>
        $"UPDATE {correction.Table} SET name = jsonb_set(name, '{{{correction.Language}}}', to_jsonb('{text.Replace("'", "''")}'::text)) "
        + $"WHERE id = '{correction.RowId}'";

    private async Task<int> ApplyAsync(string language, string faulty, string corrected, Guid id)
    {
        var correction = new SeededNameCorrection(CalendarRuleNameCorrectionSql.CalendarRuleTable, id.ToString(), language, faulty, corrected);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return await ExecuteAsync(connection, null, SeededNameCorrectionSql.BuildStatement(correction));
    }

    private async Task InsertAsync(Guid id, string english, string japanese)
    {
        var name = $"{{\"de\":\"{NamePrefix}\",\"en\":\"{english}\",\"fr\":\"{KeptFrench}\",\"it\":\"San Bertoldo\",\"ja\":\"{japanese}\"}}";
        await ExecuteAsync(
            "INSERT INTO calendar_rule (id, rule, sub_rule, is_mandatory, is_paid, state, country, description, name) "
            + $"VALUES ('{id}', '01/02', '', true, true, '{TestCountry}', '{TestCountry}', '{{}}'::jsonb, '{name.Replace("'", "''")}'::jsonb)");
    }

    private async Task<string?> NameAsync(Guid id, string language) =>
        (string?)await ScalarAsync(ReadSql(id.ToString(), language));

    private async Task CleanupAsync() =>
        await ExecuteAsync($"DELETE FROM calendar_rule WHERE name ->> 'de' LIKE '{NamePrefix}%'");

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return await ScalarAsync(connection, null, sql);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, null, sql);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync();
    }
}
