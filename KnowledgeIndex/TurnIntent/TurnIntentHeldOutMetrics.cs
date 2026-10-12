// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Figures of the decision the runtime acts on (a confident Ask) for one slice of held-out predictions: precision
/// with its Wilson lower bound, recall over the Ask items, the rate at which Do and Other items are mistaken for a
/// confident Ask, argmax accuracy, the same figures with the hand features neutralised, and the detector baseline
/// (question lead without mutation intent) on the same items.
/// </summary>
/// <param name="predictions">Predictions of one slice (locale, category or pool)</param>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public sealed record TurnIntentHeldOutMetrics(
    int Count,
    int AskItems,
    int DoItems,
    int OtherItems,
    int ConfidentAskPredicted,
    int ConfidentAskCorrect,
    int DoAsConfidentAsk,
    int OtherAsConfidentAsk,
    int ArgmaxCorrect,
    int NoHandPredicted,
    int NoHandCorrect,
    int DetectorAskPredicted,
    int DetectorAskCorrect)
{
    public double Precision => Ratio(ConfidentAskCorrect, ConfidentAskPredicted);

    public double PrecisionLowerBound => TurnIntentLocaleGate.WilsonLowerBound(ConfidentAskCorrect, ConfidentAskPredicted);

    public double Recall => Ratio(ConfidentAskCorrect, AskItems);

    public double DoToConfidentAskRate => Ratio(DoAsConfidentAsk, DoItems);

    public double OtherToConfidentAskRate => Ratio(OtherAsConfidentAsk, OtherItems);

    public double Accuracy => Ratio(ArgmaxCorrect, Count);

    public double NoHandPrecision => Ratio(NoHandCorrect, NoHandPredicted);

    public double NoHandRecall => Ratio(NoHandCorrect, AskItems);

    public double DetectorPrecision => Ratio(DetectorAskCorrect, DetectorAskPredicted);

    public double DetectorRecall => Ratio(DetectorAskCorrect, AskItems);

    public static TurnIntentHeldOutMetrics Compute(IReadOnlyCollection<TurnIntentHeldOutPrediction> predictions)
    {
        static bool IsAsk(TurnIntentHeldOutPrediction p) => p.Item.Label == TurnIntentProbeSettings.LabelAsk;
        static bool DetectorAsk(TurnIntentHeldOutPrediction p) => p.DetectorQuestion && !p.DetectorMutation;

        return new TurnIntentHeldOutMetrics(
            predictions.Count,
            predictions.Count(IsAsk),
            predictions.Count(p => p.Item.Label == TurnIntentProbeSettings.LabelDo),
            predictions.Count(p => p.Item.Label == TurnIntentProbeSettings.LabelOther),
            predictions.Count(p => p.ConfidentAsk),
            predictions.Count(p => p.ConfidentAsk && IsAsk(p)),
            predictions.Count(p => p.ConfidentAsk && p.Item.Label == TurnIntentProbeSettings.LabelDo),
            predictions.Count(p => p.ConfidentAsk && p.Item.Label == TurnIntentProbeSettings.LabelOther),
            predictions.Count(p => p.Predicted == p.Item.Label),
            predictions.Count(p => p.ConfidentAskWithoutHandFeatures),
            predictions.Count(p => p.ConfidentAskWithoutHandFeatures && IsAsk(p)),
            predictions.Count(DetectorAsk),
            predictions.Count(p => DetectorAsk(p) && IsAsk(p)));
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? double.NaN : (double)numerator / denominator;
}
