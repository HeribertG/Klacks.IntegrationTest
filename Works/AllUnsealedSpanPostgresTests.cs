// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The span of the recalculation of all unsealed entries (AllUnsealedSpanQuery) includes the cached real-plan
/// period-hours rows and translates on PostgreSQL (IT database klacks). Read only: the all-unsealed run itself would write
/// rows for every client of the shared database, so only the span lookup runs here. A prefixed row in 2099 must be inside
/// the span; a scenario row further out must not widen it.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class AllUnsealedSpanPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "ALLUNSEALEDSPAN_";
    private const string ClientSuffix = "CLIENT";

    private static readonly (DateOnly Start, DateOnly End) RealPlanRow = (new(2099, 12, 1), new(2099, 12, 31));
    private static readonly DateOnly ScenarioRowEnd = new(2199, 12, 31);

    private Guid _clientId;

    [SetUp]
    public async Task SetUp()
    {
        await WorkChainTestData.CleanupAsync(Prefix);
        _clientId = await WorkChainTestData.SeedClientAsync(Prefix, ClientSuffix);
    }

    [TearDown]
    public async Task TearDown() => await WorkChainTestData.CleanupAsync(Prefix);

    [Test]
    public async Task ACachedRealPlanRow_LiesInsideTheSpan_AScenarioRowDoesNotWidenIt()
    {
        await WorkChainTestData.SeedPeriodHoursAsync(_clientId, RealPlanRow, 8m, 0m);
        await using (var seed = WorkChainTestData.NewRetryingContext())
        {
            seed.ClientPeriodHours.Add(new ClientPeriodHours
            {
                Id = Guid.NewGuid(),
                ClientId = _clientId,
                StartDate = new DateOnly(2199, 12, 1),
                EndDate = ScenarioRowEnd,
                AnalyseToken = Guid.NewGuid(),
                CalculatedAt = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = WorkChainTestData.NewRetryingContext();
        var (first, last) = await AllUnsealedSpanQuery.LoadAsync(context, CancellationToken.None);

        first.ShouldNotBeNull();
        first.Value.ShouldBeLessThanOrEqualTo(RealPlanRow.Start);
        last.ShouldNotBeNull();
        last.Value.ShouldBeGreaterThanOrEqualTo(RealPlanRow.End);
        last.Value.ShouldBeLessThan(ScenarioRowEnd);
    }
}
