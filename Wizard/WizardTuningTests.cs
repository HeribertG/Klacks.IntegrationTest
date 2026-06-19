using System.Diagnostics;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Tuning experiment: how the GA generation/population budget affects FD/SD/ND rotation balance on
/// the canonical 5-client / 3-shift / 7-day scenario. The shift-type rotation (early -> late ->
/// night) is a low-weight Stage-3 objective (total fitness weights Stage3 by 0.01), so coverage and
/// hours are optimized first and the rotation is only refined once budget remains. At the default
/// 100 generations Stage 3 is barely touched. This sweep spends the FULL budget (early-stop =
/// generations) at increasing budgets and reports ShiftTypeEntropyAvg (per-agent count-evenness;
/// perfect mix = log2(3) ~= 1.585), the Stage-3 soft score (includes the rotation term) and the
/// per-agent FD/SD/ND mix, so the balance-vs-budget trade-off is visible. Deterministic (fixed
/// seed 42); coverage and rule-safety must stay perfect at every budget.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WizardTuningTests : WizardHarnessTestBase
{
    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void TuningSetUp() => _builder = new WizardScenarioBuilder(Context);

    [TearDown]
    public async Task TuningTearDown() => await _builder.CleanupAsync();

    [Test]
    public async Task GenerationSweep_FiveClients_ShiftTypeBalance()
    {
        var spec = WizardScenarioSpec.FiveClientsThreeShiftsSevenDay();
        var seeded = await _builder.SeedAsync(spec);
        var context = await BuildContextAsync(seeded.ContextRequest);

        var budgets = new (int Pop, int Gen)[]
        {
            (20, 100), (20, 300), (20, 600), (20, 1000), (40, 600), (40, 1000),
        };

        TestContext.Out.WriteLine("pop  gen   | stage0 cov%  | entropy(/1.585) stage3 | per-agent FD/SD/ND                     | ms");
        TestContext.Out.WriteLine(new string('-', 112));

        foreach (var (pop, gen) in budgets)
        {
            var config = new TokenEvolutionConfig
            {
                PopulationSize = pop,
                MaxGenerations = gen,
                // Spend the full budget so additional generations actually matter for Stage 3.
                EarlyStopNoImprovementGenerations = gen,
                RandomSeed = 42,
                MaxRuntime = null,
            };

            var sw = Stopwatch.StartNew();
            var best = RunLoop(context, config);
            sw.Stop();

            var result = WizardSolutionAnalyzer.Analyze(
                best, context, spec, sw.ElapsedMilliseconds, DateTime.Now.ToString("o"));

            var mixes = string.Join("  ",
                result.PerClient.Select(p => $"{p.ShiftTypeMix[0]}/{p.ShiftTypeMix[1]}/{p.ShiftTypeMix[2]}"));

            TestContext.Out.WriteLine(
                $"{pop,-4} {gen,-5} | {best.FitnessStage0,-6} {result.Metrics.CoveragePercent,5:F3} | "
                + $"{result.Metrics.ShiftTypeEntropyAvg,7:F4}        {best.FitnessStage3,6:F4} | {mixes,-37} | {sw.ElapsedMilliseconds}");

            // Coverage and rule-safety must never regress as the budget grows.
            best.FitnessStage0.ShouldBe(0, $"coverage/rules must stay perfect at pop={pop} gen={gen}");
            result.Metrics.CoveragePercent.ShouldBe(1.0, $"coverage must stay 100% at pop={pop} gen={gen}");
        }
    }
}
