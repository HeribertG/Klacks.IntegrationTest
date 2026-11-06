// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.IntegrationTest.Wizard.Spec;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Wizard-1 scenarios for employees WITHOUT a contract (owner rule 2026-11-05: a day is plannable when the
/// employee is a member that day; the effective contract data - a real contract or the settings defaults -
/// decides the weekdays). One 20-employee, FD/SD/ND, 4-week (Feb 2099) scenario in several variants:
/// A = every employee has a contract equal to the settings defaults, B = nobody has a contract,
/// C = every odd index is contract-less (alternating so contract status is not confounded with the roster
/// rank), D = odd indexes contract-less while the even ones carry heterogeneous contracts (day-only
/// part-timers and default-equivalent full-timers), DRef = D with the contract-less employees given a
/// default-equivalent contract. The contract values are READ from the settings rows of the IT DB (never
/// written), so the snapshot pairs A/B, A/C and D/DRef are equivalent inputs by construction and must
/// produce identical engine agents and contract days whatever the settings hold. The optimizer never reads
/// ContractId, so equal snapshots imply equal GA inputs; one GA smoke run (variant D) is enough to show
/// that contract-less agents get planned. A membership variant proves that the exit bounds the plannable
/// days of a contract-less employee, that an entry inside the period opens them (entry and exit inclusive) and that
/// one without any member day is excluded. The shifts are offered on the weekdays the settings defaults tick, so the
/// GA smoke stays feasible whatever weekdays the installation configured. The GA seed comes from
/// <see cref="WizardRunSeed"/> (default 42; the environment variable WIZARD_SEED overrides it for local measurement
/// series only, never in CI).
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("RealDatabase")]
public class WizardContractlessTests : WizardHarnessTestBase
{
    private static readonly DateOnly PeriodFrom = new(2099, 2, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 2, 28);

    private const int ClientCount = 20;
    private const int PopulationSize = 20;
    private const int MaxGenerations = 100;
    private const int EarlyStopGenerations = 20;
    private const decimal ShiftWorkTime = 8m;
    private const int DayShiftQuantity = 4;
    private const int LateShiftQuantity = 4;
    private const int NightShiftQuantity = 3;

    private const decimal PartTimeGuaranteedHours = 120m;
    private const decimal PartTimeMaximumHours = 130m;
    private const int PartTimeModulo = 4;
    private const int AlternationModulo = 2;

    private static readonly TimeOnly EarlyShiftStart = new(7, 0);
    private static readonly TimeOnly LateShiftStart = new(15, 0);
    private static readonly TimeOnly NightShiftStart = new(23, 0);

    private const int ExitClientIndex = 1;
    private const int NonMemberClientIndex = 3;
    private const int EntryClientIndex = 5;
    private const int EarliestEntryOffsetDays = 6;
    private const int EarliestExitOffsetDays = 13;
    private const int PreviousDayOffset = -1;
    private const int NextDayOffset = 1;
    private const string AllContractsScenarioName = "Contractless_Snapshot_A";
    private static readonly DateOnly MembershipEntry = new(2090, 1, 1);
    private static readonly DateOnly FormerMemberEntry = new(2098, 1, 1);
    private static readonly DateOnly FormerMemberExit = new(2099, 1, 15);

    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void ContractlessSetUp()
    {
        _builder = new WizardScenarioBuilder(Context);
    }

    [TearDown]
    public async Task ContractlessTearDown()
    {
        await _builder.CleanupAsync();
    }

    [Test]
    public async Task Contractless_Snapshot_NoContractsMatchesAllContracts()
    {
        var defaults = await LoadSettingsDefaultsAsync();

        await AssertEquivalentSnapshotsAsync(
            BuildSpec(AllContractsScenarioName, defaults, _ => true, heterogeneous: false),
            BuildSpec("Contractless_Snapshot_B", defaults, _ => false, heterogeneous: false));
    }

    [Test]
    public async Task Contractless_Snapshot_MixedMatchesAllContracts()
    {
        var defaults = await LoadSettingsDefaultsAsync();

        await AssertEquivalentSnapshotsAsync(
            BuildSpec(AllContractsScenarioName, defaults, _ => true, heterogeneous: false),
            BuildSpec("Contractless_Snapshot_C", defaults, IsEvenIndex, heterogeneous: false));
    }

    [Test]
    public async Task Contractless_Snapshot_HeterogeneousMixMatchesAllContracts()
    {
        var defaults = await LoadSettingsDefaultsAsync();

        await AssertEquivalentSnapshotsAsync(
            BuildSpec("Contractless_Snapshot_DRef", defaults, _ => true, heterogeneous: true),
            BuildSpec("Contractless_Snapshot_D", defaults, IsEvenIndex, heterogeneous: true));
    }

