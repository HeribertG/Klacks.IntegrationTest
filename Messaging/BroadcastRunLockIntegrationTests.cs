// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The broadcast run lock on real PostgreSQL (IT database klacks; it writes no rows): a second scope is refused without waiting
/// while the first holds the lock, a second acquire in the same scope is granted and only the outermost release frees it, and the
/// lock is gone from pg_locks afterwards, so the release ran on the connection that holds it.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Messaging;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class BroadcastRunLockIntegrationTests
{
    private const string HeldLocksSql =
        "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database()) " +
        "AND ((classid::bigint << 32) | objid::bigint) = 7203041955100002";

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static BroadcastRunLock NewLock(DbContext context) =>
        new(context, Substitute.For<ILogger<BroadcastRunLock>>());

    private static async Task<long> HeldLocksAsync()
    {
        await using var connection = new NpgsqlConnection(TestHostDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(HeldLocksSql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Test]
    public async Task ASecondScope_IsRefusedWhileTheFirstHoldsTheLock_AndGetsItAfterwards()
    {
        await using var firstContext = NewContext();
        await using var secondContext = NewContext();
        var first = NewLock(firstContext);
        var second = NewLock(secondContext);

        var hold = await first.TryAcquireAsync(CancellationToken.None);

        hold.ShouldNotBeNull();
        (await HeldLocksAsync()).ShouldBe(1);
        (await second.TryAcquireAsync(CancellationToken.None)).ShouldBeNull();

        await hold.DisposeAsync();

        (await HeldLocksAsync()).ShouldBe(0);
        var afterwards = await second.TryAcquireAsync(CancellationToken.None);
        afterwards.ShouldNotBeNull();
        await afterwards.DisposeAsync();
        (await HeldLocksAsync()).ShouldBe(0);
    }

    [Test]
    public async Task WithinOneScope_TheLockIsReentrant_AndOnlyTheOutermostReleaseFreesIt()
    {
        await using var context = NewContext();
        await using var otherContext = NewContext();
        var runLock = NewLock(context);
        var other = NewLock(otherContext);

        var outer = await runLock.TryAcquireAsync(CancellationToken.None);
        var inner = await runLock.TryAcquireAsync(CancellationToken.None);

        outer.ShouldNotBeNull();
        inner.ShouldNotBeNull();
        await inner.DisposeAsync();
        (await other.TryAcquireAsync(CancellationToken.None)).ShouldBeNull();

        await outer.DisposeAsync();

        (await HeldLocksAsync()).ShouldBe(0);
    }

    [Test]
    public async Task WhileTheLockIsHeld_TheContextKeepsWorkingOnItsConnection()
    {
        await using var context = NewContext();
        var runLock = NewLock(context);

        var hold = await runLock.TryAcquireAsync(CancellationToken.None);
        hold.ShouldNotBeNull();

        await context.Database.ExecuteSqlRawAsync("SELECT 1");
        (await HeldLocksAsync()).ShouldBe(1);

        await hold.DisposeAsync();
        (await HeldLocksAsync()).ShouldBe(0);
    }
}
