// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The AddGroupItemIsTemporary migration against real Postgres, on a throwaway database (never on the shared one: a
/// Down there would drop the column under every other test), like AddUserIntroTourStatesMigrationTests. The database is
/// created empty, migrated to the migration before this one, seeded with group items through plain SQL (foreign keys
/// are switched off for the seeding session, so no groups, clients or shifts are needed) and dropped at the end. Up
/// marks the client memberships of scenarios as temporary, live and soft-deleted ones, and leaves regular memberships
/// and shift links alone; the three unique indexes exist and refuse what they are meant to refuse; Down stops with
/// its error while a person holds two day rows and restores the previous indexes once they are gone.
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

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
public class AddGroupItemIsTemporaryMigrationTests
{
    private const string TheMigration = "20261112090000_AddGroupItemIsTemporary";
    private const string ThrowawayPrefix = "klacks_boot_";
    private const string RegularIndex = "ix_group_item_client_id_group_id";
    private const string TemporaryIndex = "ix_group_item_client_id_group_id_valid_from";
    private const string ScenarioIndex = "ix_group_item_client_id_group_id_analyse_token_valid_from";
    private const string PreviousScenarioIndex = "ix_group_item_client_id_group_id_analyse_token";
    private const string Monday = "2092-06-02 00:00:00+00";
    private const string Friday = "2092-06-06 00:00:00+00";
    private const string DownRefusal = "Cannot restore the unique indexes of group_item";

    private const string Host = "localhost";
    private const int Port = 5434;
    private const string User = "postgres";
    private const string Password = "admin";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid Regular = Guid.NewGuid();
    private static readonly Guid ShiftLink = Guid.NewGuid();
    private static readonly Guid ScenarioMembership = Guid.NewGuid();
    private static readonly Guid DeletedScenarioMembership = Guid.NewGuid();
    private static readonly Guid ScenarioShiftLink = Guid.NewGuid();
    private static readonly Guid BorrowedClient = Guid.NewGuid();
    private static readonly Guid ScenarioToken = Guid.NewGuid();

    private static string MaintenanceConnectionString =>
        $"Host={Host};Port={Port};Database=postgres;Username={User};Password={Password};Pooling=false";

