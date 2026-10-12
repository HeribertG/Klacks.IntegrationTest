// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Plain-text rendering of TurnIntentProbeResult for the test output.
/// </summary>
/// <param name="result">The metrics to render</param>

using System.Globalization;
using System.Text;

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentProbeReportFormatter
{
    public static string Format(TurnIntentProbeResult result)
    {
        var classes = TurnIntentProbeSettings.Classes;
        var report = new StringBuilder();

        report.AppendLine($"=== {result.Name} | lambda={result.Lambda.ToString(CultureInfo.InvariantCulture)} | n={result.Count} ===");
        report.AppendLine($"accuracy {Pct(result.Accuracy)} | minimal-pair accuracy {Pct(result.MinimalPairAccuracy)} ({result.MinimalPairCount} pair x locale units)");
        report.AppendLine($"ECE raw (T=1) {result.EceRaw:F3} | fitted T={result.Temperature:F2} -> ECE {result.EceScaled:F3} (T fitted on the same out-of-fold logits, optimistic)");

        report.AppendLine($"{"class",-7}{"precision",10}{"recall",9}   confusion (rows = label; cols = " + string.Join("/", classes) + ")");
        for (var c = 0; c < classes.Length; c++)
        {
            report.AppendLine(
                $"{classes[c],-7}{Pct(result.Precision(c)),10}{Pct(result.Recall(c)),9}   " +
                string.Join(" ", result.Confusion[c].Select(count => count.ToString(CultureInfo.InvariantCulture).PadLeft(4))));
        }

        report.AppendLine($"{"locale",-7}{"n",5}{"acc",8}{"pairs",7}{"pairAcc",9}");
        foreach (var entry in result.PerLocale)
        {
            report.AppendLine($"{entry.Locale,-7}{entry.Count,5}{Pct(entry.Accuracy),8}{entry.PairCount,7}{Pct(entry.PairAccuracy),9}");
        }

        report.AppendLine("selective (scaled probabilities): " + string.Join(" | ", result.Selective.Select(s =>
            $">={s.Threshold:F1}: coverage {Pct(s.Coverage)}, acc {Pct(s.Accuracy)}")));
        return report.ToString();
    }

    public static string Pct(double value) =>
        double.IsNaN(value) ? "n/a" : (100.0 * value).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
