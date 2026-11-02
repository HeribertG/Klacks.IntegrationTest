// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Makes one armed commit look lost: the database commits, then the caller gets the transient error a broken connection
/// gives, which is what EnableRetryOnFailure replays on.
/// </summary>

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Klacks.IntegrationTest.AnalyseScenarios;

internal sealed class LostCommitAcknowledgementInterceptor : DbTransactionInterceptor
{
    private const string LostMessage = "The acknowledgement of the commit was lost";

    private bool _armed;

    public int LostAcknowledgements { get; private set; }

    public void Arm() => _armed = true;

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (!_armed)
        {
            return Task.CompletedTask;
        }

        _armed = false;
        LostAcknowledgements++;
        return Task.FromException(new NpgsqlException(LostMessage, new TimeoutException()));
    }
}
