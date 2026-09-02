using System.Diagnostics;
using Klacks.IntegrationTest.Wizard.Spec;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// PARAM/SCALE wizard-coverage integration tests. Each test seeds a parameterized
/// WizardScenarioSpec (varying only existing builder axes: ClientCount,
/// GuaranteedHoursPerClient and ShiftDef.Quantity), runs the deterministic in-process GA via the
/// harness, analyzes the result with WizardSolutionAnalyzer, writes the JSON report and hard-asserts
/// the coverage guarantees. The scale tests size shift quantity to demand ~= 0.8 * capacity and
/// widen the generation budget (plus disable early-stop) so the raised budget is actually spent.
/// All tests share the builder's deterministic seed GUIDs against the single 5434 DB, so the
/// fixture runs non-parallel.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WizardSeriesTests : WizardHarnessTestBase
{
    private static readonly DateOnly PeriodFrom = new(2099, 1, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 1, 31);

    private const int PopulationSize = 20;
    private const int RandomSeed = 42;
    private const int EarlyStopGenerations = 20;

    private const decimal FlatGuaranteedHours = 180m;
    private const decimal ShiftWorkTime = 8m;

    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void SeriesSetUp()
    {
        _builder = new WizardScenarioBuilder(Context);
    }

    [TearDown]
    public async Task SeriesTearDown()
    {
        await _builder.CleanupAsync();
    }

    [Test]
    public async Task FourClients_MinimumFullCoverage()
    {
        // 93 slots (31 days x FD/SD/ND @ 8h) need >= 3 agents per calendar day, since MinPause=12h
        // caps one shift per agent per day. MaxConsecutiveDays=6 (with MinRestDays=2) caps an agent
        // at 6 of every 8 days, so N >= 3 / (6/8) = 4: 4 agents are the exact minimum, not merely
        // feasible. The forward rotation F->S->N->rest is allowed (rest gaps of 24/24/48h); only the
        // backward N->F/S turnaround on the following day is blocked. MaxWorkDays=5 would push the
        // minimum to 5, but it is a soft constraint (Stage1SoftConstraintChecker, no ViolationKind)
        // and therefore does not surface here.
        var spec = BuildSpec(
            scenarioName: "FourClients_MinimumFullCoverage",
            clientCount: 4,
            guaranteedHoursPerClient: _ => FlatGuaranteedHours,
            shiftQuantity: 1);

        // Full budget (early-stop = generations): confirms the minimum is actually reachable.
        var result = await RunScenarioAsync(spec, maxGenerations: 100, earlyStop: 100);

        result.Metrics.TheoreticalMaxCoverage.ShouldBe(1.0);
        result.Metrics.CoveragePercent.ShouldBe(1.0, "4 agents are the exact minimum for 24/7 coverage");
        result.Uncovered.ShouldBeEmpty("4 agents must fully cover the schedule");
        foreach (var restKind in new[] { "MinPauseHours", "MaxConsecutiveDays", "Overlap", "MaxDailyHours" })
        {
            result.Metrics.ViolationsByKind.ContainsKey(restKind).ShouldBeFalse(
                $"engine must not break the {restKind} rule to cover");
        }
    }

    [Test]
    public async Task ThreeClients_HeadcountInfeasible()
    {
        // Same scenario as FourClients_MinimumFullCoverage, one agent short. With only 3 agents,
        // covering every day (3 shifts/day) would require all 3 to work all 31 days, which breaks
        // MaxConsecutiveDays=6. The hard upper bound is 3 agents x 27 of 31 working days (6-on/2-off)
        // = 81/93 slots ~= 0.871. The engine must leave gaps rather than break a rest rule -> the
        // only violations are UnderSupply, never MinPause/MaxConsecutive/Overlap/MaxDaily.
        var spec = BuildSpec(
            scenarioName: "ThreeClients_HeadcountInfeasible",
            clientCount: 3,
            guaranteedHoursPerClient: _ => FlatGuaranteedHours,
            shiftQuantity: 1);

        // Full budget (early-stop = generations): the shortfall is structural, not convergence.
        var result = await RunScenarioAsync(spec, maxGenerations: 100, earlyStop: 100);

        // Data is feasible (theoretical-max ignores headcount-vs-quantity); the shortfall is capacity.
        result.Metrics.TheoreticalMaxCoverage.ShouldBe(1.0);
        result.Metrics.CoveragePercent.ShouldBeLessThan(1.0, "3 agents cannot cover 24/7 under rest rules");
        result.Uncovered.ShouldNotBeEmpty("uncovered slots prove the headcount shortfall");
        result.Metrics.ViolationsByKind.Keys.ShouldContain("UnderSupply");
        foreach (var restKind in new[] { "MinPauseHours", "MaxConsecutiveDays", "Overlap", "MaxDailyHours" })
        {
            result.Metrics.ViolationsByKind.ContainsKey(restKind).ShouldBeFalse(
                $"engine must not break the {restKind} rule to cover");
        }
    }

    [Test]
    public async Task MixedGuaranteedHours()
    {
        var spec = BuildSpec(
            scenarioName: "MixedGuaranteedHours",
            clientCount: 5,
            guaranteedHoursPerClient: i => 100m + i * 30m,
            shiftQuantity: 1);

        var result = await RunScenarioAsync(spec, maxGenerations: 100, earlyStop: EarlyStopGenerations);

        AssertFeasibleFullCoverage(result);
        result.Metrics.CoveragePercent.ShouldBe(1.0, "coverage must be 100%");
    }

    [Test]
    public async Task Scale_TwentyClients()
    {
        const int clientCount = 20;
        const int maxGenerations = 200;

        var spec = BuildSpec(
            scenarioName: "Scale_TwentyClients",
            clientCount: clientCount,
            guaranteedHoursPerClient: _ => FlatGuaranteedHours,
            shiftQuantity: DemandQuantity(clientCount));

        var result = await RunScenarioAsync(spec, maxGenerations: maxGenerations, earlyStop: maxGenerations);

        AssertFeasibleFullCoverage(result);
        result.Metrics.CoveragePercent.ShouldBe(1.0, "coverage must be 100%");
    }

    [Test]
    [Category("Heavy")]
    public async Task Scale_FiftyClients()
    {
        const int clientCount = 50;
        const int maxGenerations = 300;

        var spec = BuildSpec(
            scenarioName: "Scale_FiftyClients",
            clientCount: clientCount,
            guaranteedHoursPerClient: _ => FlatGuaranteedHours,
            shiftQuantity: DemandQuantity(clientCount));

        var result = await RunScenarioAsync(spec, maxGenerations: maxGenerations, earlyStop: maxGenerations);

        AssertFeasibleFullCoverage(result);
        result.Metrics.CoveragePercent.ShouldBe(1.0, "coverage must be 100%");
    }

    /// <summary>
    /// Sizes FD/SD/ND Quantity so demand ~= 0.8 * capacity:
    /// Quantity = max(1, round(ClientCount * 180 * 0.8 / (8 * 31 * 3))). Yields 4/10/19 for 20/50/100.
    /// </summary>
    private static int DemandQuantity(int clientCount)
    {
        var quantity = (int)Math.Round(
            clientCount * 180.0 * 0.8 / (8.0 * 31 * 3), MidpointRounding.AwayFromZero);
        return Math.Max(1, quantity);
    }

    private static WizardScenarioSpec BuildSpec(
        string scenarioName,
        int clientCount,
        Func<int, decimal> guaranteedHoursPerClient,
        int shiftQuantity)
    {
        var allDays = new[] { true, true, true, true, true, true, true };

        var shifts = new List<ShiftDef>
        {
            new("FD", new TimeOnly(7, 0), new TimeOnly(15, 0), WorkTime: ShiftWorkTime, Quantity: shiftQuantity, Weekdays: allDays, CuttingAfterMidnight: false),
            new("SD", new TimeOnly(15, 0), new TimeOnly(23, 0), WorkTime: ShiftWorkTime, Quantity: shiftQuantity, Weekdays: allDays, CuttingAfterMidnight: false),
            new("ND", new TimeOnly(23, 0), new TimeOnly(7, 0), WorkTime: ShiftWorkTime, Quantity: shiftQuantity, Weekdays: allDays, CuttingAfterMidnight: true),
        };

        return new WizardScenarioSpec(
            ScenarioName: scenarioName,
            ClientCount: clientCount,
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            GuaranteedHoursPerClient: guaranteedHoursPerClient,
            ShiftDefs: shifts,
            ContractWorkDays: allDays,
            MaximumHoursPerClient: _ => 0m,
            FullTimeHours: 40m,
            PerformsShiftWork: true);
    }

    private async Task<WizardAnalysisResult> RunScenarioAsync(
        WizardScenarioSpec spec,
        int maxGenerations,
        int earlyStop)
    {
        var (result, _) = await RunScenarioWithSolutionAsync(spec, maxGenerations, earlyStop);
        return result;
    }

    private async Task<(WizardAnalysisResult result, Klacks.ScheduleOptimizer.Models.CoreScenario best)>
        RunScenarioWithSolutionAsync(WizardScenarioSpec spec, int maxGenerations, int earlyStop)
    {
        var seeded = await _builder.SeedAsync(spec);

        var config = new TokenEvolutionConfig
        {
            PopulationSize = PopulationSize,
            MaxGenerations = maxGenerations,
            EarlyStopNoImprovementGenerations = earlyStop,
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

        return (result, best);
    }

    private static void AssertFeasibleFullCoverage(WizardAnalysisResult result)
    {
        result.Metrics.TheoreticalMaxCoverage.ShouldBe(
            1.0,
            "infeasible fixture: theoretical-max coverage < 1.0 means shift/contract weekday flags "
            + "or shift-capability make some slot uncoverable before the GA even runs");

        result.Metrics.Stage0.ShouldBe(0, "GA must produce zero hard violations");
    }
}
