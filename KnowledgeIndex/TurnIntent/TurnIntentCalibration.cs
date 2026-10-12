// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Temperature scaling and expected calibration error over out-of-fold logits. The temperature is fitted on the very
/// same out-of-fold logits it is then evaluated on, so the scaled ECE is an optimistic estimate.
/// </summary>
/// <param name="logits">Out-of-fold logits per item</param>
/// <param name="labels">Class index per item</param>
/// <param name="temperature">Divisor applied to the logits before the softmax</param>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentCalibration
{
    private const double ProbabilityFloor = 1e-12;

    public static double[] Probabilities(double[] logits, double temperature)
    {
        var scaled = logits.Select(value => value / temperature).ToArray();
        SoftmaxRegressionTrainer.SoftmaxInPlace(scaled);
        return scaled;
    }

    public static double FitTemperature(IReadOnlyList<double[]> logits, int[] labels)
    {
        var best = 1.0;
        var bestLoss = double.MaxValue;

        for (var t = TurnIntentProbeSettings.TemperatureMin;
             t <= TurnIntentProbeSettings.TemperatureMax + 1e-9;
             t += TurnIntentProbeSettings.TemperatureStep)
        {
            var loss = 0.0;
            for (var i = 0; i < logits.Count; i++)
            {
                loss -= Math.Log(Math.Max(Probabilities(logits[i], t)[labels[i]], ProbabilityFloor));
            }

            if (loss < bestLoss)
            {
                bestLoss = loss;
                best = t;
            }
        }

        return best;
    }

    public static double ExpectedCalibrationError(IReadOnlyList<double[]> logits, int[] labels, double temperature)
    {
        var binCount = TurnIntentProbeSettings.EceBinCount;
        var counts = new int[binCount];
        var correct = new double[binCount];
        var confidence = new double[binCount];

        for (var i = 0; i < logits.Count; i++)
        {
            var probabilities = Probabilities(logits[i], temperature);
            var predicted = Array.IndexOf(probabilities, probabilities.Max());
            var bin = Math.Min(binCount - 1, (int)(probabilities[predicted] * binCount));
            counts[bin]++;
            confidence[bin] += probabilities[predicted];
            correct[bin] += predicted == labels[i] ? 1.0 : 0.0;
        }

        var ece = 0.0;
        for (var b = 0; b < binCount; b++)
        {
            if (counts[b] > 0)
            {
                ece += Math.Abs(correct[b] - confidence[b]) / logits.Count;
            }
        }

        return ece;
    }
}
