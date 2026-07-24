// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for chained (nested) scenario clones as produced by the
/// AutoWizard orchestration (Wizard 1 -> Wizard 2 -> Wizard 3), where each stage
/// clones the previous stage's clone shifts. Guards that ScenarioSourceShiftId is
/// resolved to the real root shift (not the immediate intermediate clone), so the
/// single-hop remap in PromoteScenarioWorksAsync lands promoted works on the real
/// shift and they stay visible in the real plan after accept.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
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
public class AnalyseScenarioChainedAcceptTests
{
    private const string TestPrefix = "INTEGRATION_TEST_CHAINED_";
    private static readonly DateOnly PeriodStart = new(2026, 1, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 1, 31);

    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private AnalyseScenarioService _service = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        await using var context = new DataBaseContext(options, mockHttpContextAccessor);
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _service = new AnalyseScenarioService(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateTestClientAsync(string suffix)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Shift> CreateRealShiftAsync(string suffix)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Integration test chained-accept",
            Status = ShiftStatus.OriginalShift,
            FromDate = PeriodStart,
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

    private async Task<Guid> CloneShiftAsync(Guid sourceShiftId, Guid token)
    {
        var idMap = await _service.CloneScenarioDataAsync(
            groupId: null,
            fromDate: PeriodStart,
            untilDate: PeriodEnd,
            token: token,
            additionalShiftIds: new[] { sourceShiftId },
            ct: CancellationToken.None);
        await _context.SaveChangesAsync();
        return idMap[sourceShiftId];
    }

    private async Task<Shift> GetShiftAsync(Guid id) =>
        await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.Id == id)
            .AsNoTracking()
            .SingleAsync();

    [Test]
    public async Task ChainedClone_KeepsScenarioSourceShiftId_PointingAtRealRoot()
    {
        var realShift = await CreateRealShiftAsync("Root1");

        var token1 = Guid.NewGuid();
        var clone1Id = await CloneShiftAsync(realShift.Id, token1);

        var token2 = Guid.NewGuid();
        var clone2Id = await CloneShiftAsync(clone1Id, token2);

        var token3 = Guid.NewGuid();
        var clone3Id = await CloneShiftAsync(clone2Id, token3);

        var clone1 = await GetShiftAsync(clone1Id);
        var clone2 = await GetShiftAsync(clone2Id);
        var clone3 = await GetShiftAsync(clone3Id);

        clone1.ScenarioSourceShiftId.ShouldBe(realShift.Id, "stage-1 clone points at the real shift");
        clone2.ScenarioSourceShiftId.ShouldBe(realShift.Id,
            "stage-2 clone of a clone must resolve back to the real root, not the stage-1 clone");
        clone3.ScenarioSourceShiftId.ShouldBe(realShift.Id,
            "stage-3 clone of a clone-of-a-clone must still resolve to the real root");
    }

    [Test]
    public async Task ChainedScenarioAccept_PromotesWork_OntoRealShift_VisibleInRealPlan()
    {
        var realShift = await CreateRealShiftAsync("Root2");

        var token1 = Guid.NewGuid();
        var clone1Id = await CloneShiftAsync(realShift.Id, token1);

        var token2 = Guid.NewGuid();
        var clone2Id = await CloneShiftAsync(clone1Id, token2);

        var client = await CreateTestClientAsync("Worker2");
        var scenarioWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ShiftId = clone2Id,
            CurrentDate = new DateOnly(2026, 1, 5),
            StartTime = realShift.StartShift,
            EndTime = realShift.EndShift,
            WorkTime = 8m,
            AnalyseToken = token2
        };
        await _context.Work.AddAsync(scenarioWork);
        await _context.SaveChangesAsync();

        await _service.PromoteScenarioWorksAsync(token2, PeriodStart, PeriodEnd, CancellationToken.None);
        await _context.SaveChangesAsync();

