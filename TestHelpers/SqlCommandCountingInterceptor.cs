// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Counts the SQL commands EF Core sends through the contexts it is attached to: every reader, scalar and non-query
/// execution, synchronous or asynchronous, is one command (one round trip; a batched SaveChanges counts once). Next to
/// the total it keeps a count per table label, the first name after FROM, UPDATE or INSERT INTO in the command text.
/// The label is a reading aid for measurement output, not an exact attribution: a command that joins several tables
/// is booked on the first one, a function call such as get_schedule_entries is booked under the function name, and a
/// command without such a name is booked under NoTableLabel. Counting is thread-safe; Reset is meant to be called
/// while no command runs.
/// </summary>

using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Klacks.IntegrationTest.TestHelpers;

public sealed class SqlCommandCountingInterceptor : DbCommandInterceptor
{
    public const string NoTableLabel = "(no table)";

    private const string TableGroup = "table";

    private static readonly Regex FirstTable = new(
        @"\b(?:FROM|UPDATE|INSERT\s+INTO)\s+(?:ONLY\s+)?(?:""?[A-Za-z_][A-Za-z0-9_]*""?\.)?""?(?<" + TableGroup + @">[A-Za-z_][A-Za-z0-9_]*)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<string, int> _perTable = new(StringComparer.Ordinal);
    private int _total;

    public int Total => Volatile.Read(ref _total);

    public IReadOnlyDictionary<string, int> PerTable => new Dictionary<string, int>(_perTable, StringComparer.Ordinal);

    public void Reset()
    {
        Interlocked.Exchange(ref _total, 0);
        _perTable.Clear();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Count(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }

    private void Count(DbCommand command)
    {
        Interlocked.Increment(ref _total);
        _perTable.AddOrUpdate(TableLabelOf(command.CommandText), 1, (_, count) => count + 1);
    }

    private static string TableLabelOf(string commandText)
    {
        var match = FirstTable.Match(commandText);
        return match.Success ? match.Groups[TableGroup].Value.ToLowerInvariant() : NoTableLabel;
    }
}
