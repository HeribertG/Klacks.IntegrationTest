// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// [Explicit] live-DB proof (port 5434) that the Wizard-2 fitness-alignment finding holds in the
/// REAL production constraint space, not only the in-memory mechanism study. Seeds a dense,
/// constrained scenario (movable + locked works, breaks, unavailabilities, ±boundary works),
/// builds a genuine <c>BitmapInput</c> via the production <c>HarmonizerContextBuilder</c> (real
/// availability/eligibility services resolved from the in-process DI container), then runs three
/// arms over ONE shared production conductor factory + seed, varying only the population objective:
///   A = conductor-only single pass
///   B = GA + bare HarmonyFitnessEvaluator (exactly what production Wizard-2 uses today)
///   C = GA + AlignedHarmonyFitnessEvaluator (block-consolidation + legality; fairness held out)
/// A→B isolates "the GA drifts into fragmentation in the real config"; B→C isolates "the objective
/// is the cause". C's retained harmony / ScorerLie is measured with the BARE evaluator on C's
/// output, never the aligned value, so the lie detector stays non-circular. Report-first: all arm
/// metrics are printed; structural invariants are asserted; the alignment verdict is computed and
/// printed for the human to read before the finding-level assertions are hardened.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.IntegrationTest.Wizard.Spec;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
public class HarmonizerAlignmentProofTests : WizardHarnessTestBase
{
    private const int ProofClientCount = 6;
    private const int BoundaryDays = 7;
    private const int LockedCellsPerClient = 2;
    private const double ProductionEmergencyThreshold = 0.5;
    private const double KeepIfBetterTolerance = 1e-9;

    private static readonly DateOnly ProofFrom = new(2099, 3, 1);
    private static readonly DateOnly ProofUntil = new(2099, 3, 31);
    private static readonly int[] ProofSeeds = { 42, 7, 123, 999, 2024 };
    private static readonly string[] ShiftRotation = { "FD", "SD", "ND" };

    // Headroom seed (pre-registered): an un-harmonised, hand-built-style real plan with varied
    // block lengths and varied rest gaps, so the harmony objective has genuine room to improve
    // (and thus room to drift into fragmentation). Realistic irregularity, never tuned for drift.
    private static readonly int[] WorkRunLengths = { 5, 4, 6, 3 };
    private static readonly int[] RestRunLengths = { 2, 2, 1, 2 };

    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void ProofSetUp() => _builder = new WizardScenarioBuilder(Context);

    [TearDown]
    public async Task ProofTearDown() => await _builder.CleanupAsync();

