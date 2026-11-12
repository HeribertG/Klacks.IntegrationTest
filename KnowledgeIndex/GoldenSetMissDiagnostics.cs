// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Formats the diagnostic lines of an assembler miss: the tools the model was offered with their ToolsetSource and
/// retrieval score, whether an accepted target was retrieved, in the selection before the provider cap or cut by it
/// (SkillToolsetTrace), and - when the fixture runs below the reference cap - the source the target carries in a
/// re-assembly at the reference cap, which tells "cut by the budget" apart from "never selected".
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal static class GoldenSetMissDiagnostics
{
    public static IEnumerable<string> Describe(
        HardGoldenSetItem item, int index, SkillToolsetResult toolset, SkillToolsetResult? referenceToolset)
    {
        var trace = toolset.Trace;
        var retrievedRank = trace?.RetrievedNames.ToList().FindIndex(item.Accepts) ?? -1;
        var inPreCap = trace?.PreCapNames.Any(item.Accepts) == true;
        var truncated = trace?.TruncatedNames.Any(item.Accepts) == true;
        var reference = referenceToolset?.Functions.FirstOrDefault(f => item.Accepts(f.Name));

        yield return
            $"MISS DETAIL | #{index} | expected={item.ExpectedDisplay} | " +
            $"retrievedRank={(retrievedRank < 0 ? "-" : (retrievedRank + 1).ToString(CultureInfo.InvariantCulture))}" +
            $"/{trace?.RetrievedNames.Count ?? 0} | preCap={inPreCap} | truncatedByCap={truncated} | " +
            $"preCapSize={trace?.PreCapNames.Count ?? 0} | " +
            $"atReferenceCap={(referenceToolset is null ? "n/a" : reference is null ? "MISS" : $"{reference.ToolsetSource}:{FormatScore(reference.RetrievalScore)}")} | " +
            $"query=\"{item.Query}\"";
        yield return
            $"  OFFERED: {string.Join(", ", toolset.Functions.Select(f => $"{f.Name}:{f.ToolsetSource}:{FormatScore(f.RetrievalScore)}"))}";
        if (trace is { TruncatedNames.Count: > 0 })
        {
            yield return "  CUT BY CAP (source at reference cap): " + string.Join(", ", trace.TruncatedNames.Select(name =>
            {
                var atReference = referenceToolset?.Functions.FirstOrDefault(f => f.Name == name);
                return atReference is null ? $"{name}:?" : $"{name}:{atReference.ToolsetSource}:{FormatScore(atReference.RetrievalScore)}";
            }));
        }
    }

    private static string FormatScore(double? score) =>
        score.HasValue ? score.Value.ToString("F4", CultureInfo.InvariantCulture) : "-";
}
