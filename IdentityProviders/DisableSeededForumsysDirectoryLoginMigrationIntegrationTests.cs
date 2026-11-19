// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Postgres proof of the DisableSeededForumsysDirectoryLogin data migration: its statement, run against the seeded
/// public test provider row (the row is first put into the old seeded state - enabled and used for authentication),
/// switches off authentication and the enabled flag; against the same row pointed at another host it changes nothing,
/// because the statement also matches the seeded host. Each case runs in a transaction that is rolled back, so the shared
/// integration database keeps the row exactly as it was. The case reports inconclusive when the seeded row is not there.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.IdentityProviders;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class DisableSeededForumsysDirectoryLoginMigrationIntegrationTests
{
    private const string SeededProviderId = "fc0b8e9c-4694-40c1-a10e-fdb66a10b26e";
    private const string SeededHost = "ldap.forumsys.com";
    private const string ChangedHost = "ldap.company.example";
    private const string DefaultConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("DATABASE_URL") ?? DefaultConnectionString;

    [Test]
    public async Task UpSql_SwitchesOffTheSeededProviderThatStillHasTheSeededHost()
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PutIntoOldSeededStateAsync(connection, transaction, SeededHost);

        await ExecuteAsync(connection, transaction, DisableSeededForumsysDirectoryLogin.UpSql);

        var (enabled, forAuthentication) = await ReadFlagsAsync(connection, transaction);
        enabled.ShouldBeFalse();
        forAuthentication.ShouldBeFalse();
        await transaction.RollbackAsync();
    }

    [Test]
    public async Task UpSql_LeavesTheRowAloneWhenItWasPointedAtAnotherHost()
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PutIntoOldSeededStateAsync(connection, transaction, ChangedHost);

        await ExecuteAsync(connection, transaction, DisableSeededForumsysDirectoryLogin.UpSql);

        var (enabled, forAuthentication) = await ReadFlagsAsync(connection, transaction);
        enabled.ShouldBeTrue();
        forAuthentication.ShouldBeTrue();
        await transaction.RollbackAsync();
    }

    [Test]
    public async Task UpSql_RunTwice_ChangesNothingTheSecondTime()
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await PutIntoOldSeededStateAsync(connection, transaction, SeededHost);
        await ExecuteAsync(connection, transaction, DisableSeededForumsysDirectoryLogin.UpSql);

        await ExecuteAsync(connection, transaction, DisableSeededForumsysDirectoryLogin.UpSql);

        var (enabled, forAuthentication) = await ReadFlagsAsync(connection, transaction);
        enabled.ShouldBeFalse();
        forAuthentication.ShouldBeFalse();
        await transaction.RollbackAsync();
    }

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task PutIntoOldSeededStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string host)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE identity_providers SET is_enabled = true, use_for_authentication = true, host = @host WHERE id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("host", host);
        command.Parameters.AddWithValue("id", Guid.Parse(SeededProviderId));

        var affected = await command.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            Assert.Inconclusive("The seeded Forumsys provider row is not in this database.");
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(bool Enabled, bool ForAuthentication)> ReadFlagsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT is_enabled, use_for_authentication FROM identity_providers WHERE id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", Guid.Parse(SeededProviderId));

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }
}