    [Test, Explicit("Live-DB Wizard-2 alignment proof against the real test DB (port 5434).")]
    public async Task BareHarmonyGa_DriftsIntoFragmentation_AndAlignedFitnessStopsIt()
    {
        var scenario = await _builder.SeedAsync(BuildSpec());

        BitmapInput input;
        using (var scope = CreateScope())
        {
            var contextBuilder = scope.ServiceProvider.GetRequiredService<IHarmonizerContextBuilder>();
            input = await contextBuilder.BuildContextAsync(
                new HarmonizerContextRequest(ProofFrom, ProofUntil, scenario.AgentIds, null),
                CancellationToken.None);
        }

        var seed = RowSorter.Sort(BitmapBuilder.Build(input));
        var availabilityCount = input.Availability?.Count ?? 0;
        var boundaryCount = input.BoundaryAssignments?.Count ?? 0;
        var ineligibleCount = input.IneligibleAssignments?.Count ?? 0;

        var scorer = new HarmonyScorer();
        var validator = new DomainAwareReplaceValidator(
            input.Availability, input.BoundaryAssignments, input.IneligibleAssignments);
        var bareFitness = new HarmonyFitnessEvaluator(scorer);
        var alignedFitness = new AlignedHarmonyFitnessEvaluator(scorer);
        var stochastic = new StochasticBitmapMutation(validator);

        HarmonizerConductor ConductorFactory(int rowCount)
        {
            var emergency = new EmergencyUnlockManager(new EmergencyUnlockState(rowCount), ProductionEmergencyThreshold);
            var mutation = new ReplaceMutation(scorer, validator);
            var blockSwap = new BlockSwapMutation(scorer, validator);
            return new HarmonizerConductor(scorer, mutation, emergency, hints: input.SofteningHints, blockSwapMutation: blockSwap);
        }

        var seedMetrics = Measure("SEED", seed, bareFitness);

        TestContext.Progress.WriteLine(
            $"PROOF_CONSTRAINTS rows={seed.RowCount} days={seed.DayCount} availability={availabilityCount} boundary={boundaryCount} ineligible={ineligibleCount}");
        TestContext.Progress.WriteLine(
            $"PROOF_SEED blocks={seedMetrics.Blocks} avgLen={seedMetrics.AvgBlockLength:F2} work={seedMetrics.WorkDays} vio={seedMetrics.Violations} harmony={seedMetrics.Harmony:F4}");

        // The conductor has no random source, so arm A is identical across seeds — compute it once.
        var conductorBitmap = RunConductorOnly(seed, ConductorFactory);
        var a = Measure("A_conductor", conductorBitmap, bareFitness);
        TestContext.Progress.WriteLine(
            $"PROOF_ARM_A blocks={a.Blocks} avgLen={a.AvgBlockLength:F2} work={a.WorkDays} vio={a.Violations} harmony={a.Harmony:F4} lie={Lie(seedMetrics, a)}");

        var blocksB = new List<int>();
        var blocksC = new List<int>();
        var workDaysB = new List<int>();
        var workDaysC = new List<int>();
        var retainedFractions = new List<double>();
        var driftSeeds = 0;
        var consolidationSeeds = 0;
        var cAtMostConductorSeeds = 0;
        var lieClearedSeeds = 0;

        foreach (var s in ProofSeeds)
        {
            var config = new HarmonizerEvolutionConfig(Seed: s);
            var bBitmap = RunLoop(bareFitness, stochastic, ConductorFactory, config, seed);
            var cBitmap = RunLoop(alignedFitness, stochastic, ConductorFactory, config, seed);
            var b = Measure("B_bareGA", bBitmap, bareFitness);
            var c = Measure("C_alignedGA", cBitmap, bareFitness);

            var lieB = Lie(seedMetrics, b);
            var lieC = Lie(seedMetrics, c);
            var bareGain = b.Harmony - seedMetrics.Harmony;
            var alignedGain = c.Harmony - seedMetrics.Harmony;
            var retained = Math.Abs(bareGain) < KeepIfBetterTolerance ? 1.0 : alignedGain / bareGain;

            blocksB.Add(b.Blocks);
            blocksC.Add(c.Blocks);
            workDaysB.Add(b.WorkDays);
            workDaysC.Add(c.WorkDays);
            retainedFractions.Add(retained);
            if (b.Blocks > a.Blocks) { driftSeeds++; }
            if (c.Blocks < b.Blocks) { consolidationSeeds++; }
            if (c.Blocks <= a.Blocks) { cAtMostConductorSeeds++; }
            if (lieB && !lieC) { lieClearedSeeds++; }

            TestContext.Progress.WriteLine($"=== seed {s} ===");
            TestContext.Progress.WriteLine(
                $"PROOF_ARM_B blocks={b.Blocks} avgLen={b.AvgBlockLength:F2} work={b.WorkDays} vio={b.Violations} harmony={b.Harmony:F4} lie={lieB}");
            TestContext.Progress.WriteLine(
                $"PROOF_ARM_C blocks={c.Blocks} avgLen={c.AvgBlockLength:F2} work={c.WorkDays} vio={c.Violations} harmony={c.Harmony:F4} lie={lieC}");
            TestContext.Progress.WriteLine(
                $"PROOF_SEED_DELTA driftBvsA={b.Blocks - a.Blocks} consolidationBvsC={b.Blocks - c.Blocks} retainedHarmony={retained:P0}");

            // Structural invariant (must hold regardless of the finding): keep-if-better.
            b.Harmony.ShouldBeGreaterThanOrEqualTo(seedMetrics.Harmony - KeepIfBetterTolerance);
            c.Harmony.ShouldBeGreaterThanOrEqualTo(seedMetrics.Harmony - KeepIfBetterTolerance);
            bBitmap.RowCount.ShouldBe(seed.RowCount);
            cBitmap.RowCount.ShouldBe(seed.RowCount);

            // Robust invariants confirmed across all three pre-registered scenarios (seed harmony
            // 0.31 / 0.59 / 0.67, 15/15 seed-runs): the harmonizer only redistributes a fixed work
            // skeleton (worked-days conserved), and the aligned objective is no-regret on the block
            // axis (never more fragmented than the bare GA). The drift MAGNITUDE scales with how
            // un-harmonised the seed is and is deliberately NOT asserted — it is the relationship.
            b.WorkDays.ShouldBe(seedMetrics.WorkDays);
            c.WorkDays.ShouldBe(seedMetrics.WorkDays);
            c.Blocks.ShouldBeLessThanOrEqualTo(b.Blocks);
        }

        var meanBlocksB = blocksB.Average();
        var meanBlocksC = blocksC.Average();
        var meanRetained = retainedFractions.Average();

        TestContext.Progress.WriteLine("=== AGGREGATE ===");
        TestContext.Progress.WriteLine(
            $"PROOF_AGG conductorBlocks={a.Blocks} meanBareGaBlocks={meanBlocksB:F1} meanAlignedGaBlocks={meanBlocksC:F1} meanRetainedHarmony={meanRetained:P0}");
        TestContext.Progress.WriteLine(
            $"PROOF_WORKDAYS seed={seedMetrics.WorkDays} conductor={a.WorkDays} meanBareGa={workDaysB.Average():F1} meanAlignedGa={workDaysC.Average():F1} (expect conserved -> harmonizer redistributes a fixed work skeleton)");
        TestContext.Progress.WriteLine(
            $"PROOF_VERDICT driftSeeds={driftSeeds}/{ProofSeeds.Length} consolidationSeeds={consolidationSeeds}/{ProofSeeds.Length} cAtMostConductor={cAtMostConductorSeeds}/{ProofSeeds.Length} lieClearedSeeds={lieClearedSeeds}/{ProofSeeds.Length}");
        TestContext.Progress.WriteLine(
            "PROOF_INTERPRETATION drift = bare GA fragments beyond conductor in the REAL constraint space; consolidation = aligned objective stops it (load-bearing); empty constraint maps would mean the scenario degenerated to the in-memory study.");

        // The constraint space must be non-trivial (else the scenario degenerated to the in-memory
        // study), the grid must have work, the conductor must obey keep-if-better, and the work
        // skeleton must be conserved end-to-end. The per-seed loop hardens the no-regret invariant;
        // the seed-dependent drift relationship is PRINTED for the human (see PROOF_VERDICT) and is
        // intentionally NOT asserted — it is the relationship the harness characterises.
        scenario.AgentIds.Count.ShouldBe(ProofClientCount);
        seed.RowCount.ShouldBe(ProofClientCount);
        seedMetrics.Blocks.ShouldBeGreaterThan(0);
        (availabilityCount + boundaryCount + ineligibleCount).ShouldBeGreaterThan(
            0, "constraint maps are empty -> scenario degenerated to the in-memory study, proving nothing new");
        a.Harmony.ShouldBeGreaterThanOrEqualTo(seedMetrics.Harmony - KeepIfBetterTolerance);
        a.WorkDays.ShouldBe(seedMetrics.WorkDays);
    }

