// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Multinomial logistic regression with an L2 penalty on the weights (not the bias), trained by full-batch Nesterov
/// accelerated gradient descent. Fully deterministic: zero initialisation, no shuffling, and a step size taken from the
/// Lipschitz bound 0.5 * (lambdaMax(X'X / n) + 1) + lambda, with lambdaMax estimated by power iteration from a fixed
/// start vector. The loss is the mean cross-entropy plus lambda / 2 * |W|^2.
/// </summary>
/// <param name="features">Standardised training rows</param>
/// <param name="labels">Class index per row</param>
/// <param name="classCount">Number of classes</param>
/// <param name="lambda">L2 strength</param>
/// <param name="iterations">Number of gradient steps</param>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class SoftmaxRegressionTrainer
{
    private const double SoftmaxHessianBound = 0.5;
    private const double MinimumNorm = 1e-12;

    public static SoftmaxModel Train(
        IReadOnlyList<double[]> features, int[] labels, int classCount, double lambda, int iterations)
    {
        var n = features.Count;
        var d = features[0].Length;
        var step = 1.0 / (SoftmaxHessianBound * (TopEigenvalue(features, d) + 1.0) + lambda);

        var weights = NewMatrix(classCount, d);
        var bias = new double[classCount];
        var lookWeights = NewMatrix(classCount, d);
        var lookBias = new double[classCount];
        var gradWeights = NewMatrix(classCount, d);
        var gradBias = new double[classCount];
        var probabilities = new double[classCount];

        for (var t = 1; t <= iterations; t++)
        {
            Clear(gradWeights, gradBias);

            for (var i = 0; i < n; i++)
            {
                var row = features[i];
                for (var c = 0; c < classCount; c++)
                {
                    probabilities[c] = Dot(lookWeights[c], row) + lookBias[c];
                }

                SoftmaxInPlace(probabilities);
                probabilities[labels[i]] -= 1.0;

                for (var c = 0; c < classCount; c++)
                {
                    var diff = probabilities[c];
                    var grad = gradWeights[c];
                    for (var j = 0; j < d; j++)
                    {
                        grad[j] += diff * row[j];
                    }

                    gradBias[c] += diff;
                }
            }

            var momentum = (t - 1.0) / (t + 2.0);
            for (var c = 0; c < classCount; c++)
            {
                for (var j = 0; j < d; j++)
                {
                    var gradient = gradWeights[c][j] / n + lambda * lookWeights[c][j];
                    var updated = lookWeights[c][j] - step * gradient;
                    lookWeights[c][j] = updated + momentum * (updated - weights[c][j]);
                    weights[c][j] = updated;
                }

                var biasUpdated = lookBias[c] - step * (gradBias[c] / n);
                lookBias[c] = biasUpdated + momentum * (biasUpdated - bias[c]);
                bias[c] = biasUpdated;
            }
        }

        return new SoftmaxModel(weights, bias);
    }

    public static void SoftmaxInPlace(double[] values)
    {
        var max = values.Max();
        var sum = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Math.Exp(values[i] - max);
            sum += values[i];
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= sum;
        }
    }

    private static double TopEigenvalue(IReadOnlyList<double[]> features, int d)
    {
        var vector = new double[d];
        Array.Fill(vector, 1.0 / Math.Sqrt(d));
        var eigenvalue = 0.0;

        for (var iteration = 0; iteration < TurnIntentProbeSettings.PowerIterations; iteration++)
        {
            var next = new double[d];
            foreach (var row in features)
            {
                var projection = Dot(row, vector);
                for (var j = 0; j < d; j++)
                {
                    next[j] += projection * row[j];
                }
            }

            var norm = 0.0;
            for (var j = 0; j < d; j++)
            {
                next[j] /= features.Count;
                norm += next[j] * next[j];
            }

            norm = Math.Sqrt(norm);
            if (norm < MinimumNorm)
            {
                return 0.0;
            }

            for (var j = 0; j < d; j++)
            {
                vector[j] = next[j] / norm;
            }

            eigenvalue = norm;
        }

        return eigenvalue;
    }

    private static double Dot(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var j = 0; j < a.Length; j++)
        {
            sum += a[j] * b[j];
        }

        return sum;
    }

    private static double[][] NewMatrix(int rows, int columns)
    {
        var matrix = new double[rows][];
        for (var r = 0; r < rows; r++)
        {
            matrix[r] = new double[columns];
        }

        return matrix;
    }

    private static void Clear(double[][] matrix, double[] vector)
    {
        foreach (var row in matrix)
        {
            Array.Clear(row);
        }

        Array.Clear(vector);
    }
}
