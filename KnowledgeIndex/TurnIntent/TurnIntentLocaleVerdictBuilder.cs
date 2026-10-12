// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the per-locale flip evidence from out-of-fold logits: classifier and MutationIntentDetector on the same
/// items, minimal pairs, the confident-Ask decision the runtime acts on, and the forced-tool-call veto the runtime
/// only logs. The verdict's Enabled flag is TurnIntentLocaleGate.ShowsImprovement of exactly these counts.
/// </summary>
/// <param name="items">Non-ambiguous labelset items</param>
/// <param name="labels">Class index per item (TurnIntentLabel order)</param>
/// <param name="logits">Out-of-fold logits per item</param>
/// <param name="temperature">Temperature fitted on the out-of-fold logits</param>
/// <param name="askThreshold">Confident-Ask probability threshold</param>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentLocaleVerdictBuilder
{
    private const int NoDecision = -1;

    public static List<TurnIntentLocaleVerdict> Build(
        IReadOnlyList<TurnIntentProbeItem> items, int[] labels, IReadOnlyList<double[]> logits,
        double temperature, double askThreshold)
    {
        var ask = (int)TurnIntentLabel.Ask;
        var doing = (int)TurnIntentLabel.Do;

        var rows = items.Select((item, i) =>
        {
            var probabilities = TurnIntentCalibration.Probabilities(logits[i], temperature);
            var predicted = Array.IndexOf(probabilities, probabilities.Max());
            var mutation = MutationIntentDetector.IsMutationIntent(item.Text);
            var question = MutationIntentDetector.IsInformationQuestion(item.Text);
            var detector = mutation ? doing : question ? ask : NoDecision;
            return new Row(item, labels[i], predicted, predicted == ask && probabilities[ask] >= askThreshold, mutation, detector);
        }).ToList();

        return rows
            .GroupBy(row => row.Item.Locale, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => Verdict(group.Key, group.ToList(), ask, doing))
            .ToList();
    }

    private static TurnIntentLocaleVerdict Verdict(string locale, List<Row> rows, int ask, int doing)
    {
        var pairs = rows
            .Where(row => row.Item.PairId != null)
            .GroupBy(row => row.Item.PairId, StringComparer.Ordinal)
            .Where(group => group.Select(row => row.Label).OrderBy(l => l).SequenceEqual(new[] { ask, doing }.OrderBy(l => l)))
            .ToList();

        var confidentAsk = rows.Where(row => row.ConfidentAsk).ToList();
        var detectorAsk = rows.Where(row => row.Detector == ask).ToList();
        var detectorMutation = rows.Where(row => row.Mutation).ToList();
        var veto = detectorMutation.Where(row => row.ConfidentAsk).ToList();

        var counts = new TurnIntentLocaleVerdict(
            locale,
            false,
            rows.Count,
            pairs.Count,
            pairs.Count(group => group.All(row => row.Predicted == row.Label)),
            pairs.Count(group => group.All(row => row.Detector == row.Label)),
            confidentAsk.Count,
            confidentAsk.Count(row => row.Label == ask),
            rows.Count(row => row.Label == ask),
            detectorAsk.Count,
            detectorAsk.Count(row => row.Label == ask),
            detectorMutation.Count,
            veto.Count,
            veto.Count(row => row.Label == ask));

        return counts with { Enabled = TurnIntentLocaleGate.ShowsImprovement(counts) };
    }

    private sealed record Row(TurnIntentProbeItem Item, int Label, int Predicted, bool ConfidentAsk, bool Mutation, int Detector);
}
