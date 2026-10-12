// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Metrics of one cross-validated probe run.
/// </summary>
/// <param name="Name">Feature-set name</param>
/// <param name="Lambda">L2 strength used</param>
/// <param name="Count">Number of evaluated items</param>
/// <param name="Accuracy">Share of items classified correctly</param>
/// <param name="MinimalPairCount">Number of (pair, locale) units with both members evaluated</param>
/// <param name="MinimalPairAccuracy">Share of those units where both members are classified correctly</param>
/// <param name="Confusion">Counts indexed [label][prediction] in TurnIntentProbeSettings.Classes order</param>
/// <param name="PerLocale">Locale, item count, accuracy, minimal-pair count, minimal-pair accuracy</param>
/// <param name="Temperature">Fitted temperature</param>
/// <param name="EceRaw">Expected calibration error at temperature 1</param>
/// <param name="EceScaled">Expected calibration error at the fitted temperature (optimistic)</param>
/// <param name="Selective">Confidence threshold, coverage, accuracy on the covered items (scaled probabilities)</param>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public sealed record TurnIntentProbeResult(
    string Name,
    double Lambda,
    int Count,
    double Accuracy,
    int MinimalPairCount,
    double MinimalPairAccuracy,
    int[][] Confusion,
    IReadOnlyList<(string Locale, int Count, double Accuracy, int PairCount, double PairAccuracy)> PerLocale,
    double Temperature,
    double EceRaw,
    double EceScaled,
    IReadOnlyList<(double Threshold, double Coverage, double Accuracy)> Selective)
{
    public double Precision(int classIndex)
    {
        var predicted = Confusion.Sum(row => row[classIndex]);
        return predicted == 0 ? double.NaN : (double)Confusion[classIndex][classIndex] / predicted;
    }

    public double Recall(int classIndex)
    {
        var actual = Confusion[classIndex].Sum();
        return actual == 0 ? double.NaN : (double)Confusion[classIndex][classIndex] / actual;
    }
}
