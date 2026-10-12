// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public sealed record TurnIntentHeldOutPrediction(
    TurnIntentHeldOutItem Item,
    string Predicted,
    double AskProbability,
    double DoProbability,
    double OtherProbability,
    bool ConfidentAsk,
    bool ConfidentAskWithoutHandFeatures,
    bool LocaleEnabled,
    bool DetectorQuestion,
    bool DetectorMutation);
