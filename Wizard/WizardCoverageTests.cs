using System.Diagnostics;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Wizard-coverage integration tests. Runs the in-process deterministic GA against a seeded
/// scenario, computes the 7 result metrics via WizardSolutionAnalyzer, hard-asserts the coverage
/// guarantees and reports the stochastic metrics to a JSON artifact + TestContext.Out.
/// </summary>
[TestFixture]
public class WizardCoverageTests : WizardHarnessTestBase
{
    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void CoverageSetUp()
    {
        _builder = new WizardScenarioBuilder(Context);
    }

    [TearDown]
    public async Task CoverageTearDown()
    {
        await _builder.CleanupAsync();
    }

    [Test]
    public async Task FiveClients_ThreeShifts_SevenDay_FullCoverage()
    {
        var spec = WizardScenarioSpec.FiveClientsThreeShiftsSevenDay();
        var seeded = await _builder.SeedAsync(spec);

        // Budget tuned for FD/SD/ND rotation balance (see WizardTuningTests sweep): the shift-type
        // evenness is a low-weight Stage-3 objective, so it keeps improving with generations at
        // pop=20 (entropy 1.414 @gen100 -> 1.497 @gen600 -> 1.514 @gen1000) while coverage/rules
        // stay perfect. 600 is the knee of the curve (~11s). Early-stop MUST equal MaxGenerations,
        // otherwise the GA bails at the first plateau (~gen 20) and the extra budget is wasted.
        var config = new TokenEvolutionConfig
        {
            PopulationSize = 20,
            MaxGenerations = 600,
            EarlyStopNoImprovementGenerations = 600,
            RandomSeed = 42,
            MaxRuntime = null,
        };

        var context = await BuildContextAsync(seeded.ContextRequest);

        var stopwatch = Stopwatch.StartNew();
        var best = RunLoop(context, config);
        stopwatch.Stop();

        var result = WizardSolutionAnalyzer.Analyze(
                best, context, spec, stopwatch.ElapsedMilliseconds, DateTime.Now.ToString("o"))
            with
        {
            EffectiveConfig = new WizardEffectiveConfig(
                config.PopulationSize, config.MaxGenerations, config.RandomSeed),
        };

        WizardReportWriter.Write(result);

        // --- Setup guard: the fixture must be feasible by construction. ---
        result.Metrics.TheoreticalMaxCoverage.ShouldBe(
            1.0,
            "infeasible fixture: theoretical-max coverage < 1.0 means shift/contract weekday flags "
            + "or shift-capability make some slot uncoverable before the GA even runs");

        // --- Hard assertions: zero violations + full coverage. ---
        best.FitnessStage0.ShouldBe(0, "GA must produce zero hard violations");
        result.Metrics.UndersupplyCount.ShouldBe(0, "every FD/SD/ND slot must be filled");
        result.Metrics.CoveragePercent.ShouldBe(1.0, "coverage must be 100%");
        result.Uncovered.ShouldBeEmpty("uncovered slot list must be empty at full coverage");
        result.Metrics.ViolationsByKind.ShouldBeEmpty("no constraint violations of any kind");

        // --- Determinism insurance: same seed, same context => identical token set. ---
        var second = RunLoop(context, config);
        ProjectTokens(best).ShouldBe(ProjectTokens(second),
            "the GA must be deterministic for a fixed seed and a fixed context");
    }

    private static List<(string, Guid, DateOnly, int, decimal, decimal, DateTime, DateTime)> ProjectTokens(
        CoreScenario scenario)
        => scenario.Tokens
            .Select(t => (t.AgentId, t.ShiftRefId, t.Date, t.ShiftTypeIndex, t.TotalHours, t.Surcharges, t.StartAt, t.EndAt))
            .OrderBy(x => x.AgentId, StringComparer.Ordinal)
            .ThenBy(x => x.Date)
            .ThenBy(x => x.ShiftRefId)
            .ThenBy(x => x.ShiftTypeIndex)
            .ToList();
}
