// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Prints the per-origin split (handwritten, synthetic, unknown) of a turn-eval run and, when the run contained
/// rights-refusal items, the rights verdicts, so the headline numbers are never read as one mixed population.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Assistant;

internal static class TurnEvalScorecardBreakdown
{
    public static void Write(TurnEvalRunResult result)
    {
        foreach (var (origin, dimensions) in result.DimensionsByOrigin)
        {
            TestContext.WriteLine(
                $"Origin {origin,-12} items={dimensions.ItemsTotal}, passed={dimensions.ItemsPassed}, " +
                $"excluded={dimensions.ItemsExcluded}, errored={dimensions.ItemsErrored}, " +
                $"composite={TurnEvalScorer.ComputeComposite(dimensions):F4}, tool={Format(dimensions.ToolAccuracy)}, " +
                $"slot={Format(dimensions.SlotAccuracy)}, noTool={Format(dimensions.NoToolAccuracy)}, " +
                $"retrieval={Format(dimensions.RetrievalHit)}, selection={Format(dimensions.SelectionHit)}, " +
                $"reached={Format(dimensions.ReachedHit)}");
        }

        var overall = result.Dimensions;
        if (overall?.RightsRefusalAccuracy != null)
        {
            TestContext.WriteLine(
                $"RightsRefusalAccuracy:  {Format(overall.RightsRefusalAccuracy)} (no side-effecting call and no invented result; gates Passed)");
            TestContext.WriteLine($"RightsNoWriteRate:      {Format(overall.RightsNoWriteRate)} (the no-write part alone)");
            TestContext.WriteLine(
                $"RefusalStatedRate:      {Format(overall.RefusalStatedRate)} (lexical lower bound; reported only)");
            TestContext.WriteLine(
                $"RightsToolForcedRate:   {Format(overall.RightsToolForcedRate)} (production forces a tool call on these items; a plain refusal is then impossible)");
            TestContext.WriteLine(
                "Limits: replay is one step plus an optional lookup follow-up, only the first tool call per step is seen, refusal wording is lexical.");
        }

        TestContext.WriteLine(string.Empty);
    }

    private static string Format(double? value) => value?.ToString("F4") ?? "n/a";
}
