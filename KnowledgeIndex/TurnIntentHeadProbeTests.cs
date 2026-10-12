// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Feasibility probe for a turn-intent head (Ask / Do / Other) on top of the REAL multilingual-e5-base-fp16 embedding.
/// Embeds the bare text of every non-ambiguous item of turn-intent-v1.json with EmbedQueryAsync (which adds the e5
/// "query: " prefix), trains a multinomial logistic regression with grouped 5-fold cross-validation (folds split by
/// connected templateId/pairId components, so translations and both halves of a minimal pair stay together) and reports
/// accuracy, per-class precision/recall, minimal-pair accuracy, per-locale accuracy and calibration for (a) embedding
/// only and (b) embedding plus deterministic hand features. No database and no application host: the embedding
/// provider is constructed directly. Gated by KLACKS_TURN_INTENT_PROBE=1; point KnowledgeIndex__ModelsRoot at a
/// directory that holds multilingual-e5-base-fp16/ (model_fp16.onnx + tokenizer.json), otherwise the build output's
/// Cache/Models is used. Stop criterion from the plan: minimal-pair accuracy below 0.85 means the probe failed.
/// Reporting only - the verdict is printed, not asserted.
/// </summary>

using System.Globalization;
using System.Text;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Category("SlowModelLoad")]
public class TurnIntentHeadProbeTests
{
    private const string EmbeddingOnly = "(a) embedding only";
    private const string EmbeddingPlusHand = "(b) embedding + hand features";
    private const string EmbeddingPlusHandScaled = "(b3) embedding + hand features x3 [sensitivity]";
    private const string HandOnly = "(c) hand features only [ablation]";

    private sealed record Variant(string Name, bool UseEmbedding, double HandMultiplier);

    private static readonly Variant[] Variants =
    [
        new(EmbeddingOnly, true, 0.0),
        new(EmbeddingPlusHand, true, 1.0),
        new(EmbeddingPlusHandScaled, true, TurnIntentProbeSettings.HandFeatureScaleSensitivity),
        new(HandOnly, false, 1.0),
    ];

    [OneTimeSetUp]
    public void GateOnEnvironmentVariable()
    {
        if (Environment.GetEnvironmentVariable(TurnIntentProbeSettings.EnableEnvironmentVariable)
            != TurnIntentProbeSettings.EnableValue)
        {
            Assert.Ignore($"Set {TurnIntentProbeSettings.EnableEnvironmentVariable}={TurnIntentProbeSettings.EnableValue} to run the turn-intent feasibility probe.");
        }
    }

    [Test]
    public async Task Probe_EmbeddingHeadSeparatesAskDoOther_UnderGroupedCrossValidation()
    {
        var report = new StringBuilder();
        TurnIntentEmbeddingEnvironment.LoadLanguagePlugins(report);

        var goldsetPath = TurnIntentProbeGoldset.DefaultPath;
        if (!File.Exists(goldsetPath))
        {
            Assert.Inconclusive($"Labelset not found in the build output: {goldsetPath}");
        }

        var items = TurnIntentProbeGoldset.LoadNonAmbiguous(goldsetPath);
        var labels = items.Select(item => Array.IndexOf(TurnIntentProbeSettings.Classes, item.Label)).ToArray();
        labels.ShouldAllBe(label => label >= 0);

        var folds = TurnIntentGroupedFolds.Assign(items, TurnIntentProbeSettings.FoldCount, TurnIntentProbeSettings.FoldSeed);
        AssertNoGroupSpansTwoFolds(items, folds);

        report.AppendLine($"Items (non-ambiguous): {items.Count}; fold sizes: {string.Join("/", Enumerable.Range(0, TurnIntentProbeSettings.FoldCount).Select(f => folds.Count(x => x == f)))}");
        report.AppendLine($"Label counts: {string.Join(", ", TurnIntentProbeSettings.Classes.Select((c, i) => $"{c} {labels.Count(l => l == i)}"))}");

        var (embeddings, _) = await TurnIntentEmbeddingEnvironment.EmbedAsync([.. items.Select(item => item.Text)], report);

        var hand = items.Select(item => TurnIntentHandFeatures.Extract(item.Text)).ToList();
        report.AppendLine($"Hand features ({TurnIntentHandFeatures.Names.Length}): {string.Join(", ", TurnIntentHandFeatures.Names)}");
        report.AppendLine();

        var results = new List<TurnIntentProbeResult>();
        foreach (var variant in Variants)
        {
            var raw = BuildRawFeatures(variant, embeddings, hand, out var multipliers);
            foreach (var lambda in TurnIntentProbeSettings.LambdaSweep)
            {
                var logits = TurnIntentCrossValidation.OutOfFoldLogits(raw, labels, folds, multipliers, lambda);
                var result = TurnIntentProbeEvaluator.Evaluate(variant.Name, lambda, items, labels, logits);
                results.Add(result);

                if (lambda == TurnIntentProbeSettings.HeadlineLambda)
                {
                    report.AppendLine(TurnIntentProbeReportFormatter.Format(result));
                }
            }
        }

        AppendSweep(report, results);
        AppendVerdict(report, results);
        TestContext.Out.WriteLine(report.ToString());

        results.ShouldNotBeEmpty();
    }

