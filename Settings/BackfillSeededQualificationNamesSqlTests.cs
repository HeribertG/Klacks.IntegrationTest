// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the SQL of the BackfillSeededQualificationNames migration against real Postgres. The
/// migration addresses fixed seed ids, so its statement builder is exercised on throwaway rows whose name
/// starts with INTEGRATION_TEST_ (created and deleted by this fixture): a name equal to the faulty text is
/// replaced, an administrator's own text and the other languages of the same row stay untouched, a second run
/// changes nothing and apostrophes survive the round trip. One more test applies the real migration
/// statements to the seeded rows inside a transaction that is always rolled back, so no seed row of the
/// shared database is ever changed.
/// </summary>

using Klacks.Api.Data.Seed;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class BackfillSeededQualificationNamesSqlTests
{
    private const string NamePrefix = "INTEGRATION_TEST_QUAL_NAMEFIX";
    private const string French = "fr";
    private const string Italian = "it";
    private const string FaultyFrench = NamePrefix + " Administration d''injections";
    private const string CorrectedFrench = NamePrefix + " Administration d'injections";
    private const string FaultyItalian = NamePrefix + " Gestion de la chaîne du froid";
    private const string CorrectedItalian = NamePrefix + " Gestione della catena del freddo";
    private const string AdministratorsOwnText = NamePrefix + " Eigener Name";
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

        (await ScalarAsync($"SELECT count(*) FROM qualification WHERE name ->> 'fr' = '{CorrectedFrench.Replace("'", "''")}'"))
            .ShouldBe(1L);
    }

    [Test]
    public async Task TheRealStatements_RepairTheFaultySeedRows_AndAreRolledBack()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var seedRows = (long)(await ScalarAsync(connection, transaction, SeedRowCountSql()))!;
        var expectedRows = QualificationNameCorrectionSql.Corrections.Select(c => c.QualificationId).Distinct().Count();
        if (seedRows < expectedRows)
        {
            await transaction.RollbackAsync();
            Assert.Ignore("The seeded qualification rows are not present in this database.");
        }

        foreach (var correction in QualificationNameCorrectionSql.Corrections)
        {
            await ExecuteAsync(connection, transaction, ForceSql(correction, correction.FaultyName));
            var changed = await ExecuteAsync(connection, transaction, QualificationNameCorrectionSql.BuildStatement(correction));
            changed.ShouldBe(1, $"{correction.QualificationId} / {correction.Language}");
            (await ScalarAsync(connection, transaction, ReadSql(correction))).ShouldBe(correction.CorrectedName);
        }

        var again = 0;
        foreach (var correction in QualificationNameCorrectionSql.Corrections)
        {
            again += await ExecuteAsync(connection, transaction, QualificationNameCorrectionSql.BuildStatement(correction));
        }

        again.ShouldBe(0, "a second run finds nothing");
        await transaction.RollbackAsync();
    }

    private static string SeedRowCountSql() =>
        "SELECT count(*) FROM qualification WHERE id IN ("
        + string.Join(", ", QualificationNameCorrectionSql.Corrections.Select(c => $"'{c.QualificationId}'").Distinct()) + ")";

    private static string ReadSql(QualificationNameCorrection correction) =>
        $"SELECT name ->> '{correction.Language}' FROM qualification WHERE id = '{correction.QualificationId}'";

    private static string ForceSql(QualificationNameCorrection correction, string text) =>
        $"UPDATE qualification SET name = jsonb_set(name, '{{{correction.Language}}}', to_jsonb('{text.Replace("'", "''")}'::text)) "
        + $"WHERE id = '{correction.QualificationId}'";

    private async Task ApplyAsync(string language, string faulty, string corrected, Guid id)
    {
        var correction = new QualificationNameCorrection(id.ToString(), language, faulty, corrected);
        await ExecuteAsync(QualificationNameCorrectionSql.BuildStatement(correction));
    }

    private async Task InsertAsync(Guid id, string french, string italian)
    {
        var name = $"{{\"de\":\"{NamePrefix} de\",\"en\":\"{NamePrefix} en\",\"fr\":\"{french}\",\"it\":\"{italian}\"}}";
        await ExecuteAsync(
            "INSERT INTO qualification (id, name, emoji, is_time_limited, type, category, is_deleted, create_time, "
            + "current_user_created, current_user_updated, current_user_deleted) "
            + $"VALUES ('{id}', '{name.Replace("'", "''")}'::jsonb, '', false, 2, 0, false, NOW(), '{NamePrefix}', '', '')");
    }

    private async Task<string?> NameAsync(Guid id, string language) =>
        (string?)await ScalarAsync($"SELECT name ->> '{language}' FROM qualification WHERE id = '{id}'");

    private async Task CleanupAsync() =>
        await ExecuteAsync($"DELETE FROM qualification WHERE name ->> 'de' LIKE '{NamePrefix}%'");

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
