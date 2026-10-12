// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Fixed settings of the held-out evaluation of the deployed turn-intent head. The assertion thresholds are
/// pre-registered here and pooled over the enabled locales, because a per-locale Wilson bound on a few dozen confident
/// Asks flips on a single error; the per-locale figures are reported only.
/// </summary>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentHeldOutSettings
{
    public const string EnableEnvironmentVariable = "KLACKS_TURN_INTENT_HELDOUT";
    public const string ResultEnvironmentVariable = "KLACKS_TURN_INTENT_HELDOUT_OUT";
    public const string CorpusEnvironmentVariable = "KLACKS_TURN_INTENT_HELDOUT_CORPUS";
    public const string CorpusRelativePath = "KnowledgeIndex/turn-intent-heldout-v1.json";
    public const string HeadRelativePath = "Application/Klacksy/turn-intent-head.v1.json";

    public const string SetMain = "main";
    public const string ForeignUiLanguage = "en";

    public const double MinimumPooledConfidentAskPrecisionLowerBound = 0.85;
    public const double MaximumPooledDoToConfidentAskRate = 0.05;

    public const int LatencySampleSize = 60;
    public const double LatencyPercentileMedian = 0.50;
    public const double LatencyPercentileTail = 0.95;
}
