// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Per-column z-score fitted on the training rows only (constant columns map to zero), followed by a per-column
/// multiplier that lets a feature group weigh more or less under the L2 penalty.
/// </summary>
/// <param name="means">Column means of the training rows</param>
/// <param name="scales">Per-column factor: 1 / standard deviation times the group multiplier, 0 for constant columns</param>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public sealed class FeatureStandardizer
{
    private const double ConstantColumnTolerance = 1e-12;

    private readonly double[] _means;
    private readonly double[] _scales;

    private FeatureStandardizer(double[] means, double[] scales)
    {
        _means = means;
        _scales = scales;
    }

    public static FeatureStandardizer Fit(IReadOnlyList<double[]> rows, double[] columnMultipliers)
    {
        var dimension = columnMultipliers.Length;
        var means = new double[dimension];
        var variances = new double[dimension];

        foreach (var row in rows)
        {
            for (var j = 0; j < dimension; j++)
            {
                means[j] += row[j];
            }
        }

        for (var j = 0; j < dimension; j++)
        {
            means[j] /= rows.Count;
        }

        foreach (var row in rows)
        {
            for (var j = 0; j < dimension; j++)
            {
                var delta = row[j] - means[j];
                variances[j] += delta * delta;
            }
        }

        var scales = new double[dimension];
        for (var j = 0; j < dimension; j++)
        {
            var deviation = Math.Sqrt(variances[j] / rows.Count);
            scales[j] = deviation < ConstantColumnTolerance ? 0.0 : columnMultipliers[j] / deviation;
        }

        return new FeatureStandardizer(means, scales);
    }

    public IReadOnlyList<double> Means => _means;

    public IReadOnlyList<double> Scales => _scales;

    public double[] Transform(double[] row)
    {
        var result = new double[row.Length];
        for (var j = 0; j < row.Length; j++)
        {
            result[j] = (row[j] - _means[j]) * _scales[j];
        }

        return result;
    }
}
