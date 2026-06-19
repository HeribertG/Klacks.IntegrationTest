namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// JSON-serializable root result of a wizard-coverage run. Pure data; the analyzer produces it
/// and the writer serializes it. runTimestamp is supplied by the caller (never read from the
/// clock inside the pure analyzer).
/// </summary>
public sealed record WizardAnalysisResult(
    int SchemaVersion,
    string RunTimestamp,
    string ScenarioName,
    WizardScale Scale,
    WizardEffectiveConfig EffectiveConfig,
    WizardMetrics Metrics,
    IReadOnlyList<PerClientResult> PerClient,
    IReadOnlyList<UncoveredSlot> Uncovered);

/// <summary>Scenario scale dimensions.</summary>
public sealed record WizardScale(
    int Clients,
    int Shifts,
    int Slots,
    int PeriodDays);

/// <summary>The GA config that was actually applied.</summary>
public sealed record WizardEffectiveConfig(
    int PopulationSize,
    int MaxGenerations,
    int RandomSeed);

/// <summary>
/// All asserted + report-only metrics for the run. violationsByKind maps each ViolationKind name
/// to its count.
/// </summary>
public sealed record WizardMetrics(
    int Stage0,
    double CoveragePercent,
    double ShiftCoverageRatio,
    int UndersupplyCount,
    int OverstaffingCount,
    double TheoreticalMaxCoverage,
    double ShiftTypeEntropyAvg,
    double SlotGini,
    double RosterFidelityInversionRate,
    double TargetReachedPercent,
    int QualificationViolations,
    int CommandViolations,
    IReadOnlyDictionary<string, int> ViolationsByKind,
    long DurationMs);

/// <summary>Per-client outcome row.</summary>
public sealed record PerClientResult(
    int ClientIndex,
    string ClientId,
    int Slots,
    double Hours,
    double Target,
    double Deviation,
    int[] ShiftTypeMix);

/// <summary>A single uncovered (date, shift) slot remainder.</summary>
public sealed record UncoveredSlot(
    string Date,
    string ShiftRefId);
