// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Measurement, not a regression test (Explicit): what the list of alternatives (FindReplacementQuery) costs with a
/// large root, against real Postgres. The requested group has GroupMembers people plus the one who falls out; four
/// sister groups under the same root hold more people than CrossGroupPoolCap, so the cap is exercised and the search
/// judges the group and the capped number of borrowed people. Everybody has ten works around the day. Measured twice:
/// on the real plan, and inside the scenario a cover of the absent person created (there the borrowed people are
/// checked against the real plan as well). Per case WarmUpRuns runs are discarded and MeasuredRuns are reported to
/// the test output: wall-clock time, SQL commands per search (total and per table label of the last run) and how many
/// candidates came out eligible and excluded. What is real: the mediator pipeline, the handler, the conflict checker,
/// the repositories and Postgres (localhost, warm connection pool). What is not: the caller. Group and scenario
/// visibility are the unrestricted stand-ins of the test host, so the visibility lookups of a restricted planner, the
/// HTTP layer and the phone lookup of the planner endpoint are not in the numbers. The only assertions are structural:
/// the interceptor saw commands and every run judged the expected number of people.
/// </summary>

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Explicit("Measurement, not a regression test")]
[Category("Measurement")]
[NonParallelizable]
public sealed class FindReplacementLargeRootMeasurementTests : FindReplacementCostTestBase
{
    private const int WarmUpRuns = 1;
    private const int MeasuredRuns = 5;
    private const int GroupMembers = 20;
    private const int AbsentGroupMembers = 1;
    private const int JudgedCandidatesIncludingTheAbsentMember =
        GroupMembers + AbsentGroupMembers + RecoveryBuildConstants.CrossGroupPoolCap;

    private static readonly int[] SisterGroupSizes = [53, 53, 52, 52];

    protected override string Scope => "MEASURE";

    [Test]
    public async Task AlternativesOnTheRealPlan_WithALargeRoot_TimeAndSqlCommands()
    {
        // Arrange
        var seed = await SeedAsync(GroupMembers + AbsentGroupMembers, SisterGroupSizes);

        // Act
        var samples = await MeasureAsync(SearchFor(seed, analyseToken: null));

        // Assert
        Report("real_plan", samples);
        AssertStructure(samples);
    }

    [Test]
    public async Task AlternativesInsideTheCoverScenario_WithALargeRoot_TimeAndSqlCommands()
    {
        // Arrange
        var seed = await SeedAsync(GroupMembers + AbsentGroupMembers, SisterGroupSizes);
        var cover = await CoverTheSlotHolderAsync(seed);

        // Act
        var samples = await MeasureAsync(SearchFor(seed, cover.Token));

        // Assert
        Report("cover_scenario", samples);
        AssertStructure(samples);
    }

    private async Task<List<Sample>> MeasureAsync(FindReplacementQuery search)
    {
        var samples = new List<Sample>();
        for (var run = 0; run < WarmUpRuns + MeasuredRuns; run++)
        {
            Factory.SqlCommands.Reset();
            var startedAt = Stopwatch.GetTimestamp();
            var result = await SendAsync(search);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            if (run >= WarmUpRuns)
            {
                samples.Add(new Sample(
                    elapsed.TotalMilliseconds,
                    Factory.SqlCommands.Total,
                    Factory.SqlCommands.PerTable,
                    result.Eligible.Count,
                    result.Excluded.Count,
                    result.Eligible.Count(c => c.IsBorrowed) + result.Excluded.Count(c => c.IsBorrowed)));
            }
        }

        return samples;
    }

    private static void AssertStructure(IReadOnlyList<Sample> samples)
    {
        SisterGroupSizes.Sum().ShouldBeGreaterThan(
            RecoveryBuildConstants.CrossGroupPoolCap, "the sister groups must hold more people than the cap, or the cap is not exercised");
        samples.Count.ShouldBe(MeasuredRuns);
        foreach (var sample in samples)
        {
            sample.SqlCommands.ShouldBeGreaterThan(0, "the interceptor saw no command, so it is not attached to the host's contexts");
            (sample.Eligible + sample.Excluded).ShouldBe(JudgedCandidatesIncludingTheAbsentMember);
            sample.Borrowed.ShouldBe(RecoveryBuildConstants.CrossGroupPoolCap);
        }
    }

    private static void Report(string scenario, IReadOnlyList<Sample> samples)
    {
        var text = new StringBuilder();
        text.AppendLine(FormattableString.Invariant(
            $"scenario={scenario} runs={samples.Count} warmup_discarded={WarmUpRuns} group_people={GroupMembers + AbsentGroupMembers} people_in_sister_groups={SisterGroupSizes.Sum()} borrowed_cap={RecoveryBuildConstants.CrossGroupPoolCap}"));
        text.AppendLine(Line("wall_ms", samples.Select(s => s.Milliseconds)));
        text.AppendLine(Line("sql_commands_per_search", samples.Select(s => (double)s.SqlCommands)));
        text.AppendLine("raw wall_ms/sql_commands/eligible/excluded/borrowed per run: " + string.Join("; ", samples.Select(s =>
            FormattableString.Invariant($"{s.Milliseconds:F1}/{s.SqlCommands}/{s.Eligible}/{s.Excluded}/{s.Borrowed}"))));

        if (samples.Count > 0)
        {
            text.AppendLine("sql_commands by table label (last run): " + string.Join(", ", samples[^1].SqlCommandsPerTable
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => FormattableString.Invariant($"{entry.Key}={entry.Value}"))));
        }

        TestContext.Out.WriteLine(text.ToString());
    }

    private static string Line(string label, IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return label + ": no samples";
        }

        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: min={sorted[0]:F1} median={median:F1} max={sorted[^1]:F1} mean={sorted.Average():F1}");
    }

    private sealed record Sample(
        double Milliseconds,
        int SqlCommands,
        IReadOnlyDictionary<string, int> SqlCommandsPerTable,
        int Eligible,
        int Excluded,
        int Borrowed);
}
