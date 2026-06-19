using System.Diagnostics;
using Klacks.IntegrationTest.Wizard.Spec;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Seed-dependent wizard-availability integration tests. Each test seeds client_availability rows
/// (IsAvailable=false at a shift's start hour) via WizardScenarioBuilder, builds the production
/// CoreWizardContext, runs the deterministic in-process GA, analyzes the solution and hard-asserts
/// the availability-respecting / gap-surfacing behaviour. Availability ineligibility merges into
/// CoreWizardContext.IneligibleAssignments, so the analyzer's QualificationViolations counts a
/// force-assigned unavailable agent too (==0 means none was). TheoreticalMaxCoverage does NOT
/// account for availability (only shift-type fillability). All seeded rows are hard-deleted in
/// TearDown. The fixture shares the builder's deterministic seed GUIDs against the single 5434 DB,
/// so it runs non-parallel.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WizardAvailabilityTests : WizardHarnessTestBase
{
    private const string FdAbbrev = "FD";
    private const string SdAbbrev = "SD";
    private const string NdAbbrev = "ND";
    private const int FdShiftTypeIndex = 0;
    private const int NdShiftTypeIndex = 2;

    private static readonly DateOnly PeriodFrom = new(2099, 1, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 1, 31);

    private const int PopulationSize = 20;
    private const int MaxGenerations = 600;
    private const int EarlyStopGenerations = 600;
    private const int RandomSeed = 42;

    private const decimal FlatGuaranteedHours = 180m;
    private const decimal ShiftWorkTime = 8m;

    private WizardScenarioBuilder _builder = null!;

    [SetUp]
    public void AvailabilitySetUp()
    {
        _builder = new WizardScenarioBuilder(Context);
    }

    [TearDown]
    public async Task AvailabilityTearDown()
    {
        await _builder.CleanupAsync();
    }

    [Test]
    public async Task Availability_Respected_SparseUnavailability()
    {
        var client0BlockedDates = new[]
        {
            new DateOnly(2099, 1, 3),
            new DateOnly(2099, 1, 5),
            new DateOnly(2099, 1, 7),
        };
        var client1BlockedDates = new[]
        {
            new DateOnly(2099, 1, 10),
            new DateOnly(2099, 1, 12),
            new DateOnly(2099, 1, 14),
        };

        var unavailabilities = new List<UnavailabilitySpec>();
        unavailabilities.AddRange(client0BlockedDates
            .Select(d => new UnavailabilitySpec(ClientIndex: 0, Date: d, ShiftAbbrev: FdAbbrev)));
        unavailabilities.AddRange(client1BlockedDates
            .Select(d => new UnavailabilitySpec(ClientIndex: 1, Date: d, ShiftAbbrev: NdAbbrev)));

        var spec = BuildSpec(
            scenarioName: "Availability_Respected_SparseUnavailability",
            clientCount: 6,
            unavailabilities: unavailabilities);

        var (best, context, result) = await SeedBuildRunAnalyzeAsync(spec);

        // Sparse unavailability: full coverage stays reachable because the other agents absorb the
        // blocked cells. Availability does NOT lower TheoreticalMaxCoverage (it only checks
        // shift-type fillability), so the feasible upper bound is still 1.0.
        result.Metrics.TheoreticalMaxCoverage.ShouldBe(1.0,
            "feasible upper bound: all three shift types remain fillable by shift-capable agents");
        result.Metrics.Stage0.ShouldBe(0, "GA must produce zero hard violations");
        result.Metrics.CoveragePercent.ShouldBe(1.0,
            "the available agents cover every blocked cell -> full coverage");
        result.Metrics.UndersupplyCount.ShouldBe(0, "no slot may stay uncovered");
        result.Metrics.QualificationViolations.ShouldBe(0,
            "no unavailable agent may be force-assigned to a blocked (shift, date) cell");

        // Per-token proof: the unavailable agents simply work other slots/days. Client 0 carries no
        // FD on its three blocked dates; client 1 carries no ND on its three blocked dates.
        var client0Id = context.Agents[0].Id;
        var client1Id = context.Agents[1].Id;
        var client0BlockedSet = client0BlockedDates.ToHashSet();
        var client1BlockedSet = client1BlockedDates.ToHashSet();

        var client0BlockedFd = best.Tokens
            .Where(t => t.AgentId == client0Id
                && t.ShiftTypeIndex == FdShiftTypeIndex
                && client0BlockedSet.Contains(t.Date))
            .ToList();
        client0BlockedFd.ShouldBeEmpty(
            "client 0 must carry zero FD shifts on its three unavailable dates");

        var client1BlockedNd = best.Tokens
            .Where(t => t.AgentId == client1Id
                && t.ShiftTypeIndex == NdShiftTypeIndex
                && client1BlockedSet.Contains(t.Date))
            .ToList();
        client1BlockedNd.ShouldBeEmpty(
            "client 1 must carry zero ND shifts on its three unavailable dates");
    }

    [Test]
    public async Task Availability_CoverageGap()
    {
        var blockedDates = new[]
        {
            new DateOnly(2099, 1, 15),
            new DateOnly(2099, 1, 16),
            new DateOnly(2099, 1, 17),
        };

        const int clientCount = 5;

        // Mark ALL clients unavailable for ND on the three blocked dates -> those three ND slots
        // cannot be filled by anyone.
        var unavailabilities = blockedDates
            .SelectMany(d => Enumerable.Range(0, clientCount)
                .Select(c => new UnavailabilitySpec(ClientIndex: c, Date: d, ShiftAbbrev: NdAbbrev)))
            .ToList();

        var spec = BuildSpec(
            scenarioName: "Availability_CoverageGap",
            clientCount: clientCount,
            unavailabilities: unavailabilities);

        var (_, _, result) = await SeedBuildRunAnalyzeAsync(spec);

        // The GA respects availability (never force-assigns an unavailable agent) and surfaces the
        // gap as undersupply rather than breaking the constraint.
        result.Metrics.QualificationViolations.ShouldBe(0,
            "the GA respects availability and never force-assigns an unavailable agent");
        result.Metrics.UndersupplyCount.ShouldBe(3,
            "exactly the three ND slots nobody is available for stay uncovered");
        result.Metrics.CoveragePercent.ShouldBeLessThan(1.0,
            "the three unfillable ND slots prevent full coverage");

        // Every uncovered remainder is the seeded ND shift on one of the three blocked dates.
        var ndShiftId = ShiftRefId(NdAbbrev).ToString();
        var expectedUncovered = blockedDates
            .Select(d => (Date: d.ToString("yyyy-MM-dd"), ShiftRefId: ndShiftId))
            .ToHashSet();
        var actualUncovered = result.Uncovered
            .Select(u => (u.Date, u.ShiftRefId))
            .ToHashSet();
        actualUncovered.ShouldBe(expectedUncovered,
            "the uncovered set is exactly the three ND slots on the three blocked dates");
        result.Uncovered.Count.ShouldBe(3,
            "exactly three slots (the blocked ND cells) read as uncovered");
    }

    private static WizardScenarioSpec BuildSpec(
        string scenarioName,
        int clientCount,
        IReadOnlyList<UnavailabilitySpec> unavailabilities)
    {
        var allDays = new[] { true, true, true, true, true, true, true };

        var shifts = new List<ShiftDef>
        {
            new(FdAbbrev, new TimeOnly(7, 0), new TimeOnly(15, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new(SdAbbrev, new TimeOnly(15, 0), new TimeOnly(23, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            new(NdAbbrev, new TimeOnly(23, 0), new TimeOnly(7, 0), WorkTime: ShiftWorkTime, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: true),
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
            Unavailabilities: unavailabilities);
    }

    private const string ShiftGuidPrefix = "00000000-0000-0000-0004-";

    private static Guid ShiftRefId(string abbrev)
    {
        var index = abbrev switch
        {
            FdAbbrev => 0,
            SdAbbrev => 1,
            NdAbbrev => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(abbrev), abbrev, "unknown shift abbrev"),
        };
        return Guid.Parse($"{ShiftGuidPrefix}{index:D12}");
    }

    private static TokenEvolutionConfig DefaultConfig() => new()
    {
        PopulationSize = PopulationSize,
        MaxGenerations = MaxGenerations,
        EarlyStopNoImprovementGenerations = EarlyStopGenerations,
        RandomSeed = RandomSeed,
        MaxRuntime = null,
    };

    private async Task<(CoreScenario best, CoreWizardContext context, WizardAnalysisResult result)>
        SeedBuildRunAnalyzeAsync(WizardScenarioSpec spec, TokenEvolutionConfig? configOverride = null)
    {
        var seeded = await _builder.SeedAsync(spec);

        var config = configOverride ?? DefaultConfig();

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

        return (best, context, result);
    }
}
