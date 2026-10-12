// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Turns out-of-fold logits into the probe metrics: accuracy, confusion, minimal-pair accuracy, per-locale accuracy,
/// calibration and selective accuracy.
/// </summary>
/// <param name="name">Feature-set name</param>
/// <param name="lambda">L2 strength used</param>
/// <param name="items">The evaluated items</param>
/// <param name="labels">Class index per item</param>
/// <param name="logits">Out-of-fold logits per item</param>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentProbeEvaluator
{
    public static TurnIntentProbeResult Evaluate(
        string name, double lambda, IReadOnlyList<TurnIntentProbeItem> items, int[] labels, IReadOnlyList<double[]> logits)
    {
        var classCount = TurnIntentProbeSettings.Classes.Length;
        var predictions = logits.Select(l => Array.IndexOf(l, l.Max())).ToArray();

        var confusion = new int[classCount][];
        for (var c = 0; c < classCount; c++)
        {
            confusion[c] = new int[classCount];
        }

        for (var i = 0; i < items.Count; i++)
        {
            confusion[labels[i]][predictions[i]]++;
        }

        var pairs = PairUnits(items, labels, predictions);
        var temperature = TurnIntentCalibration.FitTemperature(logits, labels);

        var perLocale = items
            .Select((item, index) => (item.Locale, Index: index))
            .GroupBy(entry => entry.Locale, StringComparer.Ordinal)
            .OrderBy(group => TurnIntentProbeSettings.CoreLocales.Contains(group.Key) ? 0 : 1)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var correct = group.Count(entry => predictions[entry.Index] == labels[entry.Index]);
                var localePairs = pairs.Where(p => p.Locale == group.Key).ToList();
                return (group.Key, group.Count(), (double)correct / group.Count(),
                    localePairs.Count, Ratio(localePairs.Count(p => p.BothCorrect), localePairs.Count));
            })
            .ToList();

        var selective = TurnIntentProbeSettings.ConfidenceThresholds
            .Select(threshold => Selective(threshold, logits, labels, temperature))
            .ToList();

        return new TurnIntentProbeResult(
            name,
            lambda,
            items.Count,
            (double)Enumerable.Range(0, items.Count).Count(i => predictions[i] == labels[i]) / items.Count,
            pairs.Count,
            Ratio(pairs.Count(p => p.BothCorrect), pairs.Count),
            confusion,
            perLocale,
            temperature,
            TurnIntentCalibration.ExpectedCalibrationError(logits, labels, 1.0),
            TurnIntentCalibration.ExpectedCalibrationError(logits, labels, temperature),
            selective);
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? double.NaN : (double)numerator / denominator;

    private static List<(string Locale, bool BothCorrect)> PairUnits(
        IReadOnlyList<TurnIntentProbeItem> items, int[] labels, int[] predictions)
    {
        var askIndex = Array.IndexOf(TurnIntentProbeSettings.Classes, TurnIntentProbeSettings.LabelAsk);
        var doIndex = Array.IndexOf(TurnIntentProbeSettings.Classes, TurnIntentProbeSettings.LabelDo);

        return Enumerable.Range(0, items.Count)
            .Where(i => items[i].PairId != null)
            .GroupBy(i => (items[i].PairId, items[i].Locale))
            .Where(group => group.Select(i => labels[i]).OrderBy(l => l).SequenceEqual(
                new[] { askIndex, doIndex }.OrderBy(l => l)))
            .Select(group => (group.Key.Locale, group.All(i => predictions[i] == labels[i])))
            .ToList();
    }

    private static (double Threshold, double Coverage, double Accuracy) Selective(
        double threshold, IReadOnlyList<double[]> logits, int[] labels, double temperature)
    {
        var covered = 0;
        var correct = 0;
        for (var i = 0; i < logits.Count; i++)
        {
            var probabilities = TurnIntentCalibration.Probabilities(logits[i], temperature);
            var predicted = Array.IndexOf(probabilities, probabilities.Max());
            if (probabilities[predicted] >= threshold)
            {
                covered++;
                correct += predicted == labels[i] ? 1 : 0;
            }
        }

        return (threshold, (double)covered / logits.Count, Ratio(correct, covered));
    }
}
