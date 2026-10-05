// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Regression guard for the shift set of a cloned scenario. A group-scoped clone must carry EVERY shift
/// of the group, not only the shifts the caller lists in additionalShiftIds: the Harmonizer/Holistic apply
/// (AutoWizard stages 2 and 3) passes only the shifts that occur in its bitmap, and a shift without a
/// single work (Spaetdienst/Nachtdienst of a group planned with Fruehdienst only) used to vanish from the
/// scenario. The listed ids are an addition to the group set; a clone of a clone supersedes its real root,
/// so no logical shift is ever cloned twice.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class AnalyseScenarioGroupShiftSetCloneTests
{
    private const string TestPrefix = "INTEGRATION_TEST_SHIFTSET_";
    private static readonly DateOnly PeriodFrom = new(2099, 8, 6);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 10);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        _context = NewContext();
        _service = new AnalyseScenarioService(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

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
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Shift> CreateShiftAsync(string suffix)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Group shift set clone guard",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            AnalyseToken = null,
            ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    private async Task<Group> CreateGroupWithShiftsAsync(params Shift[] shifts)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "Group",
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = null
        };
        await _context.Set<Group>().AddAsync(group);
        foreach (var shift in shifts)
        {
            await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = group.Id, ShiftId = shift.Id });
        }

        await _context.SaveChangesAsync();
        return group;
    }

    private async Task<List<Shift>> LoadClonesAsync(Guid token) =>
        await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.AnalyseToken == token && !s.IsDeleted)
            .AsNoTracking()
            .ToListAsync();

    [Test]
    public async Task GroupClone_WithListedShiftOnly_StillClonesEveryShiftOfTheGroup()
    {
        var withWork = await CreateShiftAsync("Frueh");
        var noWorkA = await CreateShiftAsync("Spaet");
        var noWorkB = await CreateShiftAsync("Nacht");
        var group = await CreateGroupWithShiftsAsync(withWork, noWorkA, noWorkB);

        var token = Guid.NewGuid();
        var idMap = await _service.CloneScenarioDataAsync(
            group.Id, PeriodFrom, PeriodUntil, token, new[] { withWork.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clones = await LoadClonesAsync(token);
        clones.Select(c => c.ScenarioSourceShiftId).ShouldBe(
            new Guid?[] { withWork.Id, noWorkA.Id, noWorkB.Id }, ignoreOrder: true,
            "the scenario must keep Spaetdienst and Nachtdienst although the caller listed only Fruehdienst");
        idMap.Keys.ShouldBe(new[] { withWork.Id, noWorkA.Id, noWorkB.Id }, ignoreOrder: true);
    }

    [Test]
    public async Task ChainedGroupClone_ListingOneStageOneClone_ClonesEachLogicalShiftExactlyOnce()
    {
        var withWork = await CreateShiftAsync("ChainFrueh");
        var noWorkA = await CreateShiftAsync("ChainSpaet");
        var noWorkB = await CreateShiftAsync("ChainNacht");
        var group = await CreateGroupWithShiftsAsync(withWork, noWorkA, noWorkB);

        var token1 = Guid.NewGuid();
        var stageOneMap = await _service.CloneScenarioDataAsync(
            group.Id, PeriodFrom, PeriodUntil, token1, null, CancellationToken.None);
        await _context.SaveChangesAsync();

        var token2 = Guid.NewGuid();
        var stageTwoMap = await _service.CloneScenarioDataAsync(
            group.Id, PeriodFrom, PeriodUntil, token2, new[] { stageOneMap[withWork.Id] }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clones = await LoadClonesAsync(token2);
        clones.Count.ShouldBe(3, "three logical shifts, no duplicate from the real root next to its stage-one clone");
        clones.Select(c => c.ScenarioSourceShiftId).ShouldBe(
            new Guid?[] { withWork.Id, noWorkA.Id, noWorkB.Id }, ignoreOrder: true,
            "every clone resolves to its real root shift");
        stageTwoMap.ContainsKey(stageOneMap[withWork.Id]).ShouldBeTrue(
            "the bitmap's stage-one shift id must map to a clone, or its works are skipped on apply");
        stageTwoMap.ContainsKey(withWork.Id).ShouldBeFalse(
            "the real root is superseded by the stage-one clone and must not be cloned a second time");
    }

    [Test]
    public async Task GroupClone_ListedShiftOutsideTheGroup_IsClonedAsWell()
    {
        var inGroup = await CreateShiftAsync("InGroup");
        var outside = await CreateShiftAsync("Outside");
        var group = await CreateGroupWithShiftsAsync(inGroup);

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(
            group.Id, PeriodFrom, PeriodUntil, token, new[] { outside.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clones = await LoadClonesAsync(token);
        clones.Select(c => c.ScenarioSourceShiftId).ShouldBe(
            new Guid?[] { inGroup.Id, outside.Id }, ignoreOrder: true,
            "a listed shift without a membership in the group is guaranteed, the group's own shifts stay");
    }

    [Test]
    public async Task GrouplessClone_WithListedShift_ClonesExactlyTheListedShift()
    {
        var listed = await CreateShiftAsync("Listed");
        await CreateShiftAsync("NotListed");

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(
            null, PeriodFrom, PeriodUntil, token, new[] { listed.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clones = await LoadClonesAsync(token);
        clones.Select(c => c.ScenarioSourceShiftId).ShouldBe(new Guid?[] { listed.Id },
            "without a group the listed ids stay the exact set (chained clones rely on it)");
    }
}
