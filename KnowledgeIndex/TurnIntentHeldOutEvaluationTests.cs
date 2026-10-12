// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Held-out evaluation of the DEPLOYED turn-intent head (turn-intent-head.v1.json) on a corpus the trainer never saw:
/// natively written messages in all 25 locales, labelled twice independently (disagreements are excluded). Runs
/// inference only - the real e5 embedding, the real hand features with the language plugins loaded as in Program.cs
/// and the head exactly as the runtime reads it. Reports per locale, per category and for the special sets (context
/// fragments, question-mark minimal pairs, code-switching, romanised script), the detector baseline, a hand-feature
/// ablation, the effect of a UI language that differs from the message language (the locale gate resolves the UI
/// language) and the embedding latency against the runtime timeout. Asserts, pooled over the enabled locales on the
/// main set, the two claims the runtime relies on: confident-Ask precision (Wilson lower bound) and a low rate of
/// Do messages mistaken for a confident Ask. Gated by KLACKS_TURN_INTENT_HELDOUT=1; KnowledgeIndex__ModelsRoot must
/// point at the e5 model; KLACKS_TURN_INTENT_PROBE_CACHE caches embeddings; KLACKS_TURN_INTENT_HELDOUT_OUT receives
/// every prediction as JSON; KLACKS_TURN_INTENT_HELDOUT_CORPUS evaluates another corpus file of the same format.
/// </summary>

using System.Globalization;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Category("SlowModelLoad")]
public class TurnIntentHeldOutEvaluationTests
{
    private const string RowFormat = "{0,-12}{1,5}{2,5}{3,5}{4,5}{5,7}{6,8}{7,8}{8,8}{9,8}{10,8}{11,8}{12,9}{13,8}{14,8}";

    [OneTimeSetUp]
    public void GateOnEnvironmentVariable()
    {
        if (Environment.GetEnvironmentVariable(TurnIntentHeldOutSettings.EnableEnvironmentVariable)
            != TurnIntentProbeSettings.EnableValue)
        {
            Assert.Ignore($"Set {TurnIntentHeldOutSettings.EnableEnvironmentVariable}={TurnIntentProbeSettings.EnableValue} to run the held-out turn-intent evaluation.");
        }
    }