    [Test]
    public async Task Contractless_Snapshot_MembershipEntryAndExitBoundPlannableDays_AndNonMemberIsExcluded()
    {
        var defaults = await LoadSettingsDefaultsAsync();
        Assume.That(TicksAnyWeekday(defaults), Is.True,
            "the settings defaults tick no weekday, so a contract-less member has no plannable day to bound; "
            + "see the 'Settings defaults' line");

        var entryDay = PickBoundDay(defaults, PeriodFrom.AddDays(EarliestEntryOffsetDays), PreviousDayOffset);
        var exitDay = PickBoundDay(defaults, PeriodFrom.AddDays(EarliestExitOffsetDays), NextDayOffset);
        var spec = BuildSpec("Contractless_Snapshot_Membership", defaults, IsEvenIndex, heterogeneous: false) with
        {
            Memberships =
            [
                new MembershipSpec(ExitClientIndex, MembershipEntry, exitDay),
                new MembershipSpec(NonMemberClientIndex, FormerMemberEntry, FormerMemberExit),
                new MembershipSpec(EntryClientIndex, entryDay, null),
            ],
        };

        var seeded = await _builder.SeedAsync(spec);
        var context = await BuildContextAsync(seeded.ContextRequest);
        var exitAgentId = seeded.AgentIds[ExitClientIndex].ToString();
        var nonMemberAgentId = seeded.AgentIds[NonMemberClientIndex].ToString();
        var entryAgentId = seeded.AgentIds[EntryClientIndex].ToString();

        TestContext.Out.WriteLine(
            $"Exit day of contract-less client {ExitClientIndex}: {exitDay.ToString(DateTimeFormats.IsoDate)} ({exitDay.DayOfWeek})");
        TestContext.Out.WriteLine(
            $"Entry day of contract-less client {EntryClientIndex}: {entryDay.ToString(DateTimeFormats.IsoDate)} ({entryDay.DayOfWeek})");

        context.Agents.Select(a => a.Id).ShouldContain(exitAgentId,
            "a contract-less member with an exit inside the period stays an agent");
        context.Agents.Select(a => a.Id).ShouldContain(entryAgentId,
            "a contract-less member with an entry inside the period stays an agent");
        context.Agents.Select(a => a.Id).ShouldNotContain(nonMemberAgentId,
            "a contract-less client without any member day in the period must be excluded");
        context.Agents.Count.ShouldBe(ClientCount - 1, "only the client without member day is excluded");

        var periodDayCount = PeriodUntil.DayNumber - PeriodFrom.DayNumber + 1;
        var exitDays = WorksOnDayByDate(context, exitAgentId);
        var entryDays = WorksOnDayByDate(context, entryAgentId);
        exitDays.Count.ShouldBe(periodDayCount, "one contract day per period date (exit client)");
        entryDays.Count.ShouldBe(periodDayCount, "one contract day per period date (entry client)");
        exitDays[exitDay].ShouldBeTrue("the exit day is inclusive: still a member that day");
        entryDays[entryDay].ShouldBeTrue("the entry day is inclusive: already a member that day");

        for (var date = PeriodFrom; date <= PeriodUntil; date = date.AddDays(1))
        {
            var ticked = WorksOnWeekday(defaults, date.DayOfWeek);
            var day = date.ToString(DateTimeFormats.IsoDate);
            exitDays[date].ShouldBe(date <= exitDay && ticked,
                $"{day}: member days follow the settings weekdays, every day after the exit is closed");
            entryDays[date].ShouldBe(date >= entryDay && ticked,
                $"{day}: every day before the entry is closed, member days follow the settings weekdays");
        }

        var nonMemberDays = WorksOnDayByDate(context, nonMemberAgentId);
        nonMemberDays.Count.ShouldBe(periodDayCount, "one closed contract day per period date (former member)");
        nonMemberDays.Values.ShouldAllBe(worksOnDay => !worksOnDay, "a former member has no plannable day");
    }

