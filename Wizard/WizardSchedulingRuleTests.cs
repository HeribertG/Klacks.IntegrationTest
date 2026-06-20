using System.Diagnostics;
using Klacks.IntegrationTest.Wizard.Spec;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Seed-dependent wizard SchedulingRule integration tests. Each test seeds a shared SchedulingRule
/// (linked to every per-client Contract via SchedulingRuleId, reaching the GA through
/// EffectiveContractData) plus the FD/SD/ND 24/7 shift pool, builds the production
/// CoreWizardContext, runs the deterministic in-process GA, analyzes the solution with
/// WizardSolutionAnalyzer, writes the JSON report and hard-asserts that the MaxConsecutiveDays rule
/// binds and is respected. All seeded rows are hard-deleted in TearDown.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WizardSchedulingRuleTests : WizardHarnessTestBase
{
    private static readonly DateOnly PeriodFrom = new(2099, 1, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 1, 31);

    private const int PopulationSize = 20;
    private const int MaxGenerations = 600;
    private const int EarlyStopGenerations = 600;
    private const int RandomSeed = 42;

    private const decimal FlatGuaranteedHours = 180m;
    private const decimal ShiftWorkTime = 8m;
    private const decimal MinPauseHours = 11m;

    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void RuleSetUp()
    {
        _builder = new WizardScenarioBuilder(Context);
    }

    [TearDown]
    public async Task RuleTearDown()
    {
        await _builder.CleanupAsync();
    }

    [Test]
    public async Task SchedulingRule_TightMaxConsecutive_Respected()
    {
        // 6 clients buy enough headroom that a MaxConsecutiveDays=3 rule (default is 6, so 3 binds)
        // still admits full FD/SD/ND 24/7 coverage: the planner can rotate work blocks so no agent
        // ever works more than three consecutive calendar days.
        var spec = BuildSpec(
            scenarioName: "SchedulingRule_TightMaxConsecutive_Respected",
            clientCount: 6,
            rule: new SchedulingRuleSpec(MaxConsecutiveDays: 3, MinPauseHours: MinPauseHours));

        var (best, result) = await SeedBuildRunAnalyzeAsync(spec);

        result.Metrics.TheoreticalMaxCoverage.ShouldBe(1.0,
            "feasible upper bound: all three shift types remain fillable by shift-capable agents");
        result.Metrics.Stage0.ShouldBe(0, "GA must reach full real coverage with zero hard violations");
        result.Metrics.CoveragePercent.ShouldBe(1.0, "six agents fully tile 24/7 under MaxConsecutive=3");
        result.Metrics.UndersupplyCount.ShouldBe(0, "no slot stays uncovered");
        result.Metrics.ViolationsByKind.ContainsKey("MaxConsecutiveDays").ShouldBeFalse(
            "the engine must never break the MaxConsecutiveDays rule");
        MaxConsecutiveBlock(best).ShouldBeLessThanOrEqualTo(3,
            "the rule binds: no agent works more than three consecutive calendar days");
    }

    /// <summary>
    /// MaxConsecutiveDays=1 is an extreme value chosen to make the rule provably bind AND kip
    /// coverage. With a max run of one day every agent's work days must be strictly non-consecutive,
    /// so the day after any fully-covered (3-agent) day has at most 5-3=2 agents available -> full
    /// 24/7 coverage is structurally impossible regardless of GA budget. The planner respects the
    /// rule and leaves slots empty rather than violating it, which surfaces as undersupply gaps.
    /// </summary>
    [Test]
    public async Task SchedulingRule_ExtremeMaxConsecutive_KipsCoverage()
    {
        var spec = BuildSpec(
            scenarioName: "SchedulingRule_ExtremeMaxConsecutive_KipsCoverage",
            clientCount: 5,
            rule: new SchedulingRuleSpec(MaxConsecutiveDays: 1, MinPauseHours: MinPauseHours));

        var (best, result) = await SeedBuildRunAnalyzeAsync(spec);

        result.Metrics.UndersupplyCount.ShouldBeGreaterThan(0,
            "MaxConsecutive=1 structurally starves coverage -> gaps must appear");
        result.Metrics.CoveragePercent.ShouldBeLessThan(1.0,
            "full 24/7 coverage is impossible when no agent may work two consecutive days");
        result.Metrics.ViolationsByKind.ContainsKey("MaxConsecutiveDays").ShouldBeFalse(
            "the engine respects the rule and never breaks MaxConsecutiveDays");
        result.Metrics.ViolationsByKind.ContainsKey("MinPauseHours").ShouldBeFalse(
            "the engine respects the rule and never breaks MinPauseHours");
        MaxConsecutiveBlock(best).ShouldBeLessThanOrEqualTo(1,
            "the rule binds: no agent works two consecutive calendar days");
    }

    /// <summary>
    /// Longest run of consecutive calendar work days across all agents. Groups tokens by AgentId,
    /// reduces each agent to its DISTINCT set of work dates, orders ascending and walks the run of
    /// adjacent days (next.DayNumber == prev.DayNumber + 1). Returns the max run length over all
    /// agents (0 if no tokens).
    /// </summary>
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

    private static WizardScenarioSpec BuildSpec(
        string scenarioName,
        int clientCount,
        SchedulingRuleSpec rule)
    {
        var allDays = new[] { true, true, true, true, true, true, true };

        var shifts = new List<ShiftDef>
        {
            new("FD", new TimeOnly(7, 0), new TimeOnly(15, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new("SD", new TimeOnly(15, 0), new TimeOnly(23, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new("ND", new TimeOnly(23, 0), new TimeOnly(7, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: true),
        };

        return new WizardScenarioSpec(
            ScenarioName: scenarioName,
            ClientCount: clientCount,
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            GuaranteedHoursPerClient: _ => FlatGuaranteedHours,
            ShiftDefs: shifts,
            ContractWorkDays: allDays,
            MaximumHoursPerClient: _ => 0m,
            FullTimeHours: 40m,
            PerformsShiftWork: true,
            SchedulingRule: rule);
    }

    private async Task<(CoreScenario best, WizardAnalysisResult result)>
        SeedBuildRunAnalyzeAsync(WizardScenarioSpec spec)
    {
        var seeded = await _builder.SeedAsync(spec);

        var config = new TokenEvolutionConfig
        {
            PopulationSize = PopulationSize,
            MaxGenerations = MaxGenerations,
            EarlyStopNoImprovementGenerations = EarlyStopGenerations,
            RandomSeed = RandomSeed,
            MaxRuntime = null,
        };

        var stopwatch = Stopwatch.StartNew();
        var (best, context) = await BuildContextAndRunAsync(seeded.ContextRequest, config);
        stopwatch.Stop();

        var result = WizardSolutionAnalyzer.Analyze(
                best, context, spec, stopwatch.ElapsedMilliseconds, DateTime.Now.ToString("o"))
            with
        {
            EffectiveConfig = new WizardEffectiveConfig(
                config.PopulationSize, config.MaxGenerations, config.RandomSeed),
        };

        WizardReportWriter.Write(result);

        return (best, result);
    }
}
