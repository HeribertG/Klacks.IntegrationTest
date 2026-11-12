// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard on the per-candidate query growth of the list of alternatives against PostgreSQL: one FindReplacementQuery is
/// counted for a group of SmallGroupCandidates people and again for LargeGroupCandidates people (no sister groups,
/// same works per person), each after one uncounted warm-up search so caches filled on first use do not count. The
/// difference of the two counts is what the additional candidates cost. It may not exceed
/// MaxAdditionalCommandsPerCandidate per additional candidate; the bound is twice the growth measured on
/// 2026-11-12 (37 commands per candidate) and is meant to be tightened once the per-person rule check reads in batches. Both counts and their
/// difference are written to the test output.
/// </summary>

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
    private const int MaxAdditionalCommandsPerCandidate = 75;

    protected override string Scope => "COUNT";

    [Test]
    public async Task SqlCommandsOfOneSearch_GrowPerAdditionalCandidate_ByNoMoreThanTheCurrentBound()
    {
        // Act
        var commandsForTheSmallGroup = await CountCommandsOfOneSearchAsync(SmallGroupCandidates);
        var commandsForTheLargeGroup = await CountCommandsOfOneSearchAsync(LargeGroupCandidates);

        // Assert
        var additionalCandidates = LargeGroupCandidates - SmallGroupCandidates;
        var additionalCommands = commandsForTheLargeGroup - commandsForTheSmallGroup;
        TestContext.Out.WriteLine(FormattableString.Invariant(
            $"find_replacement sql commands: {SmallGroupCandidates} candidates={commandsForTheSmallGroup}, {LargeGroupCandidates} candidates={commandsForTheLargeGroup}, difference={additionalCommands} for {additionalCandidates} additional candidates"));
        additionalCommands.ShouldBeLessThanOrEqualTo(additionalCandidates * MaxAdditionalCommandsPerCandidate);
    }

    private async Task<int> CountCommandsOfOneSearchAsync(int candidates)
    {
        var seed = await SeedAsync(candidates, sisterGroupSizes: []);
        var search = SearchFor(seed, analyseToken: null);
        await SendAsync(search);

        Factory.SqlCommands.Reset();
        var result = await SendAsync(search);
        var commands = Factory.SqlCommands.Total;

        (result.Eligible.Count + result.Excluded.Count).ShouldBe(candidates, "every person of the group must have been judged");
        commands.ShouldBeGreaterThan(0, "the interceptor saw no command, so it is not attached to the host's contexts");
        await PurgeSeedAsync();
        return commands;
    }
}
