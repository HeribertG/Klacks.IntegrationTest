// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Shared gate for the live turn-eval goldsets (selection, names, honesty). Resolves the run
/// parameters from environment variables and decides the pass-rate threshold a run has to clear.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Interfaces.Assistant;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Assistant;

internal static class TurnEvalPassRateGate
{
    public const string GoldsetEnvironmentVariable = "TURNEVAL_GOLDSET";
    public const string ModelIdEnvironmentVariable = "TURNEVAL_MODEL_ID";
    public const string MaxItemsEnvironmentVariable = "TURNEVAL_MAX_ITEMS";
    public const string MinPassRateEnvironmentVariable = "TURNEVAL_MIN_PASS_RATE";

    public const string DefaultModelId = "deepseek-v4-pro";

    /// <summary>Pass-rate drop tolerated against the best comparable earlier run.</summary>
    public const double BaselineTolerance = 0.05;

    /// <summary>
    /// Absolute floor that applies even without a comparable baseline, so a run can never be
    /// silently green just because nothing comparable was ever measured. Deliberately low: it is
    /// meant to catch a collapse (a run in which almost nothing passes, e.g. a broken toolset or a
    /// dead provider), not to encode a quality target - the ratchet against the best comparable run
    /// does the fine-grained work. Measured full runs so far: 42/64 active items on the curated
    /// 70-item set (30.08.), 74/334 on the built-out set (31.08.), 0/60 on a broken crud run
    /// (31.08. 22:31) - only the last one is below this floor, which is exactly the intent.
    /// Override per run with TURNEVAL_MIN_PASS_RATE.
    /// </summary>
    public const double AbsoluteMinPassRate = 0.10;

    public static string ResolveModelId() =>
        Environment.GetEnvironmentVariable(ModelIdEnvironmentVariable) ?? DefaultModelId;

    public static int? ResolveMaxItems() =>
        int.TryParse(Environment.GetEnvironmentVariable(MaxItemsEnvironmentVariable), out var parsed) && parsed > 0
            ? parsed
            : null;

    public static string ResolveGoldset(string defaultGoldset)
    {
        var configured = Environment.GetEnvironmentVariable(GoldsetEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured) ? defaultGoldset : configured.Trim();
    }

    public static double? ReadForcedMinPassRate()
    {
        var raw = Environment.GetEnvironmentVariable(MinPassRateEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0.0 || parsed > 1.0)
        {
            throw new InvalidOperationException(
                $"{MinPassRateEnvironmentVariable} must be a number between 0.0 and 1.0, got '{raw}'.");
        }

        return parsed;
    }

    /// <summary>
    /// Number of items the upcoming run will cover, and whether that makes it a partial run.
    /// </summary>
    public static (int ItemsTotal, bool IsPartial) ResolveScope(int goldsetItemCount, int? maxItems)
    {
        var itemsTotal = maxItems.HasValue ? Math.Min(maxItems.Value, goldsetItemCount) : goldsetItemCount;
        return (itemsTotal, itemsTotal != goldsetItemCount);
    }

    /// <summary>
    /// Threshold the run must clear. Read BEFORE the run so the freshly persisted EvalRun can never
    /// become its own baseline. A forced environment value wins; otherwise the best comparable
    /// completed run sets a ratchet, and the absolute floor always applies underneath it. A missing
    /// baseline is logged, never silently skipped.
    /// </summary>
    public static async Task<double> ResolveThresholdAsync(
        IEvalRunRepository evalRunRepository,
        string goldset,
        string modelId,
        int itemsTotal,
        bool isPartial)
    {
        var forced = ReadForcedMinPassRate();
        if (forced.HasValue)
        {
            TestContext.WriteLine($"Gate: forced min pass rate {forced.Value:P1} ({MinPassRateEnvironmentVariable}).");
            return forced.Value;
        }

        var baseline = isPartial
            ? null
            : await evalRunRepository.GetBestBaselineAsync(
                goldset, modelId, itemsTotal, TurnEvalScorer.ScorerVersion);

        if (baseline == null || baseline.ItemsTotal <= 0)
        {
            TestContext.WriteLine(
                $"Gate: NO comparable baseline (goldset '{goldset}', model '{modelId}', {itemsTotal} items, " +
                $"scorerVersion {TurnEvalScorer.ScorerVersion}, partial={isPartial}) - falling back to the absolute " +
                $"floor {AbsoluteMinPassRate:P1}. Override with {MinPassRateEnvironmentVariable}.");
            return AbsoluteMinPassRate;
        }

        var baselinePassRate = (double)baseline.ItemsPassed / baseline.ItemsTotal;
        var threshold = Math.Max(baselinePassRate - BaselineTolerance, AbsoluteMinPassRate);
        TestContext.WriteLine(
            $"Gate: baseline {baselinePassRate:P1} (best {goldset}/{modelId} run over {baseline.ItemsTotal} items, " +
            $"composite {baseline.CompositeScore:F4}, {baseline.CreateTime:u}) -> min pass rate {threshold:P1}.");
        return threshold;
    }

    public static double ComputePassRate(TurnEvalDimensions dimensions)
    {
        var activeItems = Math.Max(1, dimensions.ItemsTotal - dimensions.ItemsExcluded);
        return (double)dimensions.ItemsPassed / activeItems;
    }
}
