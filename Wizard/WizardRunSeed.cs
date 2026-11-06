// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// GA random seed for the wizard-coverage harness. Defaults to 42 (the historical fixed seed). The
/// environment variable WIZARD_SEED overrides it, and is meant for local measurement series ONLY
/// (measuring the seed spread of one scenario without code changes). It must never be set in CI or
/// on the build runner: several harness tests assert thresholds that were calibrated on seed 42 and
/// may turn red under another seed. The E2E, tuning and apply-compliance-seam tests deliberately
/// keep a literal 42 and are not affected by the variable.
/// </summary>
public static class WizardRunSeed
{
    public const string EnvironmentVariable = "WIZARD_SEED";
    public const int DefaultSeed = 42;

    public static int Resolve()
        => int.TryParse(Environment.GetEnvironmentVariable(EnvironmentVariable), out var seed) ? seed : DefaultSeed;
}
