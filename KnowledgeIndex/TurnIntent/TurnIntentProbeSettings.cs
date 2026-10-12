// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Fixed, pre-registered settings of the turn-intent head feasibility probe. Hyper-parameters are constants chosen
/// BEFORE the first measurement; the lambda sweep is reported as a sweep and never used to pick a headline figure.
/// </summary>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentProbeSettings
{
    public const string EnableEnvironmentVariable = "KLACKS_TURN_INTENT_PROBE";
    public const string EnableValue = "1";
    public const string ModelsRootEnvironmentVariable = "KnowledgeIndex__ModelsRoot";
    public const string EmbeddingCacheEnvironmentVariable = "KLACKS_TURN_INTENT_PROBE_CACHE";
    public const string TrainEnvironmentVariable = "KLACKS_TURN_INTENT_TRAIN";

    public const string LabelAsk = "Ask";
    public const string LabelDo = "Do";
    public const string LabelOther = "Other";

    public static readonly string[] Classes = [LabelAsk, LabelDo, LabelOther];

    public static readonly string[] CoreLocales = ["de", "en", "fr", "it"];

    public const int FoldCount = 5;
    public const int FoldSeed = 20261010;

    public const int Iterations = 300;
    public const int PowerIterations = 60;
    public const double HeadlineLambda = 0.01;
    public static readonly double[] LambdaSweep = [0.001, 0.01, 0.1, 1.0];

    public const double HandFeatureScaleSensitivity = 3.0;

    public const double StopCriterionMinimalPairAccuracy = 0.85;
    public const double AcceptanceMinimalPairAccuracy = 0.90;
    public const double AcceptanceDoPrecision = 0.95;
    public const double AcceptanceAskPrecision = 0.90;
    public const double AcceptanceEce = 0.05;

    public const int EceBinCount = 10;
    public const double TemperatureMin = 0.25;
    public const double TemperatureMax = 5.0;
    public const double TemperatureStep = 0.05;

    public static readonly double[] ConfidenceThresholds = [0.5, 0.6, 0.7, 0.8, 0.9];

    public const string GoldsetRelativePath = "Application/Skills/Goldsets/turn-intent-v1.json";
    public const string PluginsRelativePath = "Plugins/Languages";
    public const string ApiProjectDirectory = "Klacks.Api";
    public const string ApiProjectFile = "Klacks.Api.csproj";
}
