// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Trainer of the production turn-intent head (scripts/train-turn-intent-head.ps1 runs it). Uses the probe's
/// pre-registered headline settings (embedding plus hand features, lambda 0.01, 300 iterations): grouped 5-fold
/// cross-validation yields out-of-fold logits, the temperature is fitted on them and the per-locale flip evidence is
/// computed against MutationIntentDetector on the same items (TurnIntentLocaleVerdictBuilder). The final head is then
/// trained on all non-ambiguous items and written, together with the evidence, to
/// Klacks.Api/Application/Klacksy/turn-intent-head.v1.json. Gated by KLACKS_TURN_INTENT_TRAIN=1 because it loads the
/// ONNX model and overwrites a tracked file; KnowledgeIndex__ModelsRoot and KLACKS_TURN_INTENT_PROBE_CACHE work as in
/// the probe.
/// </summary>

using System.Globalization;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Category("SlowModelLoad")]
public class TurnIntentHeadTrainingTests
{
    private const string SignificantDigits = "G6";

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [OneTimeSetUp]
    public void GateOnEnvironmentVariable()
    {
        if (Environment.GetEnvironmentVariable(TurnIntentProbeSettings.TrainEnvironmentVariable)
            != TurnIntentProbeSettings.EnableValue)
        {
            Assert.Ignore($"Set {TurnIntentProbeSettings.TrainEnvironmentVariable}={TurnIntentProbeSettings.EnableValue} to train the turn-intent head.");
        }
    }

    [Test]
    public async Task Train_WritesTheHeadWithPerLocaleFlipEvidence()
    {
        var report = new StringBuilder();
        TurnIntentEmbeddingEnvironment.LoadLanguagePlugins(report);

        var apiDirectory = TurnIntentEmbeddingEnvironment.ResolveApiSourceDirectory();
        var labelsetPath = Path.Combine(apiDirectory, TurnIntentProbeSettings.GoldsetRelativePath);
        var items = TurnIntentProbeGoldset.LoadNonAmbiguous(labelsetPath);
        var labels = items.Select(item => (int)Enum.Parse<TurnIntentLabel>(item.Label)).ToArray();

        var (embeddings, embeddingSpaceId) = await TurnIntentEmbeddingEnvironment.EmbedAsync(
            [.. items.Select(item => item.Text)], report);
        var raw = items.Select((item, i) => Concatenate(embeddings[i], TurnIntentHandFeatures.Extract(item.Text))).ToList();
        var multipliers = Enumerable.Repeat(1.0, raw[0].Length).ToArray();

        var folds = TurnIntentGroupedFolds.Assign(items, TurnIntentProbeSettings.FoldCount, TurnIntentProbeSettings.FoldSeed);
        var oofLogits = TurnIntentCrossValidation.OutOfFoldLogits(
            raw, labels, folds, multipliers, TurnIntentProbeSettings.HeadlineLambda);
        var temperature = TurnIntentCalibration.FitTemperature(oofLogits, labels);
        var verdicts = TurnIntentLocaleVerdictBuilder.Build(
            items, labels, oofLogits, temperature, TurnIntentDefaults.AskConfidenceThreshold);

        var standardizer = FeatureStandardizer.Fit(raw, multipliers);
        var model = SoftmaxRegressionTrainer.Train(
            raw.Select(standardizer.Transform).ToList(), labels, TurnIntentProbeSettings.Classes.Length,
            TurnIntentProbeSettings.HeadlineLambda, TurnIntentProbeSettings.Iterations);

        var head = new TurnIntentHeadWeights
        {
            Version = TurnIntentDefaults.HeadFormatVersion,
            EmbeddingSpaceId = embeddingSpaceId,
            DatasetHash = TurnIntentLabelsetFingerprint.Compute(File.ReadAllText(labelsetPath)),
            FeatureSourceHash = TurnIntentFeatureSourceFingerprint.Compute(apiDirectory),
            TrainedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Lambda = TurnIntentProbeSettings.HeadlineLambda,
            Iterations = TurnIntentProbeSettings.Iterations,
            Classes = Enum.GetNames<TurnIntentLabel>(),
            HandFeatureNames = TurnIntentHandFeatures.Names,
            Means = Round(standardizer.Means),
            Scales = Round(standardizer.Scales),
            Weights = [.. model.Weights.Select(Round)],
            Bias = Round(model.Bias),
            Temperature = temperature,
            AskThreshold = TurnIntentDefaults.AskConfidenceThreshold,
            Locales = verdicts,
        };

        var outputPath = Path.Combine(apiDirectory, TurnIntentDefaults.HeadFileRelativePath);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(head, OutputOptions) + "\n");

        report.AppendLine($"Items {items.Count}; temperature {temperature:F2}; head written to {outputPath}");
        AppendVerdicts(report, verdicts);
        TestContext.Out.WriteLine(report.ToString());

        verdicts.ShouldNotBeEmpty();
    }

    private static double[] Concatenate(float[] embedding, double[] hand)
    {
        var row = new double[embedding.Length + hand.Length];
        for (var j = 0; j < embedding.Length; j++)
        {
            row[j] = embedding[j];
        }

        Array.Copy(hand, 0, row, embedding.Length, hand.Length);
        return row;
    }

    private static double[] Round(IEnumerable<double> values) =>
        [.. values.Select(value => double.Parse(value.ToString(SignificantDigits, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))];

    private static void AppendVerdicts(StringBuilder report, List<TurnIntentLocaleVerdict> verdicts)
    {
        report.AppendLine($"{"locale",-7}{"on",4}{"n",5}{"pairs",6}{"clfPair",9}{"detPair",9}{"clfPairLB",10}{"confAsk",8}{"askPrec",9}{"askPrecLB",10}{"askRec",8}{"detAskP",8}{"detAskR",8}{"veto",6}{"vetoP",7}");
        foreach (var v in verdicts)
        {
            report.AppendLine(
                $"{v.Locale,-7}{(v.Enabled ? "ON" : "-"),4}{v.Items,5}{v.Pairs,6}{Pct(v.ClassifierPairsCorrect, v.Pairs),9}" +
                $"{Pct(v.DetectorPairsCorrect, v.Pairs),9}{TurnIntentLocaleGate.WilsonLowerBound(v.ClassifierPairsCorrect, v.Pairs),10:P1}" +
                $"{v.ConfidentAskPredicted,8}{Pct(v.ConfidentAskCorrect, v.ConfidentAskPredicted),9}" +
                $"{TurnIntentLocaleGate.WilsonLowerBound(v.ConfidentAskCorrect, v.ConfidentAskPredicted),10:P1}" +
                $"{Pct(v.ConfidentAskCorrect, v.AskItems),8}{Pct(v.DetectorAskCorrect, v.DetectorAskPredicted),8}" +
                $"{Pct(v.DetectorAskCorrect, v.AskItems),8}{v.VetoShadowPredicted,6}{Pct(v.VetoShadowCorrect, v.VetoShadowPredicted),7}");
        }
    }

    private static string Pct(int numerator, int denominator) =>
        denominator == 0 ? "n/a" : (100.0 * numerator / denominator).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