    private static HarmonyBitmap RunConductorOnly(HarmonyBitmap seed, Func<int, HarmonizerConductor> conductorFactory)
    {
        var bitmap = BitmapCloner.Clone(seed);
        var conductor = conductorFactory(bitmap.RowCount);
        conductor.Run(bitmap);
        return bitmap;
    }

    private static HarmonyBitmap RunLoop(
        IBitmapFitnessEvaluator fitness,
        StochasticBitmapMutation stochastic,
        Func<int, HarmonizerConductor> conductorFactory,
        HarmonizerEvolutionConfig config,
        HarmonyBitmap seed)
    {
        var loop = new HarmonizerEvolutionLoop(fitness, stochastic, conductorFactory, config);
        var result = loop.Run(BitmapCloner.Clone(seed));
        return result.Best.Bitmap;
    }

    private static ArmMetrics Measure(string name, HarmonyBitmap bitmap, HarmonyFitnessEvaluator bare)
    {
        var (blocks, workDays, violations) = BlockStats(bitmap);
        var avg = blocks > 0 ? (double)workDays / blocks : 0.0;
        return new ArmMetrics(name, blocks, avg, workDays, violations, bare.Evaluate(bitmap).Fitness);
    }

    /// <summary>
    /// Counts consecutive non-Free runs (blocks), working days and consecutive-day-over-limit
    /// violations across all rows — the same block definition the in-memory ScheduleQualityMetrics
    /// uses (everything except Free counts as a working cell; default consecutive cap 6).
    /// </summary>
    private static (int Blocks, int WorkDays, int Violations) BlockStats(HarmonyBitmap bitmap)
    {
        const int defaultMaxConsecutive = 6;
        var blocks = 0;
        var workDays = 0;
        var violations = 0;
        for (var r = 0; r < bitmap.RowCount; r++)
        {
            var maxAllowed = bitmap.Rows[r].MaxConsecutiveDays > 0 ? bitmap.Rows[r].MaxConsecutiveDays : defaultMaxConsecutive;
            var run = 0;
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                if (bitmap.GetCell(r, d).Symbol == CellSymbol.Free)
                {
                    run = 0;
                    continue;
                }
                if (run == 0) { blocks++; }
                run++;
                workDays++;
                if (run > maxAllowed) { violations++; }
            }
        }
        return (blocks, workDays, violations);
    }

    private static bool Lie(ArmMetrics seed, ArmMetrics arm)
        => arm.Harmony > seed.Harmony + KeepIfBetterTolerance
           && (arm.Blocks > seed.Blocks || arm.AvgBlockLength < seed.AvgBlockLength);

    private static WizardScenarioSpec BuildSpec()
    {
        var allDays = new[] { true, true, true, true, true, true, true };
        var shifts = new List<ShiftDef>
        {
            new("FD", new TimeOnly(7, 0), new TimeOnly(15, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new("SD", new TimeOnly(15, 0), new TimeOnly(23, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new("ND", new TimeOnly(23, 0), new TimeOnly(7, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: true),
        };

        var works = new List<LockedWorkSpec>();
        var breaks = new List<BreakSpec>();
        var unavailabilities = new List<UnavailabilitySpec>();

        var gridStart = ProofFrom.AddDays(-BoundaryDays);
        var gridEnd = ProofUntil.AddDays(BoundaryDays);
        var totalDays = gridEnd.DayNumber - gridStart.DayNumber + 1;

        for (var i = 0; i < ProofClientCount; i++)
        {
            var lockedInPeriod = 0;
            var seededBreak = false;
            var seededUnavail = false;

            // Run/rest state machine, phase-offset per client, drawing irregular block and rest
            // lengths from the pre-registered cycles -> varied blocks, not a rigid 5-on-2-off grid.
            var segment = i;
            var working = true;
            var remaining = WorkRunLengths[segment % WorkRunLengths.Length];
            var workBlockIndex = 0;

            for (var d = 0; d < totalDays; d++)
            {
                if (remaining == 0)
                {
                    working = !working;
                    segment++;
                    if (working) { workBlockIndex++; }
                    remaining = working
                        ? WorkRunLengths[segment % WorkRunLengths.Length]
                        : RestRunLengths[segment % RestRunLengths.Length];
                }

                var date = gridStart.AddDays(d);
                var insidePeriod = date >= ProofFrom && date <= ProofUntil;

                if (!working)
                {
                    // One break then one unavailability per client on off-days inside the period,
                    // so they never collide with a seeded work cell.
                    if (insidePeriod && !seededBreak)
                    {
                        breaks.Add(new BreakSpec(i, date, WorkTime: 8m));
                        seededBreak = true;
                    }
                    else if (insidePeriod && !seededUnavail)
                    {
                        unavailabilities.Add(new UnavailabilitySpec(i, date, ShiftRotation[(i + 1) % ShiftRotation.Length]));
                        seededUnavail = true;
                    }

                    remaining--;
                    continue;
                }

                // One shift per block, rotating sensibly across blocks (FD->SD->ND). This lifts the
                // starting harmony into the realistic ~0.5-0.6 band (homogeneous, well-ordered
                // blocks), while the varied block/rest lengths still leave consolidation headroom.
                var shift = ShiftRotation[(workBlockIndex + i) % ShiftRotation.Length];

                var lockLevel = WorkLockLevel.None;
                if (insidePeriod && lockedInPeriod < LockedCellsPerClient)
                {
                    lockLevel = WorkLockLevel.Confirmed;
                    lockedInPeriod++;
                }

                works.Add(new LockedWorkSpec(i, date, shift, lockLevel));
                remaining--;
            }
        }

        return new WizardScenarioSpec(
            ScenarioName: "Harmonizer_Alignment_Proof",
            ClientCount: ProofClientCount,
            PeriodFrom: ProofFrom,
            PeriodUntil: ProofUntil,
            GuaranteedHoursPerClient: _ => 160m,
            ShiftDefs: shifts,
            ContractWorkDays: allDays,
            MaximumHoursPerClient: _ => 0m,
            FullTimeHours: 40m,
            PerformsShiftWork: true,
            LockedWorks: works,
            Breaks: breaks,
            Unavailabilities: unavailabilities);
    }

    private readonly record struct ArmMetrics(string Name, int Blocks, double AvgBlockLength, int WorkDays, int Violations, double Harmony);
}