        var promotedWork = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == scenarioWork.Id)
            .AsNoTracking()
            .SingleAsync();

        promotedWork.AnalyseToken.ShouldBeNull("accepted work must be real");
        promotedWork.ShiftId.ShouldBe(realShift.Id,
            "chained accept must remap the promoted work onto the real root shift, not an intermediate clone");

        var referencedShift = await GetShiftAsync(promotedWork.ShiftId);
        referencedShift.AnalyseToken.ShouldBeNull("promoted work must reference a real (token-free) shift");
        referencedShift.IsDeleted.ShouldBeFalse("the referenced real shift must not be soft-deleted");

        var finalClone = await GetShiftAsync(clone2Id);
        finalClone.IsDeleted.ShouldBeTrue("final clone shift must be soft-deleted after accept");
    }

    [Test]
    public async Task ChainedScenarioAccept_DoesNotThrowFalseConflict_WhenRootSubtreeUnchanged()
    {
        var realShift = await CreateRealShiftAsync("Root3");

        var realChild = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "Child_Root3",
            Abbreviation = "TST",
            Description = "Pre-existing real subtree",
            Status = ShiftStatus.SplitShift,
            FromDate = realShift.FromDate,
            StartShift = realShift.StartShift,
            EndShift = realShift.EndShift,
            ShiftType = realShift.ShiftType,
            ParentId = realShift.Id,
            RootId = realShift.Id,
            AnalyseToken = null,
            ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(realChild);
        await _context.SaveChangesAsync();

        var token1 = Guid.NewGuid();
        var clone1Id = await CloneShiftAsync(realShift.Id, token1);

        var token2 = Guid.NewGuid();
        var clone2Id = await CloneShiftAsync(clone1Id, token2);

        var clone2 = await GetShiftAsync(clone2Id);
        clone2.SourceChildCountSnapshot.ShouldBe(1,
            "the snapshot must count the real root's children, keyed on the same root the source id resolves to");

        await Should.NotThrowAsync(async () =>
            await _service.ValidateNoAcceptConflictsAsync(token2, CancellationToken.None));
    }

    [Test]
    public async Task ChainedFinalAccept_InAcceptOrder_ShowsAllWorksOnRealShift()
    {
        var realShift = await CreateRealShiftAsync("Root4");
        var client = await CreateTestClientAsync("Worker4");

        var outsideWorkId = Guid.NewGuid();
        await _context.Work.AddAsync(new Work
        {
            Id = outsideWorkId,
            ClientId = client.Id,
            ShiftId = realShift.Id,
            CurrentDate = new DateOnly(2025, 11, 5),
            StartTime = realShift.StartShift,
            EndTime = realShift.EndShift,
            WorkTime = 8m,
            AnalyseToken = null
        });

        var replacedWorkId = Guid.NewGuid();
        await _context.Work.AddAsync(new Work
        {
            Id = replacedWorkId,
            ClientId = client.Id,
            ShiftId = realShift.Id,
            CurrentDate = new DateOnly(2026, 1, 10),
            StartTime = realShift.StartShift,
            EndTime = realShift.EndShift,
            WorkTime = 8m,
            AnalyseToken = null
        });
        await _context.SaveChangesAsync();

        var token1 = Guid.NewGuid();
        var clone1Id = await CloneShiftAsync(realShift.Id, token1);
        var token2 = Guid.NewGuid();
        var clone2Id = await CloneShiftAsync(clone1Id, token2);

        var scenarioWorkId = Guid.NewGuid();
        await _context.Work.AddAsync(new Work
        {
            Id = scenarioWorkId,
            ClientId = client.Id,
            ShiftId = clone2Id,
            CurrentDate = new DateOnly(2026, 1, 5),
            StartTime = realShift.StartShift,
            EndTime = realShift.EndShift,
            WorkTime = 8m,
            AnalyseToken = token2
        });
        await _context.SaveChangesAsync();

        await _service.ValidateNoAcceptConflictsAsync(token2, CancellationToken.None);
        await _service.SoftDeleteRealScheduleDataAsync(null, token2, PeriodStart, PeriodEnd, CancellationToken.None);
        await _service.PromoteScenarioWorksAsync(token2, PeriodStart, PeriodEnd, CancellationToken.None);
        await _context.SaveChangesAsync();

        var outsideWork = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == outsideWorkId).AsNoTracking().SingleAsync();
        outsideWork.IsDeleted.ShouldBeFalse("real work outside the accept period must survive");
        outsideWork.ShiftId.ShouldBe(realShift.Id);

        var replacedWork = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == replacedWorkId).AsNoTracking().SingleAsync();
        replacedWork.IsDeleted.ShouldBeTrue(
            "real work in the period on the real root must be soft-deleted so the promoted scenario does not double-book");

        var promotedWork = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == scenarioWorkId).AsNoTracking().SingleAsync();
        promotedWork.IsDeleted.ShouldBeFalse();
        promotedWork.AnalyseToken.ShouldBeNull("promoted chained work must be real");
        promotedWork.ShiftId.ShouldBe(realShift.Id,
            "the full accept sequence must land the chained work on the real root shift, visible in the real plan");

        var sourceAfter = await GetShiftAsync(realShift.Id);
        sourceAfter.IsDeleted.ShouldBeFalse("real root shift must remain after accept");
        sourceAfter.AnalyseToken.ShouldBeNull();
    }
}
