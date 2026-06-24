// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// CHARACTERIZATION tests for the Accept-path data-loss seam (Phase-0 blocker of the autofill/recovery
/// button pipeline, see docs/knowledge/recovery-autofill-button-pipeline-plan-2026-06-25.md).
///
/// Accepting an AnalyseScenario runs AnalyseScenarioService.SoftDeleteRealScheduleDataAsync, which has TWO
/// independent data-loss defects. Each is pinned by exactly one test so a partial Phase-0 fix cannot give
/// false confidence:
///
///  1. GLOBAL SCOPE when the scenario has no GroupId: SoftDeleteRealScheduleDataAsync leaves shiftIds == null
///     (:227), so SoftDeleteRealWorks applies NO shift filter (:895) and deletes EVERY real work in the date
///     window company-wide. -> Test 1 (lock state is irrelevant here; an unlocked work dies identically).
///  2. NO LockLevel FILTER even when correctly scoped: SoftDeleteRealWorks (:889-893) never checks LockLevel,
///     so a Confirmed/Approved/Closed work inside the scenario's group scope is silently deleted. -> Test 2.
///
/// Both tests are GREEN while the bugs exist (behaviour documented). The Phase-0 fix is two-part: (a) scope
/// narrowing -> flips Test 1 red; (b) symmetric LockLevel skip on delete AND promote -> flips Test 2 red.
/// When a part lands, invert that test's assertion; it then guards the fix. Mirrors RecoveryWizardLegality-
/// CrossCheckTests (green = documented divergence). A fix that flips only one test means the other bug stands.
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
            Description = "Accept data-loss characterization",
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
        return new AcceptAnalyseScenarioCommandHandler(
            repo, _cloneService, unitOfWork, softening,
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    // BUG 1 — GLOBAL SCOPE: a GroupId == null scenario deletes real works on ANY shift in the window,
    // not just the shifts it touched. Lock state plays no role here (victim is intentionally UNLOCKED).
    [Test]
    public async Task Accept_NullGroupScenario_GloballyDeletes_UnrelatedRealWork_InWindow()
    {
        // An unrelated, real (unlocked) assignment for the victim on its OWN shift.
        var victim = await CreateClientAsync("VICTIM1");
        var realShift = await CreateShiftAsync("REALSHIFT1");
        var unrelated = await CreateRealWorkAsync(victim.Id, realShift.Id, WorkLockLevel.None);

        // A scenario about a DIFFERENT shift, with GroupId == null, covering the same window.
        var cover = await CreateClientAsync("COVER1");
        var scenarioShift = await CreateShiftAsync("SCENARIOSHIFT1");
        var token = Guid.NewGuid();
        var scenario = await CreateScenarioRowAsync(token, groupId: null);
        var shiftIdMap = await _cloneService.CloneScenarioDataAsync(
            null, PeriodFrom, PeriodUntil, token, new[] { scenarioShift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();
        await InjectScenarioPlacementAsync(cover.Id, shiftIdMap[scenarioShift.Id], token);

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);
        accepted.ShouldBeTrue();

        await using var verify = NewContext();
        var promotedCover = await verify.Work
            .CountAsync(w => w.ClientId == cover.Id && w.CurrentDate == WorkDate && w.AnalyseToken == null);
        promotedCover.ShouldBe(1);

        // CHARACTERIZATION (BUG 1): the unrelated real work on a shift the scenario never touched is gone.
        // After scope-narrowing (Phase-0 spec #1) this MUST become survived == 1; invert the assertion then.
        var survived = await verify.Work.IgnoreQueryFilters()
            .CountAsync(w => w.Id == unrelated.Id && !w.IsDeleted);
        survived.ShouldBe(0);
    }

    // BUG 2 — NO LockLevel FILTER: even a correctly group-SCOPED accept deletes a Confirmed-locked work,
    // while an out-of-scope work is correctly spared (proving scope works -> the lock filter is the defect).
    [Test]
    public async Task Accept_GroupScopedScenario_Deletes_ConfirmedLockedWork_ButSparesOutOfScope()
    {
        var group = await CreateGroupAsync("GROUP2");
        var victim = await CreateClientAsync("VICTIM2");
        var inScopeShift = await CreateShiftAsync("INSCOPESHIFT2");
        await AddGroupItemAsync(group.Id, inScopeShift.Id);
        var committedLocked = await CreateRealWorkAsync(victim.Id, inScopeShift.Id, WorkLockLevel.Confirmed);

        // A bystander work on a shift that is NOT in the group -> outside the delete scope.
        var bystander = await CreateClientAsync("BYSTANDER2");
        var outOfScopeShift = await CreateShiftAsync("OUTOFSCOPESHIFT2");
        var bystanderWork = await CreateRealWorkAsync(bystander.Id, outOfScopeShift.Id, WorkLockLevel.None);

        var token = Guid.NewGuid();
        var scenario = await CreateScenarioRowAsync(token, groupId: group.Id);

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);
        accepted.ShouldBeTrue();

        await using var verify = NewContext();

        // Scope works: the out-of-group work survives -> this test is NOT about the scope bug.
        var bystanderSurvived = await verify.Work.IgnoreQueryFilters()
            .CountAsync(w => w.Id == bystanderWork.Id && !w.IsDeleted);
        bystanderSurvived.ShouldBe(1);

        // CHARACTERIZATION (BUG 2): the in-scope CONFIRMED-locked work is deleted only because there is no
        // LockLevel filter. After the symmetric lock-skip (Phase-0 spec #2) this MUST become survived == 1.
        var survived = await verify.Work.IgnoreQueryFilters()
            .CountAsync(w => w.Id == committedLocked.Id && !w.IsDeleted);
        survived.ShouldBe(0);
    }
}
