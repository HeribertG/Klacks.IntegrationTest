// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Counts how the score part of CorrectionOutcomeComposer.BuildClarification would decide on an assembled toolset:
/// the deterministic candidates (CorrectionOutcomeComposer.DeterministicCandidates) are ordered exactly like Compose
/// orders them, and the two best fall into one of the rule's buckets. Labels and language are not checked here, so
/// "ambiguous" is the share of turns whose scores alone would let the two-option question through; the real question
/// needs authored labels on top. Used to compare the cross-encoder scores (Always) with the cosine scores (Never) the
/// same tolerance (GracefulCorrectionDefaults.CorrectionAmbiguityTolerance) is applied to.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed class CorrectionAmbiguityTally
{
    private readonly List<double> _scoredGaps = [];

    public int Turns { get; private set; }

    public int FewerThanTwo { get; private set; }

    public int BothScoredWithinTolerance { get; private set; }

    public int BothScoredApart { get; private set; }

    public int OneScored { get; private set; }

    public int NeitherScored { get; private set; }

    public int Ambiguous => BothScoredWithinTolerance + NeitherScored;

    /// <summary>
    /// Classifies one assembled toolset and returns the deterministic candidates in Compose order.
    /// </summary>
    /// <param name="functions">The assembled toolset</param>
    public List<LLMFunction> Add(IReadOnlyList<LLMFunction> functions)
    {
        Turns++;
        var candidates = OrderedCandidates(functions);
        if (candidates.Count < GracefulCorrectionDefaults.ClarificationCandidateCount)
        {
            FewerThanTwo++;
            return candidates;
        }

        var best = candidates[0].RetrievalScore;
        var runnerUp = candidates[1].RetrievalScore;
        if (best.HasValue && runnerUp.HasValue)
        {
            var gap = best.Value - runnerUp.Value;
            _scoredGaps.Add(gap);
            if (gap <= GracefulCorrectionDefaults.CorrectionAmbiguityTolerance)
            {
                BothScoredWithinTolerance++;
            }
            else
            {
                BothScoredApart++;
            }
        }
        else if (best.HasValue || runnerUp.HasValue)
        {
            OneScored++;
        }
        else
        {
            NeitherScored++;
        }

        return candidates;
    }

    public static List<LLMFunction> OrderedCandidates(IReadOnlyList<LLMFunction> functions) =>
        CorrectionOutcomeComposer.DeterministicCandidates(functions)
            .OrderByDescending(f => f.RetrievalScore.HasValue)
            .ThenByDescending(f => f.RetrievalScore ?? 0.0)
            .ToList();

    public static string Describe(IEnumerable<LLMFunction> candidates) =>
        string.Join(", ", candidates.Select(c =>
            $"{c.Name}:{c.ToolsetSource}:{(c.RetrievalScore.HasValue ? c.RetrievalScore.Value.ToString("F4", CultureInfo.InvariantCulture) : "-")}"));

    public void Write(string label)
    {
        var median = _scoredGaps.Count == 0
            ? double.NaN
            : _scoredGaps.OrderBy(g => g).ElementAt(_scoredGaps.Count / 2);
        TestContext.WriteLine(
            $"CORRECTION PROXY {label} | turns={Turns} | <2 deterministic={FewerThanTwo} | " +
            $"both scored gap<={GracefulCorrectionDefaults.CorrectionAmbiguityTolerance.ToString(CultureInfo.InvariantCulture)}={BothScoredWithinTolerance} | " +
            $"both scored apart={BothScoredApart} | one scored={OneScored} | neither scored={NeitherScored} | " +
            $"score-ambiguous={Ambiguous} | median scored gap={median.ToString("F4", CultureInfo.InvariantCulture)} " +
            $"(n={_scoredGaps.Count})");
    }
}
