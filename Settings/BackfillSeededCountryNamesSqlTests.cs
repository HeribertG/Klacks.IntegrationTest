// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the SQL of the BackfillSeededCountryNames migration against real Postgres. The
/// migration addresses the fixed seed id of the USA, so its statement builder is exercised on throwaway rows
/// whose German name starts with INTEGRATION_TEST_ (created and deleted by this fixture; the abbreviation
/// column is too short to carry the prefix): a name equal to the faulty text is replaced, an administrator's
/// own text and the other languages of the same row stay untouched, a second run changes nothing and
/// apostrophes survive the round trip. One more test applies the real migration statements to the seeded USA
/// row inside a transaction that is always rolled back, so no seed row of the shared database is ever changed.
/// </summary>

using Klacks.Api.Data.Seed;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class BackfillSeededCountryNamesSqlTests
{
    private const string NamePrefix = "INTEGRATION_TEST_COUNTRY_NAMEFIX";
    private const string French = "fr";
    private const string Italian = "it";
    private const string FaultyFrench = NamePrefix + " Etats-Unis d''Amerique";
    private const string CorrectedFrench = NamePrefix + " Etats-Unis d'Amerique";
    private const string FaultyItalian = NamePrefix + " Stati Uniti d''America";
    private const string CorrectedItalian = NamePrefix + " Stati Uniti d'America";
    private const string AdministratorsOwnText = NamePrefix + " Eigener Name";
    private const string TestAbbreviation = "IT_NAMEFIX";
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
        await InsertAsync(_faultyId, FaultyFrench, FaultyItalian);
        await InsertAsync(_editedId, AdministratorsOwnText, AdministratorsOwnText);
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Statement_ReplacesAFaultyName_AndLeavesTheOtherLanguagesOfTheRowAlone()
    {
        await ApplyAsync(French, FaultyFrench, CorrectedFrench, _faultyId);

        (await NameAsync(_faultyId, French)).ShouldBe(CorrectedFrench);
        (await NameAsync(_faultyId, Italian)).ShouldBe(FaultyItalian, "a correction touches one language key only");
        (await NameAsync(_faultyId, "de")).ShouldBe(NamePrefix + " de");
    }

    [Test]
    public async Task Statement_TwoCorrectionsOnTheSameRow_BothApply()
    {
        await ApplyAsync(French, FaultyFrench, CorrectedFrench, _faultyId);
        await ApplyAsync(Italian, FaultyItalian, CorrectedItalian, _faultyId);

        (await NameAsync(_faultyId, French)).ShouldBe(CorrectedFrench);
        (await NameAsync(_faultyId, Italian)).ShouldBe(CorrectedItalian);
    }

    [Test]
    public async Task Statement_NeverOverwritesAnAdministratorsOwnText()
    {
        await ApplyAsync(French, FaultyFrench, CorrectedFrench, _editedId);
        await ApplyAsync(Italian, FaultyItalian, CorrectedItalian, _editedId);

        (await NameAsync(_editedId, French)).ShouldBe(AdministratorsOwnText);
        (await NameAsync(_editedId, Italian)).ShouldBe(AdministratorsOwnText);
    }

    [Test]
    public async Task Statement_IsIdempotent()
    {
        await ApplyAsync(French, FaultyFrench, CorrectedFrench, _faultyId);
        await ApplyAsync(French, FaultyFrench, CorrectedFrench, _faultyId);

        (await NameAsync(_faultyId, French)).ShouldBe(CorrectedFrench);
    }

    [Test]
    public async Task Statement_OnlyTouchesTheAddressedRow()
    {
        await InsertAsync(Guid.NewGuid(), FaultyFrench, FaultyItalian);

        await ApplyAsync(French, FaultyFrench, CorrectedFrench, _faultyId);

        (await ScalarAsync($"SELECT count(*) FROM countries WHERE name ->> 'fr' = '{CorrectedFrench.Replace("'", "''")}'"))
            .ShouldBe(1L);
    }

    [Test]
    public async Task TheRealStatements_RepairTheFaultyUsaRow_AndAreRolledBack()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var seedRows = (long)(await ScalarAsync(connection, transaction, SeedRowCountSql()))!;
        var expectedRows = CountryNameCorrectionSql.Corrections.Select(c => c.RowId).Distinct().Count();
        if (seedRows < expectedRows)
        {
            await transaction.RollbackAsync();
            Assert.Ignore("The seeded USA country row is not present in this database.");
        }

        foreach (var correction in CountryNameCorrectionSql.Corrections)
        {
            await ExecuteAsync(connection, transaction, ForceSql(correction, correction.FaultyName));
            var changed = await ExecuteAsync(connection, transaction, SeededNameCorrectionSql.BuildStatement(correction));
            changed.ShouldBe(1, $"{correction.RowId} / {correction.Language}");
            (await ScalarAsync(connection, transaction, ReadSql(correction))).ShouldBe(correction.CorrectedName);
        }

        var again = 0;
        foreach (var correction in CountryNameCorrectionSql.Corrections)
        {
            again += await ExecuteAsync(connection, transaction, SeededNameCorrectionSql.BuildStatement(correction));
        }

        again.ShouldBe(0, "a second run finds nothing");
        await transaction.RollbackAsync();
    }

    private static string SeedRowCountSql() =>
        $"SELECT count(*) FROM {CountryNameCorrectionSql.CountryTable} WHERE id IN ("
        + string.Join(", ", CountryNameCorrectionSql.Corrections.Select(c => $"'{c.RowId}'").Distinct()) + ")";

    private static string ReadSql(SeededNameCorrection correction) =>
        $"SELECT name ->> '{correction.Language}' FROM {correction.Table} WHERE id = '{correction.RowId}'";

    private static string ForceSql(SeededNameCorrection correction, string text) =>
        $"UPDATE {correction.Table} SET name = jsonb_set(name, '{{{correction.Language}}}', to_jsonb('{text.Replace("'", "''")}'::text)) "
        + $"WHERE id = '{correction.RowId}'";

    private async Task ApplyAsync(string language, string faulty, string corrected, Guid id)
    {
        var correction = new SeededNameCorrection(CountryNameCorrectionSql.CountryTable, id.ToString(), language, faulty, corrected);
        await ExecuteAsync(SeededNameCorrectionSql.BuildStatement(correction));
    }

    private async Task InsertAsync(Guid id, string french, string italian)
    {
        var name = $"{{\"de\":\"{NamePrefix} de\",\"en\":\"{NamePrefix} en\",\"fr\":\"{french}\",\"it\":\"{italian}\"}}";
        await ExecuteAsync(
            "INSERT INTO countries (id, abbreviation, name, prefix, is_deleted, create_time, current_user_created, "
            + "current_user_updated, current_user_deleted) "
            + $"VALUES ('{id}', '{TestAbbreviation}', '{name.Replace("'", "''")}'::jsonb, '+0', false, NOW(), '{NamePrefix}', '', '')");
    }

    private async Task<string?> NameAsync(Guid id, string language) =>
        (string?)await ScalarAsync($"SELECT name ->> '{language}' FROM countries WHERE id = '{id}'");

    private async Task CleanupAsync() =>
        await ExecuteAsync($"DELETE FROM countries WHERE name ->> 'de' LIKE '{NamePrefix}%'");

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
