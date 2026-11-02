// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Cancels the given token as soon as a transaction started, so a cancellation lands inside the commit of an accept and not
/// before it.
/// </summary>
/// <param name="cancellation">Source of the token the accept runs with</param>

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Klacks.IntegrationTest.AnalyseScenarios;

internal sealed class CancelOnTransactionStartInterceptor : DbTransactionInterceptor
{
    private readonly CancellationTokenSource _cancellation;

    public CancelOnTransactionStartInterceptor(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await _cancellation.CancelAsync();
        return result;
    }
}
