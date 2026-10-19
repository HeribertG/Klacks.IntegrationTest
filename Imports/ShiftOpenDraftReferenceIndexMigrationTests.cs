// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The narrow unique index on shift (source_system_id, external_order_reference) WHERE reference IS NOT NULL AND
/// is_deleted = false AND status = 0 (one open draft per ERP order reference), against real Postgres.
/// Two fixtures: the shared integration database proves the index's behaviour (a second open draft with the same
/// reference is refused; a sealed order, a deleted draft, another source system, a null source system and a
/// different reference are all allowed -- supersession depends on the sealed/draft pair coexisting), and a
/// throwaway database proves the migration itself: with duplicate open drafts present it fails with a message
/// naming them and deletes nothing, once they are resolved it creates the index, and Down removes it again.
/// Shared-database cleanup deletes ONLY shifts whose source system carries the INTEGRATION_TEST_SHIFTREF_ prefix.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Imports;

[TestFixture]
[Category("RealDatabase")]
public class ShiftOpenDraftReferenceIndexMigrationTests
{
    private const string PreviousMigration = "20261019134108_AddErpImportFile";
    private const string TheMigration = "20261019163414_AddShiftOpenDraftReferenceUniqueIndex";
    private const string ThrowawayPrefix = "klacks_boot_";
    private const string SourceSystem = ShiftReferenceIndexSql.Prefix + "MIG";

    private const string Host = "localhost";
    private const int Port = 5434;
    private const string User = "postgres";
    private const string Password = "admin";

    private static string MaintenanceConnectionString =>
        $"Host={Host};Port={Port};Database=postgres;Username={User};Password={Password};Pooling=false";

    private static string DbConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    [Test]
    public async Task TheMigration_FailsNamingTheDuplicateOpenDrafts_DeletesNothing_ThenCreatesTheIndexOnceResolved_AndRollsBack()
    {
        var dbName = ThrowawayPrefix + "open_draft_index_" + Guid.NewGuid().ToString("N")[..8];
        dbName.ShouldStartWith(ThrowawayPrefix);
        await RecreateDatabaseAsync(dbName);
        try
        {
            await using var context = NewContext(dbName);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            await using var connection = new NpgsqlConnection(DbConnectionString(dbName));
            await connection.OpenAsync();
            (await IndexDefinitionAsync(connection)).ShouldBeNull();

            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, SourceSystem, "DUP-1");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, SourceSystem, "DUP-1");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, SourceSystem, "DUP-2");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, SourceSystem, "DUP-2");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, SourceSystem, "DUP-2");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, SourceSystem, "ONLY-ONE");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.SealedOrder, SourceSystem, "CLOSED-REF");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.SealedOrder, SourceSystem, "CLOSED-REF");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, SourceSystem, "GONE-REF", isDeleted: true);
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, SourceSystem, "GONE-REF", isDeleted: true);
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, null, "NO-SYSTEM");
            await ShiftReferenceIndexSql.InsertShiftAsync(connection, ShiftStatus.OriginalOrder, null, "NO-SYSTEM");
            var shiftCountBefore = (long)(await ShiftReferenceIndexSql.ScalarAsync(connection, "SELECT count(*) FROM shift"))!;

            var failure = await Should.ThrowAsync<PostgresException>(() => migrator.MigrateAsync(TheMigration));

            failure.MessageText.ShouldContain(ShiftIndexNames.OpenDraftReferenceUnique);
            failure.MessageText.ShouldContain("2 order reference(s)");
            failure.MessageText.ShouldContain($"source_system_id='{SourceSystem}', external_order_reference='DUP-1', open_drafts=2");
            failure.MessageText.ShouldContain($"source_system_id='{SourceSystem}', external_order_reference='DUP-2', open_drafts=3");
            failure.MessageText.ShouldNotContain("ONLY-ONE");
            failure.MessageText.ShouldNotContain("CLOSED-REF");
            failure.MessageText.ShouldNotContain("GONE-REF");
            failure.MessageText.ShouldNotContain("NO-SYSTEM");
            (await IndexDefinitionAsync(connection)).ShouldBeNull();
            (await ScalarAsync(connection, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id = '{TheMigration}'")).ShouldBe(0L);
            (await ScalarAsync(connection, "SELECT count(*) FROM shift")).ShouldBe(shiftCountBefore);

            await ExecuteAsync(
                connection,
                "UPDATE shift SET is_deleted = true WHERE id IN (SELECT id FROM (SELECT id, row_number() OVER "
                + "(PARTITION BY external_order_reference ORDER BY id::text) AS rn FROM shift "
                + $"WHERE source_system_id = '{SourceSystem}' AND is_deleted = false AND status = 0) ranked WHERE rn > 1)");

            await migrator.MigrateAsync(TheMigration);

            var definition = await IndexDefinitionAsync(connection);
            definition.ShouldNotBeNull();
            definition.ShouldContain("UNIQUE");
            definition.ShouldContain("status = 0");
            (await ScalarAsync(connection, $"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE migration_id = '{TheMigration}'")).ShouldBe(1L);

            await migrator.MigrateAsync(PreviousMigration);
            (await IndexDefinitionAsync(connection)).ShouldBeNull();
            (await ScalarAsync(connection, "SELECT count(*) FROM pg_indexes WHERE indexname = '"
                + ShiftIndexNames.ExternalOrderReferenceLookup + "'")).ShouldBe(1L);
            (await ScalarAsync(connection, "SELECT count(*) FROM shift")).ShouldBe(shiftCountBefore);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static async Task<string?> IndexDefinitionAsync(NpgsqlConnection connection)
    {
        return (string?)await ShiftReferenceIndexSql.ScalarAsync(
            connection, $"SELECT indexdef FROM pg_indexes WHERE indexname = '{ShiftIndexNames.OpenDraftReferenceUnique}'");
    }

    private static DataBaseContext NewContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(DbConnectionString(dbName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        return await ShiftReferenceIndexSql.ScalarAsync(connection, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RecreateDatabaseAsync(string dbName)
    {
        await DropDatabaseAsync(dbName);
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"CREATE DATABASE \"{dbName}\"");
    }

    private static async Task DropDatabaseAsync(string dbName)
    {
        if (!dbName.StartsWith(ThrowawayPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop a database that is not a throwaway one.");
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(MaintenanceConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
    }
}
