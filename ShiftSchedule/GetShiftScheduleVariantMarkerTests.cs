// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of the is_variant_shift column of get_shift_schedule and get_shift_schedule_partial: a scenario clone
/// is marked only while its source shift (scenario_source_shift_id) is live and carries the token of a live variant. The
/// negative cases sit next to the positive one and differ in exactly one fact: a plain clone, a soft-deleted variant, a
/// soft-deleted key shift, a promoted key (token gone) and a source whose token belongs to a scenario instead of a variant.
/// Real-plan rows are never marked. The stored procedures must be loaded on the test database first. Only
/// INTEGRATION_TEST_ rows are written and removed; the day lies in 2099.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.ShiftSchedule;

[TestFixture]
[Category("RealDatabase")]
public class GetShiftScheduleVariantMarkerTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARMARKER_";
    private const string CloneOfLiveKey = "CLONE_LIVE";
    private const string CloneOfPlainShift = "CLONE_PLAIN";
    private const string CloneOfDeletedVariant = "CLONE_DELETED_VARIANT";
    private const string CloneOfDeletedKey = "CLONE_DELETED_KEY";
    private const string CloneOfPromotedKey = "CLONE_PROMOTED";
    private const string CloneOfScenarioSource = "CLONE_SCENARIO_SOURCE";
    private const string RealShift = "REAL";
    private const string KeySuffix = "_SOURCE";
    private static readonly DateOnly Day = new(2099, 11, 3);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ShiftScheduleService _service = null!;
    private Guid _scenarioToken;
    private readonly Dictionary<string, Shift> _shifts = [];

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
        _shifts.Clear();
        _scenarioToken = Guid.NewGuid();
        _service = new ShiftScheduleService(
            _context,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            Substitute.For<ILogger<ShiftScheduleService>>());
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [TestCase(CloneOfLiveKey, true)]
    [TestCase(CloneOfPlainShift, false)]
    [TestCase(CloneOfDeletedVariant, false)]
    [TestCase(CloneOfDeletedKey, false)]
    [TestCase(CloneOfPromotedKey, false)]
    [TestCase(CloneOfScenarioSource, false)]
    public async Task FullSchedule_MarksACloneOnlyWhileItsSourceCarriesALiveVariantToken(string clone, bool expected)
    {
        var rows = await _service.GetShiftScheduleListAsync(Day, Day, analyseToken: _scenarioToken);

        var row = rows.Single(r => r.ShiftId == _shifts[clone].Id);
        row.IsVariantShift.ShouldBe(expected);
    }

    [TestCase(CloneOfLiveKey, true)]
    [TestCase(CloneOfPlainShift, false)]
    [TestCase(CloneOfDeletedVariant, false)]
    [TestCase(CloneOfDeletedKey, false)]
    [TestCase(CloneOfPromotedKey, false)]
    [TestCase(CloneOfScenarioSource, false)]
    public async Task PartialSchedule_MarksACloneOnlyWhileItsSourceCarriesALiveVariantToken(string clone, bool expected)
    {
        var rows = await _service.GetShiftSchedulePartialAsync(
            [(_shifts[clone].Id, Day)], _scenarioToken);

        rows.Count.ShouldBe(1);
        rows[0].IsVariantShift.ShouldBe(expected);
    }

    [Test]
    public async Task FullSchedule_LiveCloneRowsAreMarkedOnEveryDayOfTheRange()
    {
        var rows = await _service.GetShiftScheduleListAsync(Day, Day.AddDays(1), analyseToken: _scenarioToken);

        var liveRows = rows.Where(r => r.ShiftId == _shifts[CloneOfLiveKey].Id).ToList();
        liveRows.Count.ShouldBe(2);
        liveRows.ShouldAllBe(r => r.IsVariantShift);
        rows.Where(r => r.ShiftId == _shifts[CloneOfPlainShift].Id).ShouldAllBe(r => !r.IsVariantShift);
    }

    [Test]
    public async Task FullSchedule_RealPlanRowsAreNeverMarked()
    {
        var rows = await _service.GetShiftScheduleListAsync(Day, Day);

        var real = rows.Single(r => r.ShiftId == _shifts[RealShift].Id);
        real.IsVariantShift.ShouldBeFalse();
        rows.ShouldNotContain(r => r.ShiftId == _shifts[CloneOfLiveKey].Id);
    }

    [Test]
    public async Task PartialSchedule_RealPlanRowsAreNeverMarked()
    {
        var rows = await _service.GetShiftSchedulePartialAsync([(_shifts[RealShift].Id, Day)]);

        rows.Count.ShouldBe(1);
        rows[0].IsVariantShift.ShouldBeFalse();
    }

    private async Task SeedAsync()
    {
        var liveVariant = NewVariant("LIVE_VARIANT", AnalyseScenarioKind.Variant);
        var deletedVariant = NewVariant("DELETED_VARIANT", AnalyseScenarioKind.Variant);
        var scenarioRow = NewVariant("SCENARIO_ROW", AnalyseScenarioKind.Scenario);
        var promotedVariant = NewVariant("PROMOTED_VARIANT", AnalyseScenarioKind.Variant);
        await _context.Set<AnalyseScenario>().AddRangeAsync(liveVariant, deletedVariant, scenarioRow, promotedVariant);

        _shifts[RealShift] = NewShift(RealShift, null, null);
        _shifts[CloneOfLiveKey + KeySuffix] = NewShift(CloneOfLiveKey + KeySuffix, liveVariant.Token, null);
        _shifts[CloneOfDeletedVariant + KeySuffix] = NewShift(CloneOfDeletedVariant + KeySuffix, deletedVariant.Token, null);
        _shifts[CloneOfDeletedKey + KeySuffix] = NewShift(CloneOfDeletedKey + KeySuffix, liveVariant.Token, null);
        _shifts[CloneOfPromotedKey + KeySuffix] = NewShift(CloneOfPromotedKey + KeySuffix, promotedVariant.Token, null);
        _shifts[CloneOfScenarioSource + KeySuffix] = NewShift(CloneOfScenarioSource + KeySuffix, scenarioRow.Token, null);
        await _context.Shift.AddRangeAsync(_shifts.Values.ToList());
        await _context.SaveChangesAsync();

        var sourceByClone = new Dictionary<string, Shift>
        {
            [CloneOfLiveKey] = _shifts[CloneOfLiveKey + KeySuffix],
            [CloneOfPlainShift] = _shifts[RealShift],
            [CloneOfDeletedVariant] = _shifts[CloneOfDeletedVariant + KeySuffix],
            [CloneOfDeletedKey] = _shifts[CloneOfDeletedKey + KeySuffix],
            [CloneOfPromotedKey] = _shifts[CloneOfPromotedKey + KeySuffix],
            [CloneOfScenarioSource] = _shifts[CloneOfScenarioSource + KeySuffix]
        };
        foreach (var (clone, source) in sourceByClone)
        {
            _shifts[clone] = NewShift(clone, _scenarioToken, source.Id);
        }

        await _context.Shift.AddRangeAsync(sourceByClone.Keys.Select(clone => _shifts[clone]));
        await _context.SaveChangesAsync();

        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE analyse_scenarios SET is_deleted = true, deleted_time = now() WHERE id = {0}", deletedVariant.Id);
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE shift SET is_deleted = true, deleted_time = now() WHERE id = {0}",
            _shifts[CloneOfDeletedKey + KeySuffix].Id);
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE shift SET analyse_token = NULL WHERE id = {0}", _shifts[CloneOfPromotedKey + KeySuffix].Id);
        _context.ChangeTracker.Clear();
    }

    private static Shift NewShift(string suffix, Guid? token, Guid? sourceShiftId) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        Abbreviation = "TST",
        Description = "Variant marker",
        Status = ShiftStatus.OriginalShift,
        FromDate = Day,
        UntilDate = Day.AddDays(1),
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
        SumEmployees = 1,
        WorkTime = 8m,
        AnalyseToken = token,
        ScenarioSourceShiftId = sourceShiftId
    };

    private static AnalyseScenario NewVariant(string suffix, AnalyseScenarioKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        GroupId = null,
        Token = Guid.NewGuid(),
        FromDate = Day,
        UntilDate = Day.AddDays(1),
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
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
