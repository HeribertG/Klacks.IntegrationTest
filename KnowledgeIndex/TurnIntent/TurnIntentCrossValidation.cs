// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Grouped k-fold cross-validation of the softmax head: per fold the standardiser is fitted on the training rows only,
/// the head is trained on them and the held-out fold is scored. Returns the out-of-fold logits of every item.
/// </summary>
/// <param name="rawFeatures">Unstandardised feature row per item</param>
/// <param name="labels">Class index per item</param>
/// <param name="folds">Fold index per item from TurnIntentGroupedFolds</param>
/// <param name="columnMultipliers">Per-column weight applied after standardisation</param>
/// <param name="lambda">L2 strength of the head</param>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentCrossValidation
{
    public static double[][] OutOfFoldLogits(
        IReadOnlyList<double[]> rawFeatures, int[] labels, int[] folds, double[] columnMultipliers, double lambda)
    {
        var logits = new double[rawFeatures.Count][];
        var classCount = TurnIntentProbeSettings.Classes.Length;

        for (var fold = 0; fold < TurnIntentProbeSettings.FoldCount; fold++)
        {
            var trainIndices = Enumerable.Range(0, rawFeatures.Count).Where(i => folds[i] != fold).ToList();
            var testIndices = Enumerable.Range(0, rawFeatures.Count).Where(i => folds[i] == fold).ToList();

            var standardizer = FeatureStandardizer.Fit(trainIndices.Select(i => rawFeatures[i]).ToList(), columnMultipliers);
            var trainFeatures = trainIndices.Select(i => standardizer.Transform(rawFeatures[i])).ToList();
            var trainLabels = trainIndices.Select(i => labels[i]).ToArray();

            var model = SoftmaxRegressionTrainer.Train(
                trainFeatures, trainLabels, classCount, lambda, TurnIntentProbeSettings.Iterations);

            foreach (var index in testIndices)
            {
                logits[index] = model.Logits(standardizer.Transform(rawFeatures[index]));
            }
        }

        return logits;
    }
}
