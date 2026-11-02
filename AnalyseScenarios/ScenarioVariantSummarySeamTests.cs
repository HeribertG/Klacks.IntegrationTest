// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of the per-variant part of ScenarioSummaryBuilder: demand, filled and open slots per linked variant,
/// the peak as the day maximum of the open slots summed over the variant's shifts (not a per-shift peak), the earliest day on
/// a tie, no peak date when nothing is open, and the cases where a clone belongs to no variant: an unlinked variant, a plain
/// clone, a soft-deleted key shift, a promoted key (token gone) and a scenario without links. Only INTEGRATION_TEST_ rows are
/// written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules.Summary;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class ScenarioVariantSummarySeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARSUMMARY_";
    private const string VariantA = "A_BIG";
    private const string VariantB = "B_TIE";
    private const string VariantC = "C_UNLINKED";
    private const string VariantD = "D_FULL";
    private const string PlainShift = "PLAIN";
    private const int WorkerCount = 6;
    private const int FirstShiftSlotsPerDay = 2;
    private const int SecondShiftSlotsPerDay = 3;
    private const int TieShiftSlotsPerDay = 2;
    private const int FullShiftSlotsPerDay = 1;
    private const int PlainShiftSlotsPerDay = 1;
    private const int DayCount = 3;
    private const int ExpectedDemandedA = (FirstShiftSlotsPerDay + SecondShiftSlotsPerDay) * DayCount;
    private const int ExpectedFilledA = 8;
    private const int ExpectedPeakA = 4;
    private const int ExpectedDemandedB = TieShiftSlotsPerDay * DayCount;
    private const int ExpectedPeakB = TieShiftSlotsPerDay;
    private const int ExpectedDemandedD = FullShiftSlotsPerDay * DayCount;
    private const string FirstShift = "A_BIG_1";
    private const string SecondShift = "A_BIG_2";
    private const string KeySuffix = "_KEY";
    private const string RealSuffix = "_REAL";
    private static readonly int[] FirstShiftWorksPerDay = [0, 1, 2];
    private static readonly int[] SecondShiftWorksPerDay = [2, 0, 3];
    private static readonly int[] FullShiftWorksPerDay = [1, 1, 1];
    private static readonly (string ShiftSuffix, string VariantSuffix, int Slots)[] KeyShiftPlan =
    [
        (FirstShift, VariantA, FirstShiftSlotsPerDay),
        (SecondShift, VariantA, SecondShiftSlotsPerDay),
        (VariantB, VariantB, TieShiftSlotsPerDay),
        (VariantC, VariantC, TieShiftSlotsPerDay),
        (VariantD, VariantD, FullShiftSlotsPerDay)
    ];
    private static readonly DateOnly FirstDay = new(2099, 10, 1);
    private static readonly DateOnly LastDay = FirstDay.AddDays(DayCount - 1);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenario _scenario = null!;
    private readonly List<Client> _workers = [];
    private readonly Dictionary<DateOnly, int> _nextWorkerByDay = [];
    private readonly Dictionary<string, AnalyseScenario> _variants = [];
    private readonly Dictionary<string, Shift> _keyShifts = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        await CleanupAsync(_context);
        _workers.Clear();
        _nextWorkerByDay.Clear();
        _variants.Clear();
        _keyShifts.Clear();

        for (var index = 0; index < WorkerCount; index++)
        {
            _workers.Add(new Client
            {
                Id = Guid.NewGuid(),
                Name = TestPrefix + "WORKER" + index,
                FirstName = "Test",
                Company = string.Empty,
                LegalEntity = false,
                Type = EntityTypeEnum.Employee
            });
        }

        await _context.Set<Client>().AddRangeAsync(_workers);
        _scenario = NewRow("SCENARIO", AnalyseScenarioKind.Scenario);
        await _context.Set<AnalyseScenario>().AddAsync(_scenario);
        foreach (var suffix in new[] { VariantA, VariantB, VariantC, VariantD })
        {
            _variants[suffix] = NewRow(suffix, AnalyseScenarioKind.Variant);
            await _context.Set<AnalyseScenario>().AddAsync(_variants[suffix]);
        }

        await _context.SaveChangesAsync();
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task Summary_ReportsDemandFilledAndOpenSlotsPerLinkedVariant()
    {
        var summary = await BuildAsync();

        var a = VariantNamed(summary, VariantA);
        a.VariantId.ShouldBe(_variants[VariantA].Id);
        a.DemandedSlots.ShouldBe(ExpectedDemandedA);
        a.FilledSlots.ShouldBe(ExpectedFilledA);
        a.OpenSlots.ShouldBe(ExpectedDemandedA - ExpectedFilledA);
        var b = VariantNamed(summary, VariantB);
        b.DemandedSlots.ShouldBe(ExpectedDemandedB);
        b.FilledSlots.ShouldBe(0);
        b.OpenSlots.ShouldBe(ExpectedDemandedB);
    }

    [Test]
    public async Task Summary_PeakIsTheDayMaximumOfTheSumOverTheShifts_NotAPerShiftPeak()
    {
        var summary = await BuildAsync();

        var a = VariantNamed(summary, VariantA);
        a.PeakOpenSlots.ShouldBe(ExpectedPeakA, "day two leaves one slot open on the first and three on the second shift");
        a.PeakOpenSlots.ShouldNotBe(SecondShiftSlotsPerDay, "the largest per-shift peak is not the answer");
        a.PeakOpenSlots.ShouldNotBe(FirstShiftSlotsPerDay + SecondShiftSlotsPerDay, "the sum of the per-shift peaks is not the answer");
        a.PeakDate.ShouldBe(FirstDay.AddDays(1));
    }

    [Test]
    public async Task Summary_PeakDateIsTheEarliestDayWhenSeveralDaysReachThePeak()
    {
        var summary = await BuildAsync();

        var b = VariantNamed(summary, VariantB);
        b.PeakOpenSlots.ShouldBe(ExpectedPeakB);
        b.PeakDate.ShouldBe(FirstDay);
    }

    [Test]
    public async Task Summary_FullyStaffedVariantHasNoPeakAndNoPeakDate()
    {
        var summary = await BuildAsync();

        var d = VariantNamed(summary, VariantD);
        d.DemandedSlots.ShouldBe(ExpectedDemandedD);
        d.OpenSlots.ShouldBe(0);
        d.PeakOpenSlots.ShouldBe(0);
        d.PeakDate.ShouldBeNull();
    }

    [Test]
    public async Task Summary_ListsOnlyLinkedVariantsOrderedByName_AndKeepsTheShiftListUnchanged()
    {
        var summary = await BuildAsync();

        summary.Variants.ShouldNotBeNull();
        summary.Variants.Select(v => v.Name).ShouldBe(
            new[] { TestPrefix + VariantA, TestPrefix + VariantB, TestPrefix + VariantD });
        summary.Shifts.Select(s => s.ShiftName).ShouldContain(TestPrefix + PlainShift);
        summary.Shifts.Count.ShouldBe(_keyShifts.Count + 1, "the clone of the unlinked variant and the plain clone stay in Shifts");
        var variantDemand = summary.Variants.Sum(v => v.DemandedSlots);
        summary.DemandedSlots.ShouldBeGreaterThan(variantDemand);
    }

    [Test]
    public async Task Summary_HasNoVariantsWhenTheScenarioCarriesNoLinks()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE analyse_scenario_variants SET is_deleted = true, deleted_time = now() WHERE scenario_token = {0}",
            _scenario.Token);

        var summary = await BuildAsync();

        summary.Variants.ShouldBeNull();
        summary.Shifts.Count.ShouldBe(_keyShifts.Count + 1);
    }

    [Test]
    public async Task Summary_DropsAVariantWhoseKeyShiftIsSoftDeleted()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE shift SET is_deleted = true, deleted_time = now() WHERE id = {0}",
            _keyShifts[VariantB].Id);

        var summary = await BuildAsync();

        summary.Variants.ShouldNotBeNull();
        summary.Variants.Select(v => v.Name).ShouldNotContain(TestPrefix + VariantB);
        summary.Variants.Select(v => v.Name).ShouldContain(TestPrefix + VariantA);
    }

    [Test]
    public async Task Summary_HasNoVariantsAfterTheKeyTokensAreGone()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE shift SET analyse_token = NULL WHERE id = ANY({0})",
            (object)_keyShifts.Values.Select(s => s.Id).ToArray());

        var summary = await BuildAsync();

        summary.Variants.ShouldBeNull();
    }

    private static ScenarioVariantCoverageDto VariantNamed(ScenarioSummaryDto summary, string suffix)
    {
        summary.Variants.ShouldNotBeNull();
        return summary.Variants.Single(v => v.Name == TestPrefix + suffix);
    }

    private async Task<ScenarioSummaryDto> BuildAsync()
    {
        _context.ChangeTracker.Clear();
        var contractProvider = Substitute.For<IClientContractDataProvider>();
        contractProvider
            .GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>());
        var builder = new ScenarioSummaryBuilder(
            _context,
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            AnalyseScenarioTestServiceFactory.Create(_context),
            contractProvider);

        var summary = await builder.BuildAsync(_scenario.Token, CancellationToken.None);

        summary.ShouldNotBeNull();
        return summary;
    }

    private async Task SeedAsync()
    {
        var realShift = NewShift(PlainShift + RealSuffix, null, null, PlainShiftSlotsPerDay);
        foreach (var (shiftSuffix, variantSuffix, slots) in KeyShiftPlan)
        {
            _keyShifts[shiftSuffix] = NewShift(shiftSuffix + KeySuffix, _variants[variantSuffix].Token, null, slots);
        }

        await _context.Shift.AddRangeAsync(_keyShifts.Values.Append(realShift));
        await _context.SaveChangesAsync();

        var clones = new Dictionary<string, Shift>();
        foreach (var (shiftSuffix, _, slots) in KeyShiftPlan)
        {
            clones[shiftSuffix] = NewShift(shiftSuffix, _scenario.Token, _keyShifts[shiftSuffix].Id, slots);
        }

        var plain = NewShift(PlainShift, _scenario.Token, realShift.Id, PlainShiftSlotsPerDay);
        await _context.Shift.AddRangeAsync(clones.Values.Append(plain));
        await _context.Set<AnalyseScenarioVariant>().AddRangeAsync(
            new[] { VariantA, VariantB, VariantD }.Select(suffix => new AnalyseScenarioVariant
            {
                Id = Guid.NewGuid(),
                ScenarioToken = _scenario.Token,
                VariantId = _variants[suffix].Id
            }));
        await _context.SaveChangesAsync();

        await AddWorksAsync(clones[FirstShift], FirstShiftWorksPerDay);
        await AddWorksAsync(clones[SecondShift], SecondShiftWorksPerDay);
        await AddWorksAsync(clones[VariantD], FullShiftWorksPerDay);
    }

    private async Task AddWorksAsync(Shift shift, int[] worksPerDay)
    {
        var works = new List<Work>();
        for (var dayIndex = 0; dayIndex < worksPerDay.Length; dayIndex++)
        {
            var date = FirstDay.AddDays(dayIndex);
            for (var count = 0; count < worksPerDay[dayIndex]; count++)
            {
                var worker = _nextWorkerByDay.GetValueOrDefault(date);
                _nextWorkerByDay[date] = worker + 1;
                works.Add(new Work
                {
                    Id = Guid.NewGuid(),
                    ClientId = _workers[worker].Id,
                    ShiftId = shift.Id,
                    CurrentDate = date,
                    StartTime = new TimeOnly(8, 0),
                    EndTime = new TimeOnly(16, 0),
                    WorkTime = 8m,
                    AnalyseToken = shift.AnalyseToken
                });
            }
        }

        await _context.Work.AddRangeAsync(works);
        await _context.SaveChangesAsync();
    }

    private static Shift NewShift(string suffix, Guid? token, Guid? sourceShiftId, int slotsPerDay) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        Abbreviation = "TST",
        Description = "Variant summary seam",
        Status = ShiftStatus.OriginalShift,
        FromDate = FirstDay,
        UntilDate = LastDay,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        IsMonday = true,
        IsTuesday = true,
        IsWednesday = true,
        IsThursday = true,
        IsFriday = true,
        IsSaturday = true,
        IsSunday = true,
        ShiftType = ShiftType.IsTask,
        Quantity = 1,
        SumEmployees = slotsPerDay,
        WorkTime = 8m,
        AnalyseToken = token,
        ScenarioSourceShiftId = sourceShiftId
    };

    private static AnalyseScenario NewRow(string suffix, AnalyseScenarioKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        GroupId = null,
        Token = Guid.NewGuid(),
        FromDate = FirstDay,
        UntilDate = LastDay,
        Status = AnalyseScenarioStatus.Active,
        Kind = kind,
        CreatedByUser = TestPrefix + "USER"
    };

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM analyse_scenario_variants
                WHERE variant_id IN (SELECT id FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%')
                OR scenario_token IN (SELECT token FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
