// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Matched A/B pair proving the wizard GA's MaxConsecutiveDays enforcement comes from the
/// admin-editable SchedulingRule and not from the scenario shape: the IDENTICAL scenario (6 clients,
/// FD/SD/ND 24/7, January 2099) and the IDENTICAL deterministic GA budget are run once with a tight
/// rule (MaxConsecutiveDays=3 - no agent may exceed a 3-day run) and once with a loose rule
/// (MaxConsecutiveDays=31 - the GA demonstrably USES runs longer than 3 days). The existing
/// WizardSchedulingRuleTests only cover the tight side; this fixture adds the strict B side.
/// All seeded rows are hard-deleted in TearDown via the shared builder.
/// </summary>

using Klacks.IntegrationTest.Wizard.Spec;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class WizardSchedulingRuleAbTests : WizardHarnessTestBase
{
    private static readonly DateOnly PeriodFrom = new(2099, 1, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 1, 31);

    private const int PopulationSize = 20;
    private const int MaxGenerations = 100;
    private const int RandomSeed = 42;

    private const int TightMaxConsecutiveDays = 3;
    private const int LooseMaxConsecutiveDays = 31;
    private const int ClientCount = 6;
    private const decimal FlatGuaranteedHours = 180m;
    private const decimal ShiftWorkTime = 8m;
    private const decimal MinPauseHours = 11m;

    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void AbSetUp()
    {
        _builder = new WizardScenarioBuilder(Context);
    }

    [TearDown]
    public async Task AbTearDown()
    {
        await _builder.CleanupAsync();
    }

    [Test]
    public async Task MaxConsecutive_TightRule_GaAvoidsRunsBeyondThree()
    {
        var best = await SeedAndRunAsync(TightMaxConsecutiveDays);

        MaxConsecutiveBlock(best).ShouldBeLessThanOrEqualTo(TightMaxConsecutiveDays,
            "with the tight rule the GA must never place a run longer than three days");
    }

    [Test]
    public async Task MaxConsecutive_LooseRule_GaUsesRunsBeyondThree()
    {
        var best = await SeedAndRunAsync(LooseMaxConsecutiveDays);

        MaxConsecutiveBlock(best).ShouldBeGreaterThan(TightMaxConsecutiveDays,
            "with the rule loosened to 31 the SAME scenario and GA budget must produce runs longer "
            + "than three days - proving the avoidance in the tight run is caused by the rule");
    }

    private async Task<CoreScenario> SeedAndRunAsync(int maxConsecutiveDays)
    {
        var allDays = new[] { true, true, true, true, true, true, true };
        var shifts = new List<ShiftDef>
        {
            new("FD", new TimeOnly(7, 0), new TimeOnly(15, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new("SD", new TimeOnly(15, 0), new TimeOnly(23, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new("ND", new TimeOnly(23, 0), new TimeOnly(7, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: true),
        };

        var spec = new WizardScenarioSpec(
            ScenarioName: $"MaxConsecutiveAb_{maxConsecutiveDays}",
            ClientCount: ClientCount,
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            GuaranteedHoursPerClient: _ => FlatGuaranteedHours,
            ShiftDefs: shifts,
            ContractWorkDays: allDays,
            MaximumHoursPerClient: _ => 0m,
            FullTimeHours: 40m,
            PerformsShiftWork: true,
            SchedulingRule: new SchedulingRuleSpec(
                MaxConsecutiveDays: maxConsecutiveDays,
                MinPauseHours: MinPauseHours));

        var seeded = await _builder.SeedAsync(spec);

        var config = new TokenEvolutionConfig
        {
            PopulationSize = PopulationSize,
            MaxGenerations = MaxGenerations,
            RandomSeed = RandomSeed,
        };

        var (best, _) = await BuildContextAndRunAsync(seeded.ContextRequest, config);
        return best;
    }

    private static int MaxConsecutiveBlock(CoreScenario best)
    {
        return best.Tokens
            .GroupBy(t => t.AgentId)
            .Select(g =>
            {
                var dates = g.Select(t => t.Date).Distinct().OrderBy(d => d).ToList();
                var longest = dates.Count == 0 ? 0 : 1;
                var current = longest;
                for (var i = 1; i < dates.Count; i++)
                {
                    if (dates[i].DayNumber == dates[i - 1].DayNumber + 1)
                    {
                        current++;
                        if (current > longest)
                        {
                            longest = current;
                        }
                    }
                    else
                    {
                        current = 1;
                    }
                }

                return longest;
            })
            .DefaultIfEmpty(0)
            .Max();
    }
}
