// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration test of ProactiveTriggerDispatchRepository.GetSinceAsync against the real PostgreSQL database: the
/// explain-offer counter rows (kinds below "explain.topic." and "explain.ui.") are excluded by the query itself, so they
/// never use up the row cap of the goal reflection, while every other kind - ledger-only rows included - is returned.
/// The prefix match must translate to SQL, which only a real database proves.
/// SHARED-DATABASE SAFETY: GetSinceAsync reads all users, so the assertions look only at rows of the synthetic fixture
/// user, and cleanup deletes only rows whose user_id carries the prefix INTEGRATION_TEST_.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class ProactiveTriggerDispatchRepositoryGetSinceTests
{
    private const string TestPrefix = "INTEGRATION_TEST_";
    private const string UserId = TestPrefix + "GETSINCE_USER";
    private const string BusinessKind = TestPrefix + "getsince_business";
    private const int GenerousMaxRows = 100000;

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
    public async Task GetSince_ExcludesExplainOfferRows_AndKeepsEveryOtherKind()
    {
        var gateKind = ExplainOfferTopics.TriggerKindOf(ExplainOfferTopics.All[0]);
        var uiKind = UiExplainOfferTopics.TriggerKindOf(UiExplainOfferTopics.All[0]);
        var since = DateTime.UtcNow.AddMinutes(-5);

        await using (var context = NewContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);
            await repository.RecordAsync(Row(BusinessKind));
            await repository.RecordAsync(Row(gateKind));
            await repository.RecordAsync(Row(uiKind));
        }

        await using (var context = NewContext())
        {
            var rows = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
                .GetSinceAsync(since, GenerousMaxRows);

            rows.Where(row => row.UserId == UserId).Select(row => row.TriggerKind).ShouldBe([BusinessKind]);
        }
    }

    private static ProactiveTriggerDispatchRow Row(string kind) => new()
    {
        UserId = UserId,
        TriggerKind = kind,
        DedupKey = TestPrefix + Guid.NewGuid().ToString("N"),
        Severity = AgentTriggerSeverity.Low
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
