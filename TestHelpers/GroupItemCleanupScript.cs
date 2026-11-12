// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Gives integration tests the startup cleanup of group_item (cleanup_group_item_duplicates.sql) as the Api assembly
/// embeds it, without running StoredProcedureInitializer: that would recreate the schedule functions and commit a
/// cleanup that works on the whole shared database. The script text is read from the embedded resource and its own
/// BEGIN and COMMIT are taken out, so a test can run it inside a transaction of its own and roll everything back. A
/// script that still controls a transaction after that is refused, because it would commit the test's transaction.
/// </summary>
/// <param name="context">Context whose open transaction the script runs in</param>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Persistence.StoredProcedures;
using Microsoft.EntityFrameworkCore;

namespace Klacks.IntegrationTest.TestHelpers;

internal static class GroupItemCleanupScript
{
    private const string ResourceSuffix = ".StoredProcedures.cleanup_group_item_duplicates.sql";
    private const string Begin = "BEGIN;";
    private const string Commit = "COMMIT;";
    private const string ClientStatementMarker = "gi.client_id IS NOT NULL";

    private static readonly Regex TransactionControl =
        new(@"(?im)^\s*(BEGIN|COMMIT|ROLLBACK|END|START\s+TRANSACTION)\b", RegexOptions.Compiled);

    private static readonly Regex LineComment = new(@"--[^\r\n]*", RegexOptions.Compiled);

    /// <returns>The script exactly as the Api assembly embeds it</returns>
    public static string ReadEmbedded()
    {
        var assembly = typeof(StoredProcedureInitializer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' cannot be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <returns>The statements of the script that deduplicate client memberships, without comments</returns>
    public static IReadOnlyList<string> ClientStatements()
        => LineComment.Replace(ReadEmbedded(), string.Empty)
            .Split(';')
            .Where(statement => statement.Contains(ClientStatementMarker, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Runs the statements of the script in the transaction the caller opened on the context. The caller rolls back.
    /// </summary>
    public static async Task ExecuteInOpenTransactionAsync(DataBaseContext context)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The cleanup script must run inside a transaction the test rolls back.");
        }

        await context.Database.ExecuteSqlRawAsync(WithoutTransactionControl(ReadEmbedded()));
    }

    private static string WithoutTransactionControl(string script)
    {
        var lines = script.Split('\n');
        var kept = lines.Where(line => line.Trim() is not (Begin or Commit)).ToList();
        var statements = string.Join('\n', kept);
        if (lines.Length - kept.Count != 2 || TransactionControl.IsMatch(LineComment.Replace(statements, string.Empty)))
        {
            throw new InvalidOperationException(
                "cleanup_group_item_duplicates.sql no longer has exactly one BEGIN; and one COMMIT; line of its own. " +
                "Running it would end the transaction of the test; adapt GroupItemCleanupScript first.");
        }

        return statements;
    }
}
