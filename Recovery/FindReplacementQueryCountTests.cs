// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard on the per-candidate query growth of the list of alternatives against PostgreSQL: one FindReplacementQuery is
/// counted for a group of SmallGroupCandidates people and again for LargeGroupCandidates people (no sister groups,
/// same works per person), each after one uncounted warm-up search so caches filled on first use do not count. The
/// difference of the two counts is what the additional candidates cost. The seeded plan carries a rule of every family
/// the rule check reads together for all people of a search (counter rule, month cap, rolling average, rest-day
/// rotation, restricted time window), bound to the seeded contract and group only, and every search must report a
/// finding of each of them: otherwise the batched rule paths did not run and a fall-back to reading per person would go
/// unnoticed. On 2026-11-12 the growth measured 37 commands per candidate before the rule check read the people of a
/// search together. Measured on the runner on 2026-11-12 after that change: 10 candidates = 77 commands, 30 candidates
/// = 77 commands, a difference of 0 for the 20 additional candidates (no foreign overtime rule, compensatory rest
/// switched off). Every read is one command per chunk of 2000 ids (clients or works) or per distinct day, window or
/// range, and the 20 additional candidates with about ten works each cross no chunk boundary, so the bound is
/// MaxAdditionalCommandsPerCandidate = 0: no growth is allowed. Any read that falls back to one command per person
/// costs at least one command per additional candidate and exceeds the bound. Known gaps of the measurement: the
/// compensatory-rest evaluator reads nothing while its setting is off, which it is in the test database, so its reads
/// are not exercised; the overtime cap of the period-cap evaluator reads per person by design, so the test is
/// inconclusive when the shared database holds an active overtime cap that applies to every client; the holiday-work
/// evaluator has no rule to seed and always runs batched. Both counts, their difference, the difference per table label
/// and the rule landscape of the shared database are written to the test output.
/// </summary>

using System.Globalization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class FindReplacementQueryCountTests : FindReplacementCostTestBase
{
    private const int SmallGroupCandidates = 10;
    private const int LargeGroupCandidates = 30;
    private const int MaxAdditionalCommandsPerCandidate = 0;

    protected override string Scope => "COUNT";

    [Test]
    public async Task SqlCommandsOfOneSearch_GrowPerAdditionalCandidate_ByNoMoreThanTheCurrentBound()
    {
        // Arrange
        var landscape = await ReadRuleLandscapeAsync();

        // Act
        var small = await CountCommandsOfOneSearchAsync(SmallGroupCandidates);
        var large = await CountCommandsOfOneSearchAsync(LargeGroupCandidates);

        // Assert
        var additionalCandidates = LargeGroupCandidates - SmallGroupCandidates;
        var additionalCommands = large.Total - small.Total;
        TestContext.Out.WriteLine(FormattableString.Invariant(
            $"find_replacement sql commands: {SmallGroupCandidates} candidates={small.Total}, {LargeGroupCandidates} candidates={large.Total}, difference={additionalCommands} for {additionalCandidates} additional candidates, bound={additionalCandidates * MaxAdditionalCommandsPerCandidate}"));
        TestContext.Out.WriteLine("difference per table label (non-zero): " + DescribeDifference(large.PerTable, small.PerTable));
        TestContext.Out.WriteLine(landscape.Describe());

        if (landscape.ForeignOvertimeCaps > 0)
        {
            Assert.Inconclusive(
                "The shared database holds an active overtime cap without a scheduling rule; the period-cap evaluator reads it per person and the test cannot switch it off without touching rows it does not own. The counts above are for information only.");
        }

        additionalCommands.ShouldBeLessThanOrEqualTo(
            additionalCandidates * MaxAdditionalCommandsPerCandidate,
            "a read of the rule check falls back to one command per person; the table labels above show which table grows");
    }

    private static string DescribeDifference(IReadOnlyDictionary<string, int> larger, IReadOnlyDictionary<string, int> smaller)
    {
        var differences = larger.Keys
            .Union(smaller.Keys, StringComparer.Ordinal)
            .Select(label => (Label: label, Difference: larger.GetValueOrDefault(label) - smaller.GetValueOrDefault(label)))
            .Where(entry => entry.Difference != 0)
            .OrderByDescending(entry => Math.Abs(entry.Difference))
            .ThenBy(entry => entry.Label, StringComparer.Ordinal)
            .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Label}={entry.Difference:+0;-0}"))
            .ToList();
        return differences.Count == 0 ? "none" : string.Join(", ", differences);
    }

    private async Task<Measurement> CountCommandsOfOneSearchAsync(int candidates)
    {
        var seed = await SeedAsync(candidates, sisterGroupSizes: []);
        var search = SearchFor(seed, analyseToken: null);
        await SendAsync(search);

        Factory.SqlCommands.Reset();
        var result = await SendAsync(search);
        var measurement = new Measurement(Factory.SqlCommands.Total, Factory.SqlCommands.PerTable);

        (result.Eligible.Count + result.Excluded.Count).ShouldBe(candidates, "every person of the group must have been judged");
        measurement.Total.ShouldBeGreaterThan(0, "the interceptor saw no command, so it is not attached to the host's contexts");
        AssertSeededRulesReported(result);
        await PurgeSeedAsync();
        return measurement;
    }

    private sealed record Measurement(int Total, IReadOnlyDictionary<string, int> PerTable);
}
