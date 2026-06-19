namespace Klacks.IntegrationTest.Wizard.Spec;

/// <summary>
/// Optional shared scheduling rule. Only positive values are honoured by the wizard
/// (a value of 0 is treated as "unset" and replaced by WizardSchedulingDefaults); leave a
/// field null to fall back to the contract / global defaults.
/// </summary>
/// <param name="MaxConsecutiveDays">Max run of distinct work days.</param>
/// <param name="MinPauseHours">Minimum rest gap between consecutive shifts (hours).</param>
/// <param name="MaxDailyHours">Max summed hours per day.</param>
/// <param name="MaxWeeklyHours">Max summed hours per week.</param>
/// <param name="MaxWorkDays">Soft max block length in days.</param>
/// <param name="MinRestDays">Required free days between work blocks.</param>
public sealed record SchedulingRuleSpec(
    int? MaxConsecutiveDays = null,
    decimal? MinPauseHours = null,
    decimal? MaxDailyHours = null,
    decimal? MaxWeeklyHours = null,
    int? MaxWorkDays = null,
    int? MinRestDays = null);
