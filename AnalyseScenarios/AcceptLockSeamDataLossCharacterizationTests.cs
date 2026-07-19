// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression guard for the Accept-path lock-seam (Phase 0 of the autofill/recovery button pipeline,
/// see docs/knowledge/recovery-autofill-button-pipeline-plan-2026-06-25.md). These tests previously
/// CHARACTERIZED two data-loss bugs (green = bug present); the Phase-0 fix closed both, so they now assert
/// the CORRECTED behaviour and guard against regression:
///
///  1. SCOPE: a scenario with no GroupId scopes the Accept-delete to its OWN footprint (the real shifts its
///     works promote onto), never the whole date window company-wide. -> Test 1.
///  2. LOCK FILTER: SoftDeleteRealWorks skips LockLevel != None, so a Confirmed/Approved/Closed work is never
///     deleted by Accept; an unlocked in-scope work still is. -> Test 2.
///  3. NO DUPLICATION: the lock-skip is symmetric — PromoteScenarioWorksAsync does NOT promote the clone of a
///     locked work (the original survives), so Accept never duplicates a locked work. -> Test 3.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
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
public class AcceptLockSeamDataLossCharacterizationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_LOCKSEAM_";
    private static readonly DateOnly PeriodFrom = new(2099, 6, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 6, 30);
    private static readonly DateOnly WorkDate = new(2099, 6, 5);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _cloneService = null!;

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
        _cloneService = new AnalyseScenarioService(_context);
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
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
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

    private async Task<Shift> CreateShiftAsync(string suffix)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Accept lock-seam regression guard",
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

    private async Task<Group> CreateGroupAsync(string suffix)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = null
        };
        await _context.Set<Group>().AddAsync(group);
        await _context.SaveChangesAsync();
        return group;
    }

    private async Task AddGroupItemAsync(Guid groupId, Guid shiftId)
    {
        await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = shiftId });
        await _context.SaveChangesAsync();
    }

    private async Task<Work> CreateRealWorkAsync(Guid clientId, Guid shiftId, WorkLockLevel lockLevel)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = null,
            LockLevel = lockLevel
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
        return work;
    }

    private async Task<AnalyseScenario> CreateScenarioRowAsync(Guid token, Guid? groupId)
    {
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SCENARIO",
            Token = token,
            GroupId = groupId,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active
        };
        await _context.Set<AnalyseScenario>().AddAsync(scenario);
        await _context.SaveChangesAsync();
        return scenario;
    }

    private async Task InjectScenarioPlacementAsync(Guid clientId, Guid cloneShiftId, Guid token)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = cloneShiftId,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
    }

    private AcceptAnalyseScenarioCommandHandler Handler()
    {
        var repo = new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>());
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        var softening = Substitute.For<IWorkSofteningRepository>();
        var compliance = Substitute.For<Klacks.Api.Application.Interfaces.Schedules.IScenarioComplianceService>();
        compliance
            .EvaluateAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Klacks.Api.Application.DTOs.Schedules.ScenarioComplianceReport([], []));
        return new AcceptAnalyseScenarioCommandHandler(
            repo, _cloneService, unitOfWork, softening,
            compliance,
            Substitute.For<Klacks.Api.Application.Interfaces.Schedules.ISupervisorOverrideAuthorizer>(),
            Substitute.For<Klacks.Api.Application.Interfaces.IScheduleTimelineService>(),
            Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    // BUG 1 FIXED — SCOPE: a null-group scenario about shift X must NOT delete real works on an unrelated
    // shift Y in the same window. Scope is the scenario's own footprint, not the whole window.
    [Test]
    public async Task Accept_NullGroupScenario_DoesNotDelete_UnrelatedRealWork_OutsideFootprint()
    {
        var victim = await CreateClientAsync("VICTIM1");
        var realShift = await CreateShiftAsync("REALSHIFT1");

        var cover = await CreateClientAsync("COVER1");
        var scenarioShift = await CreateShiftAsync("SCENARIOSHIFT1");
        var token = Guid.NewGuid();
        var scenario = await CreateScenarioRowAsync(token, groupId: null);
        var shiftIdMap = await _cloneService.CloneScenarioDataAsync(
            null, PeriodFrom, PeriodUntil, token, new[] { scenarioShift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();
        await InjectScenarioPlacementAsync(cover.Id, shiftIdMap[scenarioShift.Id], token);

        // Created AFTER the clone, on a shift the scenario never touched -> outside the scenario footprint.
        // The old global (GroupId==null) delete would wipe it; the footprint-scoped delete must leave it alone.
        var unrelated = await CreateRealWorkAsync(victim.Id, realShift.Id, WorkLockLevel.None);

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);
        accepted.ShouldBeTrue();

        await using var verify = NewContext();
        var promotedCover = await verify.Work
            .CountAsync(w => w.ClientId == cover.Id && w.CurrentDate == WorkDate && w.AnalyseToken == null);
        promotedCover.ShouldBe(1);

        // FIX 1: the unrelated work on a shift the scenario never touched survives.
        var survived = await verify.Work.IgnoreQueryFilters()
            .CountAsync(w => w.Id == unrelated.Id && !w.IsDeleted);
        survived.ShouldBe(1);
    }

    // BUG 2 FIXED — LOCK FILTER: a group-scoped accept deletes the in-scope UNLOCKED work but preserves the
    // in-scope CONFIRMED-locked work (proving the delete reaches the shift, and the lock filter saved it).
    [Test]
    public async Task Accept_GroupScopedScenario_PreservesLockedWork_DeletesUnlockedInScope()
    {
        var group = await CreateGroupAsync("GROUP2");
        var inScopeShift = await CreateShiftAsync("INSCOPESHIFT2");
        await AddGroupItemAsync(group.Id, inScopeShift.Id);

        var locked = await CreateClientAsync("LOCKED2");
        var lockedWork = await CreateRealWorkAsync(locked.Id, inScopeShift.Id, WorkLockLevel.Confirmed);

        var unlocked = await CreateClientAsync("UNLOCKED2");
        var unlockedWork = await CreateRealWorkAsync(unlocked.Id, inScopeShift.Id, WorkLockLevel.None);

        var token = Guid.NewGuid();
        var scenario = await CreateScenarioRowAsync(token, groupId: group.Id);

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);
        accepted.ShouldBeTrue();

        await using var verify = NewContext();

        // Delete reaches the in-scope shift: the unlocked work is gone.
        var unlockedSurvived = await verify.Work.IgnoreQueryFilters()
            .CountAsync(w => w.Id == unlockedWork.Id && !w.IsDeleted);
        unlockedSurvived.ShouldBe(0);

        // FIX 2: the Confirmed-locked work on the same in-scope shift survives.
        var lockedSurvived = await verify.Work.IgnoreQueryFilters()
            .CountAsync(w => w.Id == lockedWork.Id && !w.IsDeleted);
        lockedSurvived.ShouldBe(1);
    }

    // FIX 3 — NO DUPLICATION: a Confirmed work that the scenario cloned must end up as exactly ONE real work
    // after accept (original preserved by the lock-skipping delete, clone NOT promoted).
    [Test]
    public async Task Accept_DoesNotDuplicate_ClonedLockedWork()
    {
        var group = await CreateGroupAsync("GROUP3");
        var victim = await CreateClientAsync("VICTIM3");
        var realShift = await CreateShiftAsync("REALSHIFT3");
        await AddGroupItemAsync(group.Id, realShift.Id);
        var lockedWork = await CreateRealWorkAsync(victim.Id, realShift.Id, WorkLockLevel.Confirmed);

        var token = Guid.NewGuid();
        var scenario = await CreateScenarioRowAsync(token, groupId: group.Id);
        // Cloning the group data clones the Confirmed work into the token (clone carries LockLevel=Confirmed).
        await _cloneService.CloneScenarioDataWithMapsAsync(
            group.Id, PeriodFrom, PeriodUntil, token, new[] { realShift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);
        accepted.ShouldBeTrue();

        await using var verify = NewContext();

        // Exactly one real (non-deleted, promoted) work for the victim on the source shift — no duplicate.
        var realWorks = await verify.Work.IgnoreQueryFilters()
            .Where(w => w.ClientId == victim.Id && w.ShiftId == realShift.Id && w.CurrentDate == WorkDate
                && w.AnalyseToken == null && !w.IsDeleted)
            .ToListAsync();
        realWorks.Count.ShouldBe(1);
        realWorks[0].Id.ShouldBe(lockedWork.Id);
        realWorks[0].LockLevel.ShouldBe(WorkLockLevel.Confirmed);
    }
}
