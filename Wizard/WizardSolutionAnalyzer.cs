using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleOptimizer.TokenEvolution.Metrics;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// The exact-result analyzer every coverage test reuses. Computes all 7 metrics directly off the
/// solved (best, context) pair: client index, per-client hours vs target, coverage (+uncovered
/// list, replicating WizardBenchmarkService.ComputeSlotMetrics), FD/SD/ND mix, violations by kind,
/// command compliance and qualification compliance. Reuses WizardMetricsCalculator for the
/// report-only aggregate metrics. Pure function — no clock, no I/O.
/// </summary>
public static class WizardSolutionAnalyzer
{
    private const int SchemaVersion = 1;
    private const int ShiftTypeCount = 3;

    public static WizardAnalysisResult Analyze(
        CoreScenario best,
        CoreWizardContext context,
        WizardScenarioSpec spec,
        long durationMs,
        string runTimestamp)
    {
        var nonLocked = best.Tokens.Where(t => !t.IsLocked).ToList();

        // --- Coverage (replicates WizardBenchmarkService.ComputeSlotMetrics exactly) ---
        var availableSlots = context.Shifts.Count;
        var capacityPerSlot = context.Shifts
            .GroupBy(s => (s.Id, s.Date))
            .ToDictionary(g => g.Key, g => g.Count());
        var tokensPerSlot = nonLocked
            .GroupBy(t => (t.ShiftRefId.ToString(), t.Date.ToString("yyyy-MM-dd")))
            .ToDictionary(g => g.Key, g => g.Count());

        var covered = 0;
        var overstaffing = 0;
        var undersupply = 0;
        var uncovered = new List<UncoveredSlot>();
        foreach (var slot in capacityPerSlot)
        {
            var assigned = tokensPerSlot.GetValueOrDefault(slot.Key, 0);
            covered += Math.Min(slot.Value, assigned);
            if (assigned > slot.Value)
            {
                overstaffing += assigned - slot.Value;
            }
            else
            {
                var missing = slot.Value - assigned;
                undersupply += missing;
                for (var i = 0; i < missing; i++)
                {
                    uncovered.Add(new UncoveredSlot(Date: slot.Key.Item2, ShiftRefId: slot.Key.Item1));
                }
            }
        }
        foreach (var orphan in tokensPerSlot)
        {
            if (!capacityPerSlot.ContainsKey(orphan.Key))
            {
                overstaffing += orphan.Value;
            }
        }

        var coveragePercent = availableSlots == 0 ? 0d : (double)covered / availableSlots;
        var shiftCoverageRatio = coveragePercent;
        var theoreticalMaxCoverage = ComputeTheoreticalMaxCoverage(context);

        // --- Aggregate report-only metrics (reuse the production calculator) ---
        var snapshot = WizardMetricsCalculator.Compute(best, context, 0);

        // --- Violations by kind ---
        var violations = new TokenConstraintChecker().Check(best, context);
        var violationsByKind = violations
            .GroupBy(v => v.Kind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        // --- Command compliance (metric 6) ---
        var commandViolations = CountCommandBreaches(nonLocked, context);

        // --- Qualification compliance (metric 7) ---
        var qualificationViolations = nonLocked.Count(t =>
            t.ShiftRefId != Guid.Empty && !context.IsEligible(t.AgentId, t.ShiftRefId, t.Date));

        // --- Per-client rows (metrics 1, 2, 4) ---
        var tokensByAgent = nonLocked
            .GroupBy(t => t.AgentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var perClient = new List<PerClientResult>(context.Agents.Count);
        for (var index = 0; index < context.Agents.Count; index++)
        {
            var agent = context.Agents[index];
            var agentTokens = tokensByAgent.TryGetValue(agent.Id, out var ts) ? ts : [];

            var hours = agentTokens.Sum(t => (double)(t.TotalHours + t.Surcharges));
            var target = agent.GuaranteedHours;

            var mix = new int[ShiftTypeCount];
            foreach (var t in agentTokens)
            {
                if (t.ShiftTypeIndex >= 0 && t.ShiftTypeIndex < ShiftTypeCount)
                {
                    mix[t.ShiftTypeIndex]++;
                }
            }

            perClient.Add(new PerClientResult(
                ClientIndex: index,
                ClientId: agent.Id,
                Slots: agentTokens.Count,
                Hours: hours,
                Target: target,
                Deviation: hours - target,
                ShiftTypeMix: mix));
        }

        var periodDays = spec.PeriodUntil.DayNumber - spec.PeriodFrom.DayNumber + 1;

        var metrics = new WizardMetrics(
            Stage0: best.FitnessStage0,
            CoveragePercent: coveragePercent,
            ShiftCoverageRatio: shiftCoverageRatio,
            UndersupplyCount: undersupply,
            OverstaffingCount: overstaffing,
            TheoreticalMaxCoverage: theoreticalMaxCoverage,
            ShiftTypeEntropyAvg: snapshot.ShiftTypeEntropyAvg,
            SlotGini: snapshot.SlotGini,
            RosterFidelityInversionRate: snapshot.RosterFidelityInversionRate,
            TargetReachedPercent: snapshot.TargetReachedPercent,
            QualificationViolations: qualificationViolations,
            CommandViolations: commandViolations,
            ViolationsByKind: violationsByKind,
            DurationMs: durationMs);

        return new WizardAnalysisResult(
            SchemaVersion: SchemaVersion,
            RunTimestamp: runTimestamp,
            ScenarioName: spec.ScenarioName,
            Scale: new WizardScale(
                Clients: context.Agents.Count,
                Shifts: spec.ShiftDefs.Count,
                Slots: availableSlots,
                PeriodDays: periodDays),
            EffectiveConfig: new WizardEffectiveConfig(0, 0, 0),
            Metrics: metrics,
            PerClient: perClient,
            Uncovered: uncovered);
    }

    /// <summary>
    /// Replicates WizardBenchmarkService.SlotLooksFillable / TheoreticalMaxCoverage: a slot is an
    /// upper-bound "fillable" if it is an early slot OR there is at least one shift-capable agent.
    /// </summary>
    private static double ComputeTheoreticalMaxCoverage(CoreWizardContext context)
    {
        var availableSlots = context.Shifts.Count;
        if (availableSlots == 0)
        {
            return 0d;
        }

        var agentsInContext = context.Agents.Count;
        var agentsShiftCapable = context.Agents.Count(a => a.PerformsShiftWork);

        var fillable = context.Shifts.Count(s =>
        {
            if (agentsInContext == 0)
            {
                return false;
            }
            var shiftTypeIndex = ShiftTypeInference.FromStartTimeString(s.StartTime);
            return shiftTypeIndex == 0 || agentsShiftCapable > 0;
        });

        return (double)fillable / availableSlots;
    }

    /// <summary>
    /// Replays context.ScheduleCommands against the token set per (agent, date), mapping
    /// ShiftTypeIndex 0/1/2 to Early/Late/Night. Counts breaches. NotFree is a hint (no veto).
    /// </summary>
    private static int CountCommandBreaches(IReadOnlyList<CoreToken> tokens, CoreWizardContext context)
    {
        if (context.ScheduleCommands.Count == 0)
        {
            return 0;
        }

        var tokensByAgentDate = tokens
            .GroupBy(t => (t.AgentId, t.Date))
            .ToDictionary(g => g.Key, g => g.Select(t => t.ShiftTypeIndex).ToList());

        var breaches = 0;
        foreach (var cmd in context.ScheduleCommands)
        {
            var dayTokens = tokensByAgentDate.TryGetValue((cmd.AgentId, cmd.Date), out var list)
                ? list
                : [];

            var breached = cmd.Keyword switch
            {
                ScheduleCommandKeyword.Free => dayTokens.Count > 0,
                ScheduleCommandKeyword.OnlyEarly => dayTokens.Any(i => i != 0),
                ScheduleCommandKeyword.NoEarly => dayTokens.Any(i => i == 0),
                ScheduleCommandKeyword.OnlyLate => dayTokens.Any(i => i != 1),
                ScheduleCommandKeyword.NoLate => dayTokens.Any(i => i == 1),
                ScheduleCommandKeyword.OnlyNight => dayTokens.Any(i => i != 2),
                ScheduleCommandKeyword.NoNight => dayTokens.Any(i => i == 2),
                _ => false,
            };

            if (breached)
            {
                breaches++;
            }
        }

        return breaches;
    }
}
