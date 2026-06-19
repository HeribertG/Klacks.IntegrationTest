using System.Diagnostics;
using Klacks.IntegrationTest.Wizard.Spec;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Part-time-contract wizard-coverage integration tests. Two scenarios mix full-time agents with
/// part-timers along the two contract axes the builder exposes per client: a hard MaximumHours cap
/// (reduced-hours part-timers) and the PerformsShiftWork flag (day-only / FD-only part-timers). Each
/// test seeds via WizardScenarioBuilder, builds the production CoreWizardContext, runs the
/// deterministic in-process GA at the showcase budget (pop=20/gen=600/seed=42, early-stop disabled
/// so the full budget is spent), analyzes the solution with WizardSolutionAnalyzer, writes the JSON
/// report and hard-asserts the part-time semantics: caps respected (no MaximumHoursExceeded) and
/// day-only agents never carry SD/ND (no PerformsShiftWorkViolation). The fixture shares the
/// builder's deterministic seed GUIDs against the single 5434 DB, so it runs non-parallel.
///
/// If the GA run regresses, Scenario 1 (ReducedHours) is the tight feasibility point: demand 744h
/// against 3 uncapped + 3 96h-capped agents under the default rest rules. Scenario 2 (DayOnly) has
/// comfortable slack.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WizardPartTimeTests : WizardHarnessTestBase
{
    private static readonly DateOnly PeriodFrom = new(2099, 1, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 1, 31);

    private const int PopulationSize = 20;
    private const int MaxGenerations = 600;
    private const int RandomSeed = 42;

    private const decimal FullTimeGuaranteedHours = 180m;
    private const decimal PartTimeGuaranteedHours = 90m;
    private const decimal NoCap = 0m;
    private const decimal PartTimeMaximumHours = 96m;
    private const int PartTimeShiftCap = 12;
    private const decimal ShiftWorkTime = 8m;

    private const int LateShiftTypeIndex = 1;
    private const int NightShiftTypeIndex = 2;

    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void PartTimeSetUp()
    {
        _builder = new WizardScenarioBuilder(Context);
    }

    [TearDown]
    public async Task PartTimeTearDown()
    {
        await _builder.CleanupAsync();
    }

    [Test]
    public async Task PartTime_ReducedHours_RespectsMaxHours()
    {
        // 6 clients: idx 0-2 full-time (180h guaranteed, NO cap), idx 3-5 part-time (90h guaranteed,
        // 96h hard cap = 12 eight-hour shifts). All shift-capable (flat PerformsShiftWork=true). The
        // part-timers are capped, the uncapped full-timers carry the rest of the 744h demand.
        var spec = BuildSpec(
            scenarioName: "PartTime_ReducedHours_RespectsMaxHours",
            clientCount: 6,
            guaranteedHoursPerClient: i => i < 3 ? FullTimeGuaranteedHours : PartTimeGuaranteedHours,
            maximumHoursPerClient: i => i < 3 ? NoCap : PartTimeMaximumHours,
            performsShiftWorkPerClient: null);

        var result = await RunScenarioAsync(spec);

        result.Metrics.TheoreticalMaxCoverage.ShouldBe(1.0,
            "feasible fixture: every FD/SD/ND slot stays fillable by a shift-capable agent");
        result.Metrics.Stage0.ShouldBe(0, "GA must produce zero hard violations");
        result.Metrics.CoveragePercent.ShouldBe(1.0,
            "uncapped full-timers absorb the demand the capped part-timers cannot");
        result.Metrics.UndersupplyCount.ShouldBe(0, "no slot may stay uncovered");

        result.Metrics.ViolationsByKind.ContainsKey("MaximumHoursExceeded").ShouldBeFalse(
            "the 96h hard cap on the part-time clients must be respected");

        // Paid hours >= work hours, so a 96h cap implies at most 12 eight-hour slots per part-timer.
        for (var index = 3; index < spec.ClientCount; index++)
        {
            result.PerClient[index].Slots.ShouldBeLessThanOrEqualTo(PartTimeShiftCap,
                $"capped part-time client {index} may hold at most {PartTimeShiftCap} shifts (96h / 8h)");
        }

        // REPORT: the part-timers' hours must sit visibly below the full-timers' hours.
        var fullTimeMinHours = result.PerClient.Take(3).Min(p => p.Hours);
        for (var index = 3; index < spec.ClientCount; index++)
        {
            result.PerClient[index].Hours.ShouldBeLessThan(fullTimeMinHours,
                $"part-time client {index} carries fewer hours than every full-timer");
        }
    }

    [Test]
    public async Task PartTime_DayOnly_RespectsShiftWork()
    {
        // 8 clients: idx 0-4 shift-capable (180h, no cap), idx 5-7 day-only part-time
        // (PerformsShiftWork=false via the per-client override, 90h guaranteed, 96h cap). Day-only
        // agents may ONLY carry FD (ShiftTypeIndex 0). The five shift-capable agents cover SD+ND.
        var spec = BuildSpec(
            scenarioName: "PartTime_DayOnly_RespectsShiftWork",
            clientCount: 8,
            guaranteedHoursPerClient: i => i < 5 ? FullTimeGuaranteedHours : PartTimeGuaranteedHours,
            maximumHoursPerClient: i => i < 5 ? NoCap : PartTimeMaximumHours,
            performsShiftWorkPerClient: i => i < 5);

        var result = await RunScenarioAsync(spec);

        result.Metrics.TheoreticalMaxCoverage.ShouldBe(1.0,
            "feasible fixture: SD/ND stay fillable by the five shift-capable agents");
        result.Metrics.Stage0.ShouldBe(0, "GA must produce zero hard violations");
        result.Metrics.CoveragePercent.ShouldBe(1.0,
            "the five shift-capable agents comfortably cover SD+ND plus a share of FD");

        result.Metrics.ViolationsByKind.ContainsKey("PerformsShiftWorkViolation").ShouldBeFalse(
            "no day-only agent may be assigned an SD or ND shift");

        // REPORT + assert: the day-only clients carry FD only (zero SD, zero ND).
        for (var index = 5; index < spec.ClientCount; index++)
        {
            result.PerClient[index].ShiftTypeMix[LateShiftTypeIndex].ShouldBe(0,
                $"day-only client {index} must carry zero SD (late) shifts");
            result.PerClient[index].ShiftTypeMix[NightShiftTypeIndex].ShouldBe(0,
                $"day-only client {index} must carry zero ND (night) shifts");
        }
    }

    private static WizardScenarioSpec BuildSpec(
        string scenarioName,
        int clientCount,
        Func<int, decimal> guaranteedHoursPerClient,
        Func<int, decimal> maximumHoursPerClient,
        Func<int, bool>? performsShiftWorkPerClient)
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
            GuaranteedHoursPerClient: guaranteedHoursPerClient,
            ShiftDefs: shifts,
            ContractWorkDays: allDays,
            MaximumHoursPerClient: maximumHoursPerClient,
            FullTimeHours: 40m,
            PerformsShiftWork: true,
            PerformsShiftWorkPerClient: performsShiftWorkPerClient);
    }

    private async Task<WizardAnalysisResult> RunScenarioAsync(WizardScenarioSpec spec)
    {
        var seeded = await _builder.SeedAsync(spec);

        var config = new TokenEvolutionConfig
        {
            PopulationSize = PopulationSize,
            MaxGenerations = MaxGenerations,
            EarlyStopNoImprovementGenerations = MaxGenerations,
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

        return result;
    }
}
