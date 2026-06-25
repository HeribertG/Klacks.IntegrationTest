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
    public async Task FourClients_HeadcountInfeasible()
    {
        // 4 agents structurally cannot tile FD/SD/ND 24/7 under the default rest rules: ND ends
        // 07:00 and MinPause=11h blocks the same agent's next-day FD(07:00)/SD(15:00), so the
        // canonical continental rotation needs >= 5 agents. The engine must leave gaps rather than
        // break a rest rule -> the only violations are UnderSupply, never MinPause/MaxConsecutive/
        // Overlap/MaxDaily. This documents the 5-agent minimum for 24/7 coverage.
        var spec = BuildSpec(
            scenarioName: "FourClients_HeadcountInfeasible",
            clientCount: 4,
            guaranteedHoursPerClient: _ => FlatGuaranteedHours,
            shiftQuantity: 1);

        // Full budget (early-stop = generations): the shortfall is structural, not convergence.
        var result = await RunScenarioAsync(spec, maxGenerations: 100, earlyStop: 100);

        // Data is feasible (theoretical-max ignores headcount-vs-quantity); the shortfall is capacity.
        result.Metrics.TheoreticalMaxCoverage.ShouldBe(1.0);
        result.Metrics.CoveragePercent.ShouldBeLessThan(1.0, "4 agents cannot cover 24/7 under rest rules");
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
