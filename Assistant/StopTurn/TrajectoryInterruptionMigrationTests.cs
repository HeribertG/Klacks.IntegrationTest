// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The AddTrajectoryInterruption migration against real Postgres, up and down, on a throwaway database
/// (never on the shared one: a Down there would drop the columns under every other test). The database is
/// migrated to the migration before it, a trajectory row is written the way an older build wrote it, then the
/// migration runs: the two columns exist with the shape the model declares (was_interrupted boolean NOT NULL
/// default false, interrupted_phase varchar(16) nullable), the existing row reads false and null, a new row
/// without the columns reads the default, and a phase longer than the column is refused. Then the migration is
/// rolled back (both columns gone, the row survives) and applied a second time.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[TestFixture]
[Category("RealDatabase")]
public class TrajectoryInterruptionMigrationTests
{
    private const string PreviousMigration = "20260925084556_AddMacroOrigin";
    private const string TheMigration = "20260925130249_AddTrajectoryInterruption";
    private const string Table = "skill_selection_trajectories";
    private const string ThrowawayPrefix = "klacks_boot_";
    private const int PhaseColumnLength = 16;

    private const string Host = "localhost";
    private const int Port = 5434;
    private const string User = "postgres";
    private const string Password = "admin";

    private static string MaintenanceConnectionString =>
        $"Host={Host};Port={Port};Database=postgres;Username={User};Password={Password};Pooling=false";

    private static string DbConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    [Test]
    public async Task TheMigration_AddsBothColumnsWithTheirDefaults_AndRollsBackCleanly()
    {
        var dbName = ThrowawayPrefix + "trajectory_interruption_" + Guid.NewGuid().ToString("N")[..8];
        dbName.ShouldStartWith(ThrowawayPrefix);
        await RecreateDatabaseAsync(dbName);
        try
        {
            await using var context = NewContext(dbName);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync(PreviousMigration);
            (await ColumnAsync(dbName, "was_interrupted")).ShouldBeNull();
            (await ColumnAsync(dbName, "interrupted_phase")).ShouldBeNull();
            var legacyRow = Guid.NewGuid();
            await ExecuteAsync(dbName, LegacyInsert(legacyRow));

            await migrator.MigrateAsync(TheMigration);
            await AssertColumnsAsync(dbName);
            (await ScalarAsync(dbName, $"SELECT was_interrupted FROM {Table} WHERE id = '{legacyRow}'")).ShouldBe(false);
            (await ScalarAsync(dbName, $"SELECT interrupted_phase FROM {Table} WHERE id = '{legacyRow}'")).ShouldBeNull();

            var newRow = Guid.NewGuid();
            await ExecuteAsync(dbName, LegacyInsert(newRow));
            (await ScalarAsync(dbName, $"SELECT was_interrupted FROM {Table} WHERE id = '{newRow}'")).ShouldBe(false);
            await ExecuteAsync(dbName, $"UPDATE {Table} SET was_interrupted = true, interrupted_phase = 'during_tools' WHERE id = '{newRow}'");
            (await ScalarAsync(dbName, $"SELECT interrupted_phase FROM {Table} WHERE id = '{newRow}'")).ShouldBe("during_tools");
            var tooLong = new string('x', PhaseColumnLength + 1);
            await Should.ThrowAsync<PostgresException>(
                () => ExecuteAsync(dbName, $"UPDATE {Table} SET interrupted_phase = '{tooLong}' WHERE id = '{newRow}'"));

            await migrator.MigrateAsync(PreviousMigration);
            (await ColumnAsync(dbName, "was_interrupted")).ShouldBeNull();
            (await ColumnAsync(dbName, "interrupted_phase")).ShouldBeNull();
            (await ScalarAsync(dbName, $"SELECT count(*) FROM {Table} WHERE id IN ('{legacyRow}', '{newRow}')")).ShouldBe(2L);
            (await ScalarAsync(
                dbName, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id = '{TheMigration}'")).ShouldBe(0L);

            await migrator.MigrateAsync(TheMigration);
            await AssertColumnsAsync(dbName);
            (await ScalarAsync(dbName, $"SELECT was_interrupted FROM {Table} WHERE id = '{legacyRow}'")).ShouldBe(false);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static async Task AssertColumnsAsync(string dbName)
    {
        var flag = await ColumnAsync(dbName, "was_interrupted");
        flag.ShouldNotBeNull();
        flag[0].ShouldBe("boolean");
        flag[1].ShouldBe("NO");
        flag[2]!.ToString()!.ShouldStartWith("false");
        flag[3].ShouldBeNull();

        var phase = await ColumnAsync(dbName, "interrupted_phase");
        phase.ShouldNotBeNull();
        phase[0].ShouldBe("character varying");
        phase[1].ShouldBe("YES");
        phase[2].ShouldBeNull();
        phase[3].ShouldBe(PhaseColumnLength);
    }

    private static DataBaseContext NewContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(DbConnectionString(dbName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static string LegacyInsert(Guid id) =>
        $"INSERT INTO {Table} (id, agent_id, locale, user_message_hash, intent_excerpt, knowledge_index_candidates_json, "
        + "was_executed, had_mutation_intent, was_corrected, correction_type, latency_ms_total, latency_ms_knowledge, "
        + $"latency_ms_llm, is_deleted) VALUES ('{id}', '{Guid.NewGuid()}', 'en', 'hash', 'excerpt', '[]', false, false, "
        + "false, 'none', 0, 0, 0, false)";

    private static async Task<object?[]?> ColumnAsync(string dbName, string column)
    {
        await using var connection = new NpgsqlConnection(DbConnectionString(dbName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT data_type, is_nullable, column_default, character_maximum_length FROM information_schema.columns "
            + "WHERE table_name = @table AND column_name = @column", connection);
        command.Parameters.AddWithValue("table", Table);
        command.Parameters.AddWithValue("column", column);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var row = new object?[reader.FieldCount];
        for (var index = 0; index < row.Length; index++)
        {
            row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        }

        return row;
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
