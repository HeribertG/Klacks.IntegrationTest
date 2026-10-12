// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Trained multinomial logistic regression head.
/// </summary>
/// <param name="Weights">Class-by-feature weight matrix</param>
/// <param name="Bias">Per-class bias</param>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public sealed record SoftmaxModel(double[][] Weights, double[] Bias)
{
    public double[] Logits(double[] features)
    {
        var logits = new double[Weights.Length];
        for (var c = 0; c < Weights.Length; c++)
        {
            var sum = Bias[c];
            var weights = Weights[c];
            for (var j = 0; j < features.Length; j++)
            {
                sum += weights[j] * features[j];
            }

            logits[c] = sum;
        }

        return logits;
    }
}