    [Test]
    public async Task DeployedHead_OnHeldOutCorpus_KeepsConfidentAskPrecisionInEnabledLocales()
    {
        var report = new StringBuilder();
        TurnIntentEmbeddingEnvironment.LoadLanguagePlugins(report);

        var head = LoadDeployedHead();
        var corpusPath = TurnIntentHeldOutCorpus.DefaultPath;
        if (!File.Exists(corpusPath))
        {
            Assert.Inconclusive($"Held-out corpus not found in the build output: {corpusPath}");
        }

        var items = TurnIntentHeldOutCorpus.LoadAgreed(corpusPath);
        report.AppendLine($"Head trained {head.TrainedAtUtc}, dataset {head.DatasetHash[..12]}, T={head.Temperature:F2}, askThreshold={head.AskThreshold:F2}");
        report.AppendLine($"Held-out items (agreed labels): {items.Count}");

        var (embeddings, spaceId) = await TurnIntentEmbeddingEnvironment.EmbedAsync([.. items.Select(item => item.Text)], report);
        spaceId.ShouldBe(head.EmbeddingSpaceId);

        var predictions = items.Select((item, i) => Predict(head, item, embeddings[i], item.Locale)).ToList();
        var main = predictions.Where(p => p.Item.Set == TurnIntentHeldOutSettings.SetMain).ToList();
        var enabledMain = main.Where(p => p.LocaleEnabled).ToList();

        report.AppendLine();
        AppendTable(report, "Main set per locale (ON = locale enabled at runtime)", main.GroupBy(p => p.Item.Locale)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Name: g.Key + (g.First().LocaleEnabled ? " ON" : " off"), Rows: (IReadOnlyCollection<TurnIntentHeldOutPrediction>)g.ToList())));
        AppendTable(report, "Main set pooled", [
            ("enabled", enabledMain),
            ("disabled", main.Where(p => !p.LocaleEnabled).ToList()),
            ("all", main),
        ]);
        AppendTable(report, "Main set per category (all locales)", main.GroupBy(p => p.Item.Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Rows: (IReadOnlyCollection<TurnIntentHeldOutPrediction>)g.ToList())));
        AppendTable(report, "Special sets (not asserted)", predictions.Where(p => p.Item.Set != TurnIntentHeldOutSettings.SetMain)
            .GroupBy(p => p.Item.Set)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Rows: (IReadOnlyCollection<TurnIntentHeldOutPrediction>)g.ToList())));

        AppendQuestionMarkPairs(report, predictions);
        AppendForeignUiLanguage(report, head, items, embeddings);
        await AppendLatencyAsync(report, items);
        WriteResults(predictions);

        TestContext.Out.WriteLine(report.ToString());

        var pooled = TurnIntentHeldOutMetrics.Compute(enabledMain);
        pooled.ConfidentAskPredicted.ShouldBeGreaterThan(0);
        pooled.PrecisionLowerBound.ShouldBeGreaterThanOrEqualTo(
            TurnIntentHeldOutSettings.MinimumPooledConfidentAskPrecisionLowerBound,
            $"pooled confident-Ask precision {pooled.ConfidentAskCorrect}/{pooled.ConfidentAskPredicted}");
        pooled.DoToConfidentAskRate.ShouldBeLessThanOrEqualTo(
            TurnIntentHeldOutSettings.MaximumPooledDoToConfidentAskRate,
            $"Do mistaken for a confident Ask: {pooled.DoAsConfidentAsk}/{pooled.DoItems}");
    }

    private static TurnIntentHeadWeights LoadDeployedHead()
    {
        var path = Path.Combine(AppContext.BaseDirectory, TurnIntentHeldOutSettings.HeadRelativePath);
        if (!File.Exists(path))
        {
            path = Path.Combine(TurnIntentEmbeddingEnvironment.ResolveApiSourceDirectory(), TurnIntentHeldOutSettings.HeadRelativePath);
        }

        var head = new FileTurnIntentHeadProvider(path, NullLogger<FileTurnIntentHeadProvider>.Instance).Current;
        head.ShouldNotBeNull($"deployed head could not be loaded from {path}");
        return head;
    }

    private static TurnIntentHeldOutPrediction Predict(
        TurnIntentHeadWeights head, TurnIntentHeldOutItem item, float[] embedding, string uiLanguage)
    {
        var hand = TurnIntentHandFeatures.Extract(item.Text);
        var probabilities = TurnIntentHead.Probabilities(head, embedding, hand);
        var neutralHand = hand.Select((_, j) => head.Means[embedding.Length + j]).ToArray();
        var withoutHand = TurnIntentHead.Probabilities(head, embedding, neutralHand);
        var locale = TurnIntentLocaleGate.ResolveLocale(uiLanguage, head.Locales);

        return new TurnIntentHeldOutPrediction(
            item,
            head.Classes[Array.IndexOf(probabilities, probabilities.Max())],
            probabilities[(int)TurnIntentLabel.Ask],
            probabilities[(int)TurnIntentLabel.Do],
            probabilities[(int)TurnIntentLabel.Other],
            IsConfidentAsk(head, probabilities),
            IsConfidentAsk(head, withoutHand),
            locale?.Enabled == true,
            MutationIntentDetector.IsInformationQuestion(item.Text),
            MutationIntentDetector.IsMutationIntent(item.Text));
    }

    private static bool IsConfidentAsk(TurnIntentHeadWeights head, double[] probabilities)
    {
        var ask = probabilities[(int)TurnIntentLabel.Ask];
        return ask >= head.AskThreshold && ask >= probabilities.Max();
    }

    private static void AppendTable(
        StringBuilder report,
        string title,
        IEnumerable<(string Name, IReadOnlyCollection<TurnIntentHeldOutPrediction> Rows)> groups)
    {
        report.AppendLine($"=== {title} ===");
        report.AppendLine(string.Format(CultureInfo.InvariantCulture, RowFormat,
            "slice", "n", "Ask", "Do", "Oth", "cAsk", "prec", "precLB", "recall", "Do>cA", "Oth>cA", "acc", "noHandP", "detP", "detR"));
        foreach (var (name, rows) in groups)
        {
            var m = TurnIntentHeldOutMetrics.Compute(rows);
            report.AppendLine(string.Format(CultureInfo.InvariantCulture, RowFormat,
                name, m.Count, m.AskItems, m.DoItems, m.OtherItems, m.ConfidentAskPredicted,
                Pct(m.Precision), Pct(m.PrecisionLowerBound), Pct(m.Recall), Pct(m.DoToConfidentAskRate),
                Pct(m.OtherToConfidentAskRate), Pct(m.Accuracy), Pct(m.NoHandPrecision), Pct(m.DetectorPrecision),
                Pct(m.DetectorRecall)));
        }

        report.AppendLine();
    }

    private static void AppendQuestionMarkPairs(StringBuilder report, IReadOnlyList<TurnIntentHeldOutPrediction> predictions)
    {
        var pairs = predictions.Where(p => p.Item.PairKey != null)
            .GroupBy(p => p.Item.Locale + "|" + p.Item.PairKey)
            .Where(g => g.Count() == 2 && g.Select(p => p.Item.Label).Distinct().Count() == 2)
            .ToList();
        var bothRight = pairs.Count(g => g.All(p => p.Predicted == p.Item.Label));
        var askFlipsWithMark = pairs.Count(g => g.Single(p => p.Item.Label == TurnIntentProbeSettings.LabelAsk).ConfidentAsk
                                                && !g.Single(p => p.Item.Label != TurnIntentProbeSettings.LabelAsk).ConfidentAsk);
        report.AppendLine($"=== Question-mark minimal pairs (labels differ) === pairs {pairs.Count}, both argmax right {bothRight}, only the Ask member confident {askFlipsWithMark}");
        report.AppendLine();
    }

    private static void AppendForeignUiLanguage(
        StringBuilder report, TurnIntentHeadWeights head, IReadOnlyList<TurnIntentHeldOutItem> items, float[][] embeddings)
    {
        var disabledLocales = head.Locales.Where(l => !l.Enabled).Select(l => l.Locale).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreign = items.Select((item, i) => (item, i))
            .Where(x => x.item.Set == TurnIntentHeldOutSettings.SetMain && disabledLocales.Contains(x.item.Locale))
            .Select(x => Predict(head, x.item, embeddings[x.i], TurnIntentHeldOutSettings.ForeignUiLanguage))
            .ToList();
        var acting = foreign.Where(p => p.LocaleEnabled && p.ConfidentAsk).ToList();
        var m = TurnIntentHeldOutMetrics.Compute(foreign);
        report.AppendLine(
            $"=== Disabled-locale messages with UI language '{TurnIntentHeldOutSettings.ForeignUiLanguage}' === " +
            $"{foreign.Count} messages, {acting.Count} would act (confident Ask, gate open); precision {Pct(m.Precision)} " +
            $"(LB {Pct(m.PrecisionLowerBound)}), Do>cAsk {Pct(m.DoToConfidentAskRate)}");
        report.AppendLine();
    }

    private static async Task AppendLatencyAsync(StringBuilder report, IReadOnlyList<TurnIntentHeldOutItem> items)
    {
        var sample = items.Where((_, i) => i % Math.Max(1, items.Count / TurnIntentHeldOutSettings.LatencySampleSize) == 0)
            .Take(TurnIntentHeldOutSettings.LatencySampleSize)
            .Select(item => item.Text)
            .ToList();
        var timings = await TurnIntentEmbeddingEnvironment.MeasureLatencyMillisecondsAsync(sample);
        var warm = timings.Skip(1).OrderBy(t => t).ToArray();
        var overTimeout = warm.Count(t => t > TurnIntentDefaults.DefaultTimeoutMilliseconds);
        report.AppendLine(
            $"=== Embedding latency (single thread, this machine; not production) === cold first call {timings[0]:F0} ms; " +
            $"warm n={warm.Length} p50 {Percentile(warm, TurnIntentHeldOutSettings.LatencyPercentileMedian):F0} ms, " +
            $"p95 {Percentile(warm, TurnIntentHeldOutSettings.LatencyPercentileTail):F0} ms, max {warm.LastOrDefault():F0} ms, " +
            $"over {TurnIntentDefaults.DefaultTimeoutMilliseconds} ms: {overTimeout}");
        report.AppendLine();
    }

    private static double Percentile(double[] sorted, double quantile) =>
        sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(quantile * sorted.Length))];

    private static void WriteResults(IReadOnlyList<TurnIntentHeldOutPrediction> predictions)
    {
        var path = Environment.GetEnvironmentVariable(TurnIntentHeldOutSettings.ResultEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var rows = predictions.Select(p => new
        {
            p.Item.Id,
            p.Item.Locale,
            p.Item.Set,
            p.Item.Category,
            p.Item.Text,
            p.Item.Label,
            p.Predicted,
            p.AskProbability,
            p.DoProbability,
            p.OtherProbability,
            p.ConfidentAsk,
            p.ConfidentAskWithoutHandFeatures,
            p.LocaleEnabled,
            p.DetectorQuestion,
            p.DetectorMutation,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(rows));
    }

    private static string Pct(double value) => TurnIntentProbeReportFormatter.Pct(value);
}