    [Test]
    public async Task Contractless_GaSmoke_MixedHeterogeneous_PlansContractlessAgents()
    {
        var defaults = await LoadSettingsDefaultsAsync();
        Assume.That(
            TicksAnyWeekday(defaults) && defaults.GuaranteedHours > 0m,
            Is.True,
            "the GA smoke needs contract-less employees with plannable days and hours (at least one "
            + "SCHEDULING_DEFAULT_WORK_ON_* = true, guaranteed hours > 0); this DB differs, see the 'Settings defaults' line. "
            + "Settings are never written by this test.");

        var spec = BuildSpec("Contractless_D_MixedHeterogeneous", defaults, IsEvenIndex, heterogeneous: true);
        var seeded = await _builder.SeedAsync(spec);

        var config = new TokenEvolutionConfig
        {
            PopulationSize = PopulationSize,
            MaxGenerations = MaxGenerations,
            EarlyStopNoImprovementGenerations = EarlyStopGenerations,
            RandomSeed = WizardRunSeed.Resolve(),
            MaxRuntime = null,
        };

        var context = await BuildContextAsync(seeded.ContextRequest);
        context.Agents.Count.ShouldBe(ClientCount, "every employee, with or without contract, must reach the engine");

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

        result.Metrics.TheoreticalMaxCoverage.ShouldBe(1.0, "fixture must be feasible by construction");
        result.Metrics.Stage0.ShouldBe(0, "GA must produce zero hard violations");

        var slotsByAgent = result.PerClient.ToDictionary(c => c.ClientId, c => c.Slots, StringComparer.Ordinal);
        for (var i = 0; i < ClientCount; i++)
        {
            if (spec.HasContract(i))
            {
                continue;
            }

            slotsByAgent[seeded.AgentIds[i].ToString()].ShouldBeGreaterThan(0,
                $"contract-less employee {i} must be planned with the settings defaults");
        }
    }

    private static bool IsEvenIndex(int index) => index % AlternationModulo == 0;

    private static Dictionary<DateOnly, bool> WorksOnDayByDate(CoreWizardContext context, string agentId) =>
        context.ContractDays
            .Where(d => d.AgentId == agentId)
            .ToDictionary(d => d.Date, d => d.WorksOnDay);

    private async Task<EffectiveContractData> LoadSettingsDefaultsAsync()
    {
        using var scope = CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IClientContractDataProvider>();
        var defaults = await provider.GetEffectiveContractDataAsync(Guid.Empty, PeriodFrom);

        defaults.HasActiveContract.ShouldBeFalse("Guid.Empty must resolve to the settings defaults");

        TestContext.Out.WriteLine(
            $"Settings defaults: guaranteed={defaults.GuaranteedHours} max={defaults.MaximumHours} min={defaults.MinimumHours} "
            + $"fullTime={defaults.FullTime} shiftWork={defaults.PerformsShiftWork} maxConsecutive={defaults.MaxConsecutiveDays} "
            + $"minPause={defaults.MinPauseHours} maxDaily={defaults.MaxDailyHours} maxWeekly={defaults.MaxWeeklyHours} "
            + $"maxWorkDays={defaults.MaxWorkDays} minRestDays={defaults.MinRestDays} "
            + $"weekdays={defaults.WorkOnMonday}/{defaults.WorkOnTuesday}/{defaults.WorkOnWednesday}/{defaults.WorkOnThursday}/"
            + $"{defaults.WorkOnFriday}/{defaults.WorkOnSaturday}/{defaults.WorkOnSunday}");

        return defaults;
    }

    private static bool[] WeekdayFlags(EffectiveContractData data) =>
    [
        data.WorkOnMonday, data.WorkOnTuesday, data.WorkOnWednesday, data.WorkOnThursday,
        data.WorkOnFriday, data.WorkOnSaturday, data.WorkOnSunday,
    ];

    private static bool TicksAnyWeekday(EffectiveContractData data) => WeekdayFlags(data).Any(flag => flag);

    private static bool WorksOnWeekday(EffectiveContractData data, DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => data.WorkOnMonday,
        DayOfWeek.Tuesday => data.WorkOnTuesday,
        DayOfWeek.Wednesday => data.WorkOnWednesday,
        DayOfWeek.Thursday => data.WorkOnThursday,
        DayOfWeek.Friday => data.WorkOnFriday,
        DayOfWeek.Saturday => data.WorkOnSaturday,
        DayOfWeek.Sunday => data.WorkOnSunday,
        _ => false,
    };

    /// <summary>
    /// First ticked day from <paramref name="from"/> on whose <paramref name="neighbourOffsetDays"/> neighbour is ticked
    /// too, so an off-by-one at the membership bound flips a ticked day and fails the test; falls back to the first
    /// ticked day when the defaults tick no such pair.
    /// </summary>
    private static DateOnly PickBoundDay(EffectiveContractData data, DateOnly from, int neighbourOffsetDays)
    {
        DateOnly? firstTicked = null;
        for (var date = from; date <= PeriodUntil; date = date.AddDays(1))
        {
            if (!WorksOnWeekday(data, date.DayOfWeek))
            {
                continue;
            }

            firstTicked ??= date;
            if (WorksOnWeekday(data, date.AddDays(neighbourOffsetDays).DayOfWeek))
            {
                return date;
            }
        }

        return firstTicked
            ?? throw new InvalidOperationException("No ticked weekday until the period end; the caller must assume one.");
    }

