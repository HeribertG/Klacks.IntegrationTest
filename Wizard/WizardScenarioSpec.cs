using Klacks.IntegrationTest.Wizard.Spec;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Immutable, declarative description of a wizard-coverage scenario. Every optional axis
/// (qualifications, schedule commands, locked works, breaks, scheduling rule) carries a safe
/// empty/default so the first test only sets the handful of fields it cares about. The builder
/// turns this into seeded DB rows; the analyzer never reads it structurally.
/// </summary>
/// <param name="ScenarioName">Used for the JSON artifact filename and the result record.</param>
/// <param name="ClientCount">Number of Client+Contract+active-ClientContract triples to seed.</param>
/// <param name="PeriodFrom">Planning window start (inclusive).</param>
/// <param name="PeriodUntil">Planning window end (inclusive).</param>
/// <param name="GuaranteedHoursPerClient">Per-client Contract.GuaranteedHours (index -> hours).</param>
/// <param name="MaximumHoursPerClient">Per-client Contract.MaximumHours (index -> hours); 0 = no cap.</param>
/// <param name="FullTimeHours">Per-client Contract.FullTime hours.</param>
/// <param name="ShiftDefs">The shift pool (FD/SD/ND etc.).</param>
/// <param name="ContractWorkDays">Contract.WorkOnMonday..Sunday flags (index 0=Mon..6=Sun).</param>
/// <param name="PerformsShiftWork">Contract.PerformsShiftWork (true = full FD/SD/ND rotation).</param>
/// <param name="PerformsShiftWorkPerClient">Optional per-client override (index -> bool) for Contract.PerformsShiftWork; null falls back to the flat <see cref="PerformsShiftWork"/>. Lets a scenario mix full shift-workers with day-only (FD-only) part-timers.</param>
/// <param name="SchedulingRule">Optional shared scheduling rule linked via Contract.SchedulingRuleId.</param>
/// <param name="Qualifications">Optional qualification axis (empty for the first test).</param>
/// <param name="ScheduleCommands">Optional per-(client,date) command axis (empty for the first test).</param>
/// <param name="LockedWorks">Optional locked-work axis (empty for the first test).</param>
/// <param name="Breaks">Optional break/absence axis (empty for the first test).</param>
public sealed record WizardScenarioSpec(
    string ScenarioName,
    int ClientCount,
    DateOnly PeriodFrom,
    DateOnly PeriodUntil,
    Func<int, decimal> GuaranteedHoursPerClient,
    IReadOnlyList<ShiftDef> ShiftDefs,
    bool[] ContractWorkDays,
    Func<int, decimal>? MaximumHoursPerClient = null,
    decimal FullTimeHours = 40m,
    bool PerformsShiftWork = true,
    Func<int, bool>? PerformsShiftWorkPerClient = null,
    SchedulingRuleSpec? SchedulingRule = null,
    IReadOnlyList<QualificationSpec>? Qualifications = null,
    IReadOnlyList<ScheduleCommandSpec>? ScheduleCommands = null,
    IReadOnlyList<LockedWorkSpec>? LockedWorks = null,
    IReadOnlyList<BreakSpec>? Breaks = null)
{
    public Func<int, decimal> MaximumHoursOrZero => MaximumHoursPerClient ?? (_ => 0m);

    public IReadOnlyList<QualificationSpec> QualificationsOrEmpty => Qualifications ?? [];

    public IReadOnlyList<ScheduleCommandSpec> ScheduleCommandsOrEmpty => ScheduleCommands ?? [];

    public IReadOnlyList<LockedWorkSpec> LockedWorksOrEmpty => LockedWorks ?? [];

    public IReadOnlyList<BreakSpec> BreaksOrEmpty => Breaks ?? [];

    /// <summary>
    /// Builds the FIRST coverage test spec: 5 clients, FD/SD/ND tiling 24h on ALL seven weekdays
    /// (true 7-day, USER-CONFIRMED), GuaranteedHours=180, no cap, one full calendar month.
    /// </summary>
    public static WizardScenarioSpec FiveClientsThreeShiftsSevenDay()
    {
        var allDays = new[] { true, true, true, true, true, true, true };

        var shifts = new List<ShiftDef>
        {
            new("FD", new TimeOnly(7, 0), new TimeOnly(15, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new("SD", new TimeOnly(15, 0), new TimeOnly(23, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new("ND", new TimeOnly(23, 0), new TimeOnly(7, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: true),
        };

        return new WizardScenarioSpec(
            ScenarioName: "FiveClients_ThreeShifts_SevenDay",
            ClientCount: 5,
            PeriodFrom: new DateOnly(2099, 1, 1),
            PeriodUntil: new DateOnly(2099, 1, 31),
            GuaranteedHoursPerClient: _ => 180m,
            ShiftDefs: shifts,
            ContractWorkDays: allDays,
            MaximumHoursPerClient: _ => 0m,
            FullTimeHours: 40m,
            PerformsShiftWork: true);
    }
}
