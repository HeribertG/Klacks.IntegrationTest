// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The AddUserIntroTourStates migration against real Postgres, on a throwaway database (never on the shared one: a
/// Down there would drop the table under every other test). Accounts that exist before the migration each get one
/// legacy_offer row with an empty station list; an account created afterwards gets none (it is a new account and is
/// offered the intro automatically); Down drops the table and a second Up backfills again without duplicates.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.IntroTour;

[TestFixture]
[Category("RealDatabase")]
public class AddUserIntroTourStatesMigrationTests
{
    private const string PreviousMigration = "20261017061726_SoftDeleteOrphanedGroupItems";
    private const string TheMigration = "20261017083821_AddUserIntroTourStates";
    private const string Table = "user_intro_tour_states";
    private const string ThrowawayPrefix = "klacks_boot_";

    private const string Host = "localhost";
    private const int Port = 5434;
    private const string User = "postgres";
    private const string Password = "admin";

    private static string MaintenanceConnectionString =>
        $"Host={Host};Port={Port};Database=postgres;Username={User};Password={Password};Pooling=false";

    private static string DbConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    [Test]
    public async Task ExistingAccounts_GetOneLegacyOfferEach_NewAccountsNone_AndItRollsBackCleanly()
    {
        var dbName = ThrowawayPrefix + "intro_tour_" + Guid.NewGuid().ToString("N")[..8];
        dbName.ShouldStartWith(ThrowawayPrefix);
        await RecreateDatabaseAsync(dbName);
        try
        {
            await using var context = NewContext(dbName);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync(PreviousMigration);
            (await TableExistsAsync(dbName)).ShouldBeFalse();
            var existingA = await AddUserAsync(context, "a");
            var existingB = await AddUserAsync(context, "b");

            await migrator.MigrateAsync(TheMigration);
            await AssertLegacyOfferAsync(dbName, existingA);
            await AssertLegacyOfferAsync(dbName, existingB);

            var newcomer = await AddUserAsync(context, "c");
            (await ScalarAsync(dbName, $"SELECT count(*) FROM {Table} WHERE user_id = '{newcomer}'")).ShouldBe(0L);

            await migrator.MigrateAsync(PreviousMigration);
            (await TableExistsAsync(dbName)).ShouldBeFalse();

            await migrator.MigrateAsync(TheMigration);
            (await ScalarAsync(dbName, $"SELECT count(*) FROM {Table}")).ShouldBe(3L);
            await AssertLegacyOfferAsync(dbName, existingA);
            await AssertLegacyOfferAsync(dbName, newcomer);
        }
        finally
        {
            await DropDatabaseAsync(dbName);
        }
    }

    private static async Task AssertLegacyOfferAsync(string dbName, string userId)
    {
        (await ScalarAsync(dbName, $"SELECT count(*) FROM {Table} WHERE user_id = '{userId}'")).ShouldBe(1L);
        (await ScalarAsync(dbName, $"SELECT status FROM {Table} WHERE user_id = '{userId}'"))
            .ShouldBe(IntroTourStatus.LegacyOffer);
        (await ScalarAsync(dbName, $"SELECT jsonb_array_length(completed_stations) FROM {Table} WHERE user_id = '{userId}'"))
            .ShouldBe(0);
        (await ScalarAsync(dbName, $"SELECT snooze_count FROM {Table} WHERE user_id = '{userId}'")).ShouldBe(0);
        (await ScalarAsync(dbName, $"SELECT last_offered_at FROM {Table} WHERE user_id = '{userId}'")).ShouldBeNull();
    }

    private static async Task<string> AddUserAsync(DataBaseContext context, string suffix)
    {
        var id = Guid.NewGuid().ToString();
        var email = $"intro-{suffix}-{id}@example.invalid";
        context.AppUser.Add(new AppUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = "Intro",
            LastName = suffix,
            SecurityStamp = Guid.NewGuid().ToString()
        });
        await context.SaveChangesAsync();
        return id;
    }

    private static DataBaseContext NewContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(DbConnectionString(dbName))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task<bool> TableExistsAsync(string dbName)
    {
        var count = await ScalarAsync(
            dbName, $"SELECT count(*) FROM information_schema.tables WHERE table_name = '{Table}'");
        return (long)count! > 0;
    }

    private static async Task<object?> ScalarAsync(string dbName, string sql)
    {
        await using var connection = new NpgsqlConnection(DbConnectionString(dbName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
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
