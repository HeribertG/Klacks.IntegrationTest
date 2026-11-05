// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Live-DB proof (port 5434) of the owner decision 2026-11-05: the Harmonizer family respects the shift-work flag.
/// Seeds two shift workers and two early-only employees (Contract.PerformsShiftWork = false) with a dense real plan
/// whose shift workers are overloaded while an early-only employee is free every other day, so moving a late or night
/// duty onto an early-only row is tempting. The production HarmonizerContextBuilder builds the input; stage 2
/// (conductor + evolution loop) and stage 3 (deterministic search with harmony guard and region rebuild) run as in
/// production. Asserted: the early-only rows gain no late, night or other duty, and the locked late duty an early-only
/// employee already holds stays where it is.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.IntegrationTest.Wizard.Spec;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
public class HarmonizerShiftWorkTests : WizardHarnessTestBase
{
    private const int ClientCount = 4;
    private const int FirstEarlyOnlyClient = 2;
    private const int LockedLateClient = 2;
    private const int LockedLateDayOffset = 5;
    private const int EarlyRotationDays = 2;
    private const int EarlyRotationCycle = 4;
    private const int Stage2Seed = 42;
    private const double EmergencyThreshold = 0.5;
    private const string Early = "FD";
    private const string Late = "SD";
    private const string Night = "ND";
    private const string ShiftWorkReasonFragment = "does not perform shift work";

    private static readonly DateOnly PeriodFrom = new(2099, 5, 4);
    private static readonly DateOnly PeriodUntil = new(2099, 5, 17);
    private static readonly TimeSpan Stage2Runtime = TimeSpan.FromSeconds(20);

    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void ShiftWorkSetUp() => _builder = new WizardScenarioBuilder(Context);

    [TearDown]
    public async Task ShiftWorkTearDown() => await _builder.CleanupAsync();

    [Test]
    public async Task Stage2AndStage3_GiveEarlyOnlyEmployeesNoLateOrNightDuty_AndKeepTheirLockedLate()
    {
        var scenario = await _builder.SeedAsync(BuildSpec());

        BitmapInput input;
        using (var scope = CreateScope())
        {
            var contextBuilder = scope.ServiceProvider.GetRequiredService<IHarmonizerContextBuilder>();
            input = await contextBuilder.BuildContextAsync(
                new HarmonizerContextRequest(PeriodFrom, PeriodUntil, scenario.AgentIds, null),
                CancellationToken.None);
        }

        var earlyOnlyIds = scenario.AgentIds.Skip(FirstEarlyOnlyClient).Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var agent in input.Agents)
        {
            agent.PerformsShiftWork.ShouldBe(!earlyOnlyIds.Contains(agent.Id), $"shift-work flag of {agent.DisplayName}");
        }

        var seed = RowSorter.Sort(BitmapBuilder.Build(input));
        var lockedLateAgent = scenario.AgentIds[LockedLateClient].ToString();
        var lockedLateRow = RowOf(seed, lockedLateAgent);
        var lockedLateDay = LockedLateDayOffset;
        var lockedLate = seed.GetCell(lockedLateRow, lockedLateDay);
        lockedLate.Symbol.ShouldBe(CellSymbol.Late);
        lockedLate.IsLocked.ShouldBeTrue();

        var validator = DomainAwareReplaceValidator.ForInput(input, null);
        ProveTheTemptationIsRejected(seed, validator, earlyOnlyIds);

        var stage2 = RunStage2(input, seed, validator);
        AssertNoNewShiftDutyOnEarlyOnlyRows(seed, stage2, earlyOnlyIds, "stage 2");
        stage2.GetCell(RowOf(stage2, lockedLateAgent), lockedLateDay).ShouldBeSameAs(lockedLate);

