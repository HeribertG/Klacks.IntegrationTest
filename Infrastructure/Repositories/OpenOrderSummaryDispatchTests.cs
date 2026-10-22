// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests of the two database pieces of the daily open-order summary against real PostgreSQL:
/// (1) ProactiveTriggerDispatchRepository.ListDedupKeysAsync, the read behind the rising-count dedup, whose
/// StartsWith prefix match must translate to SQL and must keep to one user, one kind and live rows; (2) the
/// MarkPerOrderOpenOrderMessagesRead migration SQL, which must stamp only unread rows of the former per-order shape
/// (kind open_order, content key i18n:assistant.proactive.openOrder), leave the summary and every other row alone,
/// and change nothing when it runs a second time.
/// SHARED-DATABASE SAFETY: assertions look only at rows of the synthetic fixture user, and cleanup deletes only rows
/// whose user_id carries the prefix INTEGRATION_TEST_. The migration SQL is executed as written, so on the shared test
/// database it also marks other per-order open-order rows read - which is exactly what it does on deploy.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class OpenOrderSummaryDispatchTests
{
    private const string TestPrefix = "INTEGRATION_TEST_";
    private const string UserId = TestPrefix + "OPEN_ORDER_SUMMARY_USER";
    private const string OtherUserId = TestPrefix + "OPEN_ORDER_SUMMARY_OTHER";
    private const string PerOrderContentKey = "i18n:assistant.proactive.openOrder";
    private const string SummaryContentKey = "i18n:assistant.proactive.openOrdersSummary";
    private const string OtherKind = TestPrefix + "other_kind";

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task CleanupAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_trigger_dispatches WHERE user_id LIKE {0}", TestPrefix + "%");
    }

    [Test]
    public async Task ListDedupKeys_ReturnsTheUsersLiveKeysOfTheKindWithThePrefix_Only()
    {
        await using (var context = NewContext())
        {
            context.AgentTriggerDispatches.AddRange(
                Row(UserId, AgentTriggerKinds.OpenOrder, "2026-10-22#5", SummaryContentKey),
                Row(UserId, AgentTriggerKinds.OpenOrder, "2026-10-22#7", SummaryContentKey),
                Row(UserId, AgentTriggerKinds.OpenOrder, "2026-10-21#9", SummaryContentKey),
                Row(UserId, AgentTriggerKinds.OpenOrder, "2026-10-22#30", SummaryContentKey, isDeleted: true),
                Row(UserId, OtherKind, "2026-10-22#99", SummaryContentKey),
                Row(OtherUserId, AgentTriggerKinds.OpenOrder, "2026-10-22#50", SummaryContentKey));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var keys = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
                .ListDedupKeysAsync(UserId, AgentTriggerKinds.OpenOrder, "2026-10-22#");

            keys.ShouldBe(["2026-10-22#5", "2026-10-22#7"], ignoreOrder: true);
        }
    }

    [Test]
    public async Task MarkReadMigration_StampsOnlyUnreadPerOrderRows_AndIsIdempotent()
    {
        var earlierRead = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var perOrderUnread = Row(UserId, AgentTriggerKinds.OpenOrder, $"{Guid.NewGuid()}:2026-10-25", PerOrderContentKey);
        var perOrderRead = Row(UserId, AgentTriggerKinds.OpenOrder, $"{Guid.NewGuid()}:2026-10-26", PerOrderContentKey);
        perOrderRead.ReadAtUtc = earlierRead;
        var summaryUnread = Row(UserId, AgentTriggerKinds.OpenOrder, "2026-10-22#5", SummaryContentKey);
        var otherKindUnread = Row(UserId, OtherKind, $"{Guid.NewGuid()}:2026-10-25", PerOrderContentKey);

        await using (var context = NewContext())
        {
            context.AgentTriggerDispatches.AddRange(perOrderUnread, perOrderRead, summaryUnread, otherKindUnread);
            await context.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();
        var afterFirstRun = await ReadStampsAsync();
        await RunMigrationSqlAsync();
        var afterSecondRun = await ReadStampsAsync();

        afterFirstRun[perOrderUnread.Id].ShouldNotBeNull();
        afterFirstRun[perOrderRead.Id]!.Value.ToUniversalTime().ShouldBe(earlierRead);
        afterFirstRun[summaryUnread.Id].ShouldBeNull();
        afterFirstRun[otherKindUnread.Id].ShouldBeNull();
        afterSecondRun.ShouldBe(afterFirstRun);
    }

    private static async Task RunMigrationSqlAsync()
    {
        var sql = new MarkPerOrderOpenOrderMessagesRead().UpOperations.OfType<SqlOperation>().Single().Sql;
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<Dictionary<Guid, DateTime?>> ReadStampsAsync()
    {
        await using var context = NewContext();
        return await context.AgentTriggerDispatches.AsNoTracking()
            .Where(row => row.UserId == UserId)
            .ToDictionaryAsync(row => row.Id, row => row.ReadAtUtc);
    }

    private static ProactiveTriggerDispatchRow Row(
        string userId, string kind, string dedupKey, string contentKey, bool isDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TriggerKind = kind,
        DedupKey = dedupKey,
        ContentKey = contentKey,
        Severity = AgentTriggerSeverity.Medium,
        IsDeleted = isDeleted
    };

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }
}