    /// <summary>
    /// Builds the scenario. Contract holders get the settings defaults as explicit contract values; in the
    /// heterogeneous variants every PartTimeModulo-th contract holder is a day-only part-timer instead. The shifts
    /// are offered on the default weekdays only; when the defaults perform no shift work, the same demand is offered
    /// as day shifts only, so the scenario stays feasible.
    /// </summary>
    private static WizardScenarioSpec BuildSpec(
        string scenarioName,
        EffectiveContractData defaults,
        Func<int, bool> hasContract,
        bool heterogeneous)
    {
        var shiftDays = WeekdayFlags(defaults);

        bool IsPartTimer(int index) => heterogeneous && hasContract(index) && index % PartTimeModulo == 0;

        var shifts = defaults.PerformsShiftWork
            ? new List<ShiftDef>
            {
                new("FD", EarlyShiftStart, LateShiftStart, WorkTime: ShiftWorkTime, Quantity: DayShiftQuantity, Weekdays: shiftDays, CuttingAfterMidnight: false),
                new("SD", LateShiftStart, NightShiftStart, WorkTime: ShiftWorkTime, Quantity: LateShiftQuantity, Weekdays: shiftDays, CuttingAfterMidnight: false),
                new("ND", NightShiftStart, EarlyShiftStart, WorkTime: ShiftWorkTime, Quantity: NightShiftQuantity, Weekdays: shiftDays, CuttingAfterMidnight: true),
            }
            : new List<ShiftDef>
            {
                new("FD", EarlyShiftStart, LateShiftStart, WorkTime: ShiftWorkTime, Quantity: DayShiftQuantity + LateShiftQuantity + NightShiftQuantity, Weekdays: shiftDays, CuttingAfterMidnight: false),
            };

        return new WizardScenarioSpec(
            ScenarioName: scenarioName,
            ClientCount: ClientCount,
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            GuaranteedHoursPerClient: i => IsPartTimer(i) ? PartTimeGuaranteedHours : defaults.GuaranteedHours,
            ShiftDefs: shifts,
            ContractWorkDays: WeekdayFlags(defaults),
            MaximumHoursPerClient: i => IsPartTimer(i) ? PartTimeMaximumHours : defaults.MaximumHours,
            FullTimeHours: defaults.FullTime,
            PerformsShiftWork: defaults.PerformsShiftWork,
            PerformsShiftWorkPerClient: i => !IsPartTimer(i) && defaults.PerformsShiftWork,
            HasContractPerClient: hasContract);
    }

    /// <summary>
    /// Seeds both specs one after the other (fresh builder and cleared change tracker in between, because the
    /// builder accumulates ids and the raw-SQL purge leaves tracked entities behind) and compares the engine
    /// snapshots: agents serialized as a whole, contract days without ContractId.
    /// </summary>
    private async Task AssertEquivalentSnapshotsAsync(WizardScenarioSpec reference, WizardScenarioSpec candidate)
    {
        var expected = await BuildSnapshotProjectionAsync(reference);

        await _builder.CleanupAsync();
        Context.ChangeTracker.Clear();
        _builder = new WizardScenarioBuilder(Context);

        var actual = await BuildSnapshotProjectionAsync(candidate);

        expected.AgentCount.ShouldBe(ClientCount, $"{reference.ScenarioName}: every employee must reach the engine");
        actual.AgentCount.ShouldBe(ClientCount, $"{candidate.ScenarioName}: every contract-less member must reach the engine");
        actual.Agents.ShouldBe(expected.Agents,
            $"{candidate.ScenarioName} vs {reference.ScenarioName}: a contract-less employee must look exactly like "
            + "an employee whose contract equals the settings defaults");
        actual.Days.ShouldBe(expected.Days,
            $"{candidate.ScenarioName} vs {reference.ScenarioName}: the contract days (apart from ContractId) must be identical");
    }

    private async Task<ContractlessSnapshotProjection> BuildSnapshotProjectionAsync(WizardScenarioSpec spec)
    {
        var seeded = await _builder.SeedAsync(spec);
        var context = await BuildContextAsync(seeded.ContextRequest);
        return ContractlessSnapshotProjection.From(context);
    }
}
