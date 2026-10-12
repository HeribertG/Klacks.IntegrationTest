// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Measures the single-query e5 embedding latency the turn-intent classifier pays, with the production embedding
/// profile (ORT intra-op spinning at its default) against the same profile with spinning switched off - the setting
/// that cut the reranker's p50 from 6168 to 1140 ms on win-arm64. Rounds alternate between the two profiles so
/// background load hits both alike; every round uses a fresh provider and drops its first (session-building) call.
/// Also checks that the vectors are identical. Reporting only. Gated by KLACKS_TURN_INTENT_HELDOUT=1 and needs the
/// held-out corpus and KnowledgeIndex__ModelsRoot like the held-out evaluation.
/// </summary>

using System.Text;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Category("SlowModelLoad")]
public class TurnIntentEmbeddingSpinningLatencyTests
{
    private const int Rounds = 3;
    private const string SpinningOff = "0";
    private const float IdenticalTolerance = 1e-6f;

    [OneTimeSetUp]
    public void GateOnEnvironmentVariable()
    {
        if (Environment.GetEnvironmentVariable(TurnIntentHeldOutSettings.EnableEnvironmentVariable)
            != TurnIntentProbeSettings.EnableValue)
        {
            Assert.Ignore($"Set {TurnIntentHeldOutSettings.EnableEnvironmentVariable}={TurnIntentProbeSettings.EnableValue} to run the latency measurement.");
        }
    }

    [Test]
    public async Task EmbeddingLatency_WithAndWithoutIntraOpSpinning()
    {
        var corpusPath = TurnIntentHeldOutCorpus.DefaultPath;
        if (!File.Exists(corpusPath))
        {
            Assert.Inconclusive($"Held-out corpus not found: {corpusPath}");
        }

        var items = TurnIntentHeldOutCorpus.LoadAgreed(corpusPath);
        var step = Math.Max(1, items.Count / TurnIntentHeldOutSettings.LatencySampleSize);
        var sample = items.Where((_, i) => i % step == 0).Take(TurnIntentHeldOutSettings.LatencySampleSize)
            .Select(item => item.Text).ToList();

        var spinningOff = OnnxEmbeddingRuntimeProfile.Default with
        {
            CreateSessionOptions = () =>
            {
                var options = OnnxSessionOptionsFactory.CreateEmbedding();
                options.AddSessionConfigEntry(OnnxRuntimeConfigKeys.AllowIntraOpSpinning, SpinningOff);
                return options;
            },
        };

        var defaultTimings = new List<double>();
        var offTimings = new List<double>();
        var defaultVectors = new List<float[]>();
        var offVectors = new List<float[]>();
        for (var round = 0; round < Rounds; round++)
        {
            defaultTimings.AddRange((await TurnIntentEmbeddingEnvironment.MeasureLatencyMillisecondsAsync(
                sample, null, round == 0 ? defaultVectors : null)).Skip(1));
            offTimings.AddRange((await TurnIntentEmbeddingEnvironment.MeasureLatencyMillisecondsAsync(
                sample, spinningOff, round == 0 ? offVectors : null)).Skip(1));
        }

        var maxDifference = defaultVectors.Zip(offVectors)
            .Max(pair => pair.First.Zip(pair.Second).Max(values => Math.Abs(values.First - values.Second)));

        var report = new StringBuilder();
        report.AppendLine($"Cores {Environment.ProcessorCount}, {Rounds} rounds x {sample.Count - 1} warm calls per profile");
        report.AppendLine(Describe("spinning default", defaultTimings));
        report.AppendLine(Describe("spinning off", offTimings));
        report.AppendLine($"max |vector difference| {maxDifference:E2}");
        TestContext.Out.WriteLine(report.ToString());

        maxDifference.ShouldBeLessThan(IdenticalTolerance);
    }

    private static string Describe(string name, List<double> timings)
    {
        var sorted = timings.OrderBy(t => t).ToArray();
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * sorted.Length))];
        return $"{name,-18} p50 {At(TurnIntentHeldOutSettings.LatencyPercentileMedian):F0} ms, " +
               $"p95 {At(TurnIntentHeldOutSettings.LatencyPercentileTail):F0} ms, max {sorted[^1]:F0} ms, " +
               $"over 500 ms: {sorted.Count(t => t > 500)}/{sorted.Length}";
    }
}
