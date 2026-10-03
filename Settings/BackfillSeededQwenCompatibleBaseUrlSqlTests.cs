// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the SQL of the BackfillSeededQwenCompatibleBaseUrl migration against real Postgres. The
/// statement addresses the fixed provider id 'qwen', so every test works on that row inside a transaction that is
/// always rolled back - no row of the shared database is ever changed. Verified: the exact old seeded URL is
/// replaced, an administrator's own URL (including a near-miss of the old value) stays untouched, and a second run
/// changes nothing.
/// </summary>

using Klacks.Api.Data.Seed;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class BackfillSeededQwenCompatibleBaseUrlSqlTests
{
    private const string AdministratorsOwnUrl = "https://INTEGRATION_TEST_.example.test/compatible-mode/v1/";
    private const string OldUrlWithoutTrailingSlash = "https://dashscope.aliyuncs.com/api/v1";
    private const string DefaultConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? DefaultConnectionString;
    }

    [Test]
    public async Task Statement_ReplacesTheExactOldSeededUrl_AndASecondRunChangesNothing()
    {
        await InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            await ForceUrlAsync(connection, transaction, LLMProviderBaseUrlCorrectionSql.FaultyQwenBaseUrl);

            var changed = await ExecuteAsync(connection, transaction, LLMProviderBaseUrlCorrectionSql.BuildQwenStatement());
            var again = await ExecuteAsync(connection, transaction, LLMProviderBaseUrlCorrectionSql.BuildQwenStatement());

            changed.ShouldBe(1);
            again.ShouldBe(0, "a second run finds nothing");
            (await ReadUrlAsync(connection, transaction)).ShouldBe(LLMProviderBaseUrlCorrectionSql.CorrectedQwenBaseUrl);
        });
    }

    [TestCase(AdministratorsOwnUrl)]
    [TestCase(OldUrlWithoutTrailingSlash)]
    public async Task Statement_NeverOverwritesAnAdministratorsOwnUrl(string ownUrl)
    {
        await InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            await ForceUrlAsync(connection, transaction, ownUrl);

            var changed = await ExecuteAsync(connection, transaction, LLMProviderBaseUrlCorrectionSql.BuildQwenStatement());

            changed.ShouldBe(0);
            (await ReadUrlAsync(connection, transaction)).ShouldBe(ownUrl);
        });
    }

    [Test]
    public async Task Statement_LeavesOtherProvidersWithTheSameUrlAlone()
    {
        await InRolledBackTransactionAsync(async (connection, transaction) =>
        {
            await ForceUrlAsync(connection, transaction, LLMProviderBaseUrlCorrectionSql.FaultyQwenBaseUrl);
            var otherProviders = await ExecuteAsync(connection, transaction,
                $"UPDATE {LLMProviderBaseUrlCorrectionSql.ProviderTable} SET base_url = '{LLMProviderBaseUrlCorrectionSql.FaultyQwenBaseUrl}' "
                + $"WHERE provider_id <> '{LLMProviderBaseUrlCorrectionSql.QwenProviderId}'");

            await ExecuteAsync(connection, transaction, LLMProviderBaseUrlCorrectionSql.BuildQwenStatement());

            var stillOld = (long)(await ScalarAsync(connection, transaction,
                $"SELECT count(*) FROM {LLMProviderBaseUrlCorrectionSql.ProviderTable} "
                + $"WHERE base_url = '{LLMProviderBaseUrlCorrectionSql.FaultyQwenBaseUrl}'"))!;
            stillOld.ShouldBe(otherProviders);
        });
    }

    private async Task InRolledBackTransactionAsync(Func<NpgsqlConnection, NpgsqlTransaction, Task> body)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var qwenRows = (long)(await ScalarAsync(connection, transaction,
                $"SELECT count(*) FROM {LLMProviderBaseUrlCorrectionSql.ProviderTable} "
                + $"WHERE provider_id = '{LLMProviderBaseUrlCorrectionSql.QwenProviderId}'"))!;
            if (qwenRows != 1)
            {
                Assert.Ignore("The seeded Qwen provider row is not present exactly once in this database.");
            }

            await body(connection, transaction);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static Task<int> ForceUrlAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string url) =>
        ExecuteAsync(connection, transaction,
            $"UPDATE {LLMProviderBaseUrlCorrectionSql.ProviderTable} SET base_url = '{url}' "
            + $"WHERE provider_id = '{LLMProviderBaseUrlCorrectionSql.QwenProviderId}'");

    private static async Task<string?> ReadUrlAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        (string?)await ScalarAsync(connection, transaction,
            $"SELECT base_url FROM {LLMProviderBaseUrlCorrectionSql.ProviderTable} "
            + $"WHERE provider_id = '{LLMProviderBaseUrlCorrectionSql.QwenProviderId}'");

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync();
    }
}