    private static List<double[]> BuildRawFeatures(
        Variant variant, float[][] embeddings, List<double[]> hand, out double[] multipliers)
    {
        var embeddingDimension = variant.UseEmbedding ? embeddings[0].Length : 0;
        var handDimension = variant.HandMultiplier > 0.0 ? TurnIntentHandFeatures.Names.Length : 0;

        multipliers = new double[embeddingDimension + handDimension];
        Array.Fill(multipliers, 1.0, 0, embeddingDimension);
        Array.Fill(multipliers, variant.HandMultiplier, embeddingDimension, handDimension);

        var raw = new List<double[]>(embeddings.Length);
        for (var i = 0; i < embeddings.Length; i++)
        {
            var row = new double[embeddingDimension + handDimension];
            for (var j = 0; j < embeddingDimension; j++)
            {
                row[j] = embeddings[i][j];
            }

            Array.Copy(hand[i], 0, row, embeddingDimension, handDimension);
            raw.Add(row);
        }

        return raw;
    }

    private static void AssertNoGroupSpansTwoFolds(IReadOnlyList<TurnIntentProbeItem> items, int[] folds)
    {
        var byTemplate = items.Select((item, i) => (Key: item.TemplateId, Fold: folds[i])).GroupBy(x => x.Key);
        byTemplate.ShouldAllBe(group => group.Select(x => x.Fold).Distinct().Count() == 1);

        var byPair = items.Select((item, i) => (Key: item.PairId, Fold: folds[i])).Where(x => x.Key != null).GroupBy(x => x.Key);
        byPair.ShouldAllBe(group => group.Select(x => x.Fold).Distinct().Count() == 1);
    }

    private static void AppendSweep(StringBuilder report, List<TurnIntentProbeResult> results)
    {
        report.AppendLine("=== Lambda sweep (informational; the headline figure is lambda=" +
                          TurnIntentProbeSettings.HeadlineLambda.ToString(CultureInfo.InvariantCulture) + ", fixed before the run) ===");
        report.AppendLine($"{"feature set",-48}{"lambda",8}{"acc",8}{"pairAcc",9}{"Ask-P",8}{"Do-P",8}{"ECE raw",9}{"ECE T",8}");
        var askIndex = Array.IndexOf(TurnIntentProbeSettings.Classes, TurnIntentProbeSettings.LabelAsk);
        var doIndex = Array.IndexOf(TurnIntentProbeSettings.Classes, TurnIntentProbeSettings.LabelDo);
        foreach (var result in results)
        {
            report.AppendLine(
                $"{result.Name,-48}{result.Lambda.ToString(CultureInfo.InvariantCulture),8}" +
                $"{TurnIntentProbeReportFormatter.Pct(result.Accuracy),8}{TurnIntentProbeReportFormatter.Pct(result.MinimalPairAccuracy),9}" +
                $"{TurnIntentProbeReportFormatter.Pct(result.Precision(askIndex)),8}{TurnIntentProbeReportFormatter.Pct(result.Precision(doIndex)),8}" +
                $"{result.EceRaw,9:F3}{result.EceScaled,8:F3}");
        }

        report.AppendLine();
    }

    private static void AppendVerdict(StringBuilder report, List<TurnIntentProbeResult> results)
    {
        var askIndex = Array.IndexOf(TurnIntentProbeSettings.Classes, TurnIntentProbeSettings.LabelAsk);
        var doIndex = Array.IndexOf(TurnIntentProbeSettings.Classes, TurnIntentProbeSettings.LabelDo);

        report.AppendLine("=== Verdict against the plan (headline lambda only) ===");
        foreach (var name in new[] { EmbeddingOnly, EmbeddingPlusHand })
        {
            var result = results.Single(r => r.Name == name && r.Lambda == TurnIntentProbeSettings.HeadlineLambda);
            var stop = result.MinimalPairAccuracy < TurnIntentProbeSettings.StopCriterionMinimalPairAccuracy;
            report.AppendLine(
                $"{name}: minimal-pair accuracy {TurnIntentProbeReportFormatter.Pct(result.MinimalPairAccuracy)} -> " +
                (stop ? $"BELOW the stop criterion {TurnIntentProbeSettings.StopCriterionMinimalPairAccuracy:P0}: probe FAILED" : $"at/above the stop criterion {TurnIntentProbeSettings.StopCriterionMinimalPairAccuracy:P0}: probe passed"));
            report.AppendLine(
                $"    acceptance figures (before integration): pair>={TurnIntentProbeSettings.AcceptanceMinimalPairAccuracy:F2} -> {result.MinimalPairAccuracy >= TurnIntentProbeSettings.AcceptanceMinimalPairAccuracy}; " +
                $"Do-precision>={TurnIntentProbeSettings.AcceptanceDoPrecision:F2} -> {TurnIntentProbeReportFormatter.Pct(result.Precision(doIndex))}; " +
                $"Ask-precision>={TurnIntentProbeSettings.AcceptanceAskPrecision:F2} -> {TurnIntentProbeReportFormatter.Pct(result.Precision(askIndex))}; " +
                $"ECE<={TurnIntentProbeSettings.AcceptanceEce:F2} -> raw {result.EceRaw:F3}, scaled {result.EceScaled:F3}");
        }
    }
}