    private static string DbConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    [Test]
    public async Task Up_MarksScenarioMemberships_AndBuildsTheIndexes_DownRefusesDayRows_AndRestoresWithoutThem()
    {
        var dbName = ThrowawayPrefix + "group_item_temporary_" + Guid.NewGuid().ToString("N")[..8];
        dbName.ShouldStartWith(ThrowawayPrefix);
        await RecreateDatabaseAsync(dbName);
        try
        {
            var previousMigration = PreviousMigration(dbName);
            await MigrateAsync(dbName, previousMigration);
            (await ColumnExistsAsync(dbName)).ShouldBeFalse();
            await SeedBeforeTheMigrationAsync(dbName);

            await MigrateAsync(dbName, TheMigration);
            await AssertBackfillAsync(dbName);
            await AssertIndexesAsync(dbName);
            await AssertUniquenessAsync(dbName);

            var refused = await Should.ThrowAsync<Exception>(() => MigrateAsync(dbName, previousMigration));
            refused.ToString().ShouldContain(DownRefusal);
            (await ColumnExistsAsync(dbName)).ShouldBeTrue("a refused Down leaves the database unchanged");
            (await IndexDefinitionAsync(dbName, ScenarioIndex)).ShouldNotBeNull();

            await ExecuteAsync(dbName, $"DELETE FROM group_item WHERE client_id = '{BorrowedClient}'");
            await MigrateAsync(dbName, previousMigration);
            (await ColumnExistsAsync(dbName)).ShouldBeFalse();
            (await IndexDefinitionAsync(dbName, PreviousScenarioIndex)).ShouldNotBeNull();
            (await IndexDefinitionAsync(dbName, ScenarioIndex)).ShouldBeNull();
            (await IndexDefinitionAsync(dbName, TemporaryIndex)).ShouldBeNull();
            (await IndexDefinitionAsync(dbName, RegularIndex)).ShouldNotBeNull().ShouldNotContain("is_temporary");

            await MigrateAsync(dbName, TheMigration);
            await AssertBackfillAsync(dbName);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static async Task SeedBeforeTheMigrationAsync(string dbName)
    {
        var sql = $@"
            SET session_replication_role = replica;
            INSERT INTO group_item (id, client_id, shift_id, group_id, valid_from, valid_until, analyse_token, is_deleted) VALUES
                ('{Regular}', '{Guid.NewGuid()}', NULL, '{GroupId}', NULL, NULL, NULL, false),
                ('{ShiftLink}', NULL, '{Guid.NewGuid()}', '{GroupId}', NULL, NULL, NULL, false),
                ('{ScenarioMembership}', '{Guid.NewGuid()}', NULL, '{GroupId}', '{Monday}', '{Friday}', '{ScenarioToken}', false),
                ('{DeletedScenarioMembership}', '{Guid.NewGuid()}', NULL, '{GroupId}', '{Monday}', '{Monday}', '{Guid.NewGuid()}', true),
                ('{ScenarioShiftLink}', NULL, '{Guid.NewGuid()}', '{GroupId}', NULL, NULL, '{ScenarioToken}', false);
            SET session_replication_role = DEFAULT;";
        await ExecuteAsync(dbName, sql);
    }

    private static async Task AssertBackfillAsync(string dbName)
    {
        (await IsTemporaryAsync(dbName, ScenarioMembership)).ShouldBeTrue();
        (await IsTemporaryAsync(dbName, DeletedScenarioMembership)).ShouldBeTrue();
        (await IsTemporaryAsync(dbName, Regular)).ShouldBeFalse();
        (await IsTemporaryAsync(dbName, ShiftLink)).ShouldBeFalse();
        (await IsTemporaryAsync(dbName, ScenarioShiftLink)).ShouldBeFalse();
    }

    private static async Task AssertIndexesAsync(string dbName)
    {
        var regular = (await IndexDefinitionAsync(dbName, RegularIndex)).ShouldNotBeNull();
        regular.ShouldContain("UNIQUE");
        regular.ShouldContain("(client_id, group_id)");
        regular.ShouldContain("analyse_token IS NULL");
        regular.ShouldContain("is_temporary = false");

        var temporary = (await IndexDefinitionAsync(dbName, TemporaryIndex)).ShouldNotBeNull();
        temporary.ShouldContain("UNIQUE");
        temporary.ShouldContain("(client_id, group_id, valid_from)");
        temporary.ShouldContain("analyse_token IS NULL");
        temporary.ShouldContain("is_temporary = true");

        var scenario = (await IndexDefinitionAsync(dbName, ScenarioIndex)).ShouldNotBeNull();
        scenario.ShouldContain("UNIQUE");
        scenario.ShouldContain("(client_id, group_id, analyse_token, valid_from)");
        scenario.ShouldContain("analyse_token IS NOT NULL");

        (await IndexDefinitionAsync(dbName, PreviousScenarioIndex)).ShouldBeNull();
    }

    /// <summary>
    /// One person borrowed on Monday and Friday, in a scenario and in the real plan: each day is allowed once, next to a
    /// regular membership of the same person and group, and a second regular membership is still refused.
    /// </summary>
    private static async Task AssertUniquenessAsync(string dbName)
    {
        await ExecuteAsync(dbName, DayRow(Monday, ScenarioToken));
        await ExecuteAsync(dbName, DayRow(Friday, ScenarioToken));
        await ShouldViolateUniquenessAsync(dbName, DayRow(Monday, ScenarioToken));

        await ExecuteAsync(dbName, DayRow(Monday, token: null));
        await ExecuteAsync(dbName, DayRow(Friday, token: null));
        await ShouldViolateUniquenessAsync(dbName, DayRow(Monday, token: null));

        await ExecuteAsync(dbName, RegularRow());
        await ShouldViolateUniquenessAsync(dbName, RegularRow());
    }

    private static string DayRow(string day, Guid? token)
    {
        var tokenSql = token is { } value ? $"'{value}'" : "NULL";
        return $@"
            SET session_replication_role = replica;
            INSERT INTO group_item (id, client_id, group_id, valid_from, valid_until, analyse_token, is_deleted, is_temporary)
            VALUES ('{Guid.NewGuid()}', '{BorrowedClient}', '{GroupId}', '{day}', '{day}', {tokenSql}, false, true);";
    }

    private static string RegularRow() => $@"
            SET session_replication_role = replica;
            INSERT INTO group_item (id, client_id, group_id, is_deleted, is_temporary)
            VALUES ('{Guid.NewGuid()}', '{BorrowedClient}', '{GroupId}', false, false);";

    private static async Task ShouldViolateUniquenessAsync(string dbName, string sql)
    {
        var violation = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(dbName, sql));
        violation.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    private static string PreviousMigration(string dbName)
    {
        using var context = NewContext(dbName);
        var migrations = context.Database.GetMigrations().ToList();
        var index = migrations.IndexOf(TheMigration);
        index.ShouldBeGreaterThan(0, "the migration under test must be known and must not be the first one");
        return migrations[index - 1];
    }

    private static async Task MigrateAsync(string dbName, string targetMigration)
    {
        await using var context = NewContext(dbName);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static DataBaseContext NewContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(DbConnectionString(dbName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task<bool> IsTemporaryAsync(string dbName, Guid id)
        => (bool)(await ScalarAsync(dbName, $"SELECT is_temporary FROM group_item WHERE id = '{id}'"))!;

    private static async Task<bool> ColumnExistsAsync(string dbName)
    {
        var count = await ScalarAsync(
            dbName,
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'group_item' AND column_name = 'is_temporary'");
        return (long)count! > 0;
    }

    private static async Task<string?> IndexDefinitionAsync(string dbName, string indexName)
        => (string?)await ScalarAsync(
            dbName, $"SELECT indexdef FROM pg_indexes WHERE tablename = 'group_item' AND indexname = '{indexName}'");

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