        var stage3 = BitmapCloner.Clone(stage2);
        var components = HolisticHarmonizerComponents.Build(
            input, new MemoizedHarmonyFitnessEvaluator(new HarmonyScorer()), HolisticHarmonizerComponents.UntrimmedPool);
        new DeterministicHarmonyOptimizer(components, DeterministicSearchOptions.Harmonized)
            .Run(stage3, progress: null, CancellationToken.None);
        AssertNoNewShiftDutyOnEarlyOnlyRows(seed, stage3, earlyOnlyIds, "stage 3");
        stage3.GetCell(RowOf(stage3, lockedLateAgent), lockedLateDay).ShouldBeSameAs(lockedLate);
    }

    private static HarmonyBitmap RunStage2(BitmapInput input, HarmonyBitmap seed, DomainAwareReplaceValidator validator)
    {
        var scorer = new HarmonyScorer();

        HarmonizerConductor ConductorFactory(int rowCount)
        {
            var emergency = new EmergencyUnlockManager(new EmergencyUnlockState(rowCount), EmergencyThreshold);
            return new HarmonizerConductor(
                scorer,
                new ReplaceMutation(scorer, validator),
                emergency,
                hints: input.SofteningHints,
                blockSwapMutation: new BlockSwapMutation(scorer, validator));
        }

        var loop = new HarmonizerEvolutionLoop(
            new HarmonyFitnessEvaluator(scorer),
            new StochasticBitmapMutation(validator),
            ConductorFactory,
            new HarmonizerEvolutionConfig(Seed: Stage2Seed, MaxRuntime: Stage2Runtime));
        return loop.Run(BitmapCloner.Clone(seed)).Best.Bitmap;
    }

    /// <summary>
    /// Guards against a vacuous pass: on the seed there is a same-day swap that would hand an unlocked late or night
    /// duty of a shift worker to a free early-only row, and the production validator refuses it for the shift-work flag.
    /// </summary>
    private static void ProveTheTemptationIsRejected(HarmonyBitmap seed, DomainAwareReplaceValidator validator, IReadOnlySet<string> earlyOnlyIds)
    {
        for (var day = 0; day < seed.DayCount; day++)
        {
            for (var giver = 0; giver < seed.RowCount; giver++)
            {
                var cell = seed.GetCell(giver, day);
                if (earlyOnlyIds.Contains(seed.Rows[giver].Id) || cell.IsLocked || cell.Symbol is not (CellSymbol.Late or CellSymbol.Night))
                {
                    continue;
                }

                for (var receiver = 0; receiver < seed.RowCount; receiver++)
                {
                    if (earlyOnlyIds.Contains(seed.Rows[receiver].Id) && seed.GetCell(receiver, day).Symbol == CellSymbol.Free)
                    {
                        var reason = validator.Diagnose(seed, new ReplaceMove(giver, receiver, day));
                        reason.ShouldNotBeNull();
                        reason.ShouldContain(ShiftWorkReasonFragment);
                        return;
                    }
                }
            }
        }

        Assert.Fail("The seed offers no late or night duty next to a free early-only row; the test would pass vacuously.");
    }

    private static void AssertNoNewShiftDutyOnEarlyOnlyRows(
        HarmonyBitmap seed, HarmonyBitmap result, IReadOnlySet<string> earlyOnlyIds, string stage)
    {
        for (var row = 0; row < result.RowCount; row++)
        {
            if (!earlyOnlyIds.Contains(result.Rows[row].Id))
            {
                continue;
            }

            var seedRow = RowOf(seed, result.Rows[row].Id);
            for (var day = 0; day < result.DayCount; day++)
            {
                var cell = result.GetCell(row, day);
                if (cell.Symbol is CellSymbol.Late or CellSymbol.Night or CellSymbol.Other)
                {
                    cell.ShouldBeSameAs(
                        seed.GetCell(seedRow, day),
                        $"{stage}: {result.Rows[row].DisplayName} received {cell.Symbol} on {result.Days[day]:yyyy-MM-dd}");
                }
            }
        }
    }

    private static int RowOf(HarmonyBitmap bitmap, string agentId)
    {
        for (var row = 0; row < bitmap.RowCount; row++)
        {
            if (string.Equals(bitmap.Rows[row].Id, agentId, StringComparison.Ordinal))
            {
                return row;
            }
        }

        throw new InvalidOperationException($"Agent {agentId} is not in the bitmap.");
    }

    /// <summary>
    /// Every day one early, one late and one night duty. Early alternates in two-day runs between the two early-only
    /// employees, so one of them is free every day; late and night alternate between the two shift workers, who
    /// therefore work every day. On the locked day the early-only employee 2 holds a locked late duty instead.
    /// </summary>
    private static WizardScenarioSpec BuildSpec()
    {
        var allDays = new[] { true, true, true, true, true, true, true };
        var shifts = new List<ShiftDef>
        {
            new(Early, new TimeOnly(7, 0), new TimeOnly(15, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new(Late, new TimeOnly(15, 0), new TimeOnly(23, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new(Night, new TimeOnly(23, 0), new TimeOnly(7, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: true),
        };

        var works = new List<LockedWorkSpec>();
        var dayCount = PeriodUntil.DayNumber - PeriodFrom.DayNumber + 1;
        for (var d = 0; d < dayCount; d++)
        {
            var date = PeriodFrom.AddDays(d);
            var earlyClient = d % EarlyRotationCycle < EarlyRotationDays ? FirstEarlyOnlyClient : FirstEarlyOnlyClient + 1;
            var lateClient = d % 2;
            var nightClient = 1 - lateClient;

            if (d == LockedLateDayOffset)
            {
                works.Add(new LockedWorkSpec(LockedLateClient, date, Late, WorkLockLevel.Confirmed));
                works.Add(new LockedWorkSpec(LockedLateClient + 1, date, Early, WorkLockLevel.None));
                works.Add(new LockedWorkSpec(nightClient, date, Night, WorkLockLevel.None));
                continue;
            }

            works.Add(new LockedWorkSpec(earlyClient, date, Early, WorkLockLevel.None));
            works.Add(new LockedWorkSpec(lateClient, date, Late, WorkLockLevel.None));
            works.Add(new LockedWorkSpec(nightClient, date, Night, WorkLockLevel.None));
        }

        return new WizardScenarioSpec(
            ScenarioName: "Harmonizer_ShiftWork",
            ClientCount: ClientCount,
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            GuaranteedHoursPerClient: _ => 80m,
            ShiftDefs: shifts,
            ContractWorkDays: allDays,
            MaximumHoursPerClient: _ => 0m,
            FullTimeHours: 40m,
            PerformsShiftWork: true,
            PerformsShiftWorkPerClient: index => index < FirstEarlyOnlyClient,
            LockedWorks: works);
    }
}
