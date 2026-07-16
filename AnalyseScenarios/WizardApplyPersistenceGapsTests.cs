// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Phase 1: closes the two unasserted Wizard-1 (Planner) APPLY persistence gaps in
/// <see cref="WizardApplyService"/> against the real test DB (port 5434):
/// (1) the `.Where(t => !t.IsLocked)` filter on the Apply -> Work path must materialise ONLY
///     non-locked tokens; the locked token must produce no Work.
/// (2) PersistEscalationsAsync/MapEscalations must persist exactly the valid escalation as a
///     work_softening row with the correctly mapped <see cref="SofteningKind"/> (MapKind), while
///     dropping an escalation whose date is OUTSIDE the applied period and one whose agent is NOT
///     among the applied works.
/// These tests drive the real ApplyAsScenarioAsync with a mediator whose BulkAddWorks actually
/// inserts works, and the real WorkSofteningRepository so escalation rows truly persist.
/// Far-future dates (2099) isolate the rows; cleanup runs in teardown.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using Work = Klacks.Api.Domain.Models.Schedules.Work;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class WizardApplyPersistenceGapsTests
{
    private const string TestPrefix = "INTEGRATION_TEST_WIZGAPS_";
    private static readonly DateOnly WorkDate = new(2099, 9, 8);
    private static readonly DateOnly OutOfPeriodDate = WorkDate.AddDays(30);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _scenarioService = null!;

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
        _scenarioService = new AnalyseScenarioService(_context);
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
            DELETE FROM work_softening WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%')
                OR shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%')
                OR shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var c = new Client { Id = Guid.NewGuid(), Name = TestPrefix + suffix, FirstName = "Test", Company = string.Empty, LegalEntity = false };
        await _context.Set<Client>().AddAsync(c);
        await _context.SaveChangesAsync();
        return c;
    }

    private async Task<Shift> CreateShiftAsync(string suffix)
    {
        var s = new Shift
        {
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, Abbreviation = "TST", Description = "Persistence-gap test",
            Status = ShiftStatus.OriginalShift, FromDate = new DateOnly(2099, 1, 1), UntilDate = null,
            StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            IsSaturday = true, IsSunday = true,
            ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 8m,
            AnalyseToken = null, ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(s);
        await _context.SaveChangesAsync();
        return s;
    }

    private async Task<Group> CreateGroupAsync(string suffix)
    {
        var g = new Group
        {
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), Parent = null,
        };
        await _context.Set<Group>().AddAsync(g);
        await _context.SaveChangesAsync();
        return g;
    }

    private async Task AddGroupItemAsync(Guid groupId, Guid shiftId)
    {
        await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = shiftId });
        await _context.SaveChangesAsync();
    }

    private static CoreToken Token(Guid shiftId, Guid agentId, bool isLocked) => new(
        WorkIds: [], ShiftTypeIndex: 0, Date: WorkDate, TotalHours: 8m,
        StartAt: WorkDate.ToDateTime(new TimeOnly(8, 0)), EndAt: WorkDate.ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(), PositionInBlock: 0, IsLocked: isLocked, LocationContext: null,
        ShiftRefId: shiftId, AgentId: agentId.ToString());

    // Builds the real WizardApplyService with a mediator whose BulkAddWorks inserts works on the shared
    // context, and the REAL WorkSofteningRepository so escalation rows actually persist and are observable.
    private WizardApplyService CreateApplyService(WizardResultCache cache)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkAddWorksCommand>()).Returns(async call =>
        {
            var cmd = call.Arg<BulkAddWorksCommand>();
            var ids = new List<Guid>();
            foreach (var item in cmd.Request.Works)
            {
                var w = new Work
                {
                    Id = Guid.NewGuid(), ClientId = item.ClientId, ShiftId = item.ShiftId, CurrentDate = item.CurrentDate,
                    StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), WorkTime = item.WorkTime,
                    Surcharges = item.Surcharges, LockLevel = WorkLockLevel.None, AnalyseToken = item.AnalyseToken
                };
                await _context.Work.AddAsync(w);
                ids.Add(w.Id);
            }
            await _context.SaveChangesAsync();
            return new BulkWorksResponse { CreatedIds = ids };
        });

        var scenarioRepo = Substitute.For<IAnalyseScenarioRepository>();
        scenarioRepo.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<AnalyseScenario>());
        var softeningRepo = new WorkSofteningRepository(_context);
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());

        return new WizardApplyService(
            cache,
            mediator,
            scenarioRepo,
            _scenarioService,
            unitOfWork,
            softeningRepo,
            Substitute.For<IWizardRunCaptureRepository>(),
            Substitute.For<ILogger<WizardApplyService>>());
    }

    [Test, Explicit("Read/write W1 apply locked-token materialisation against the real test DB (port 5434); cleans up after itself.")]
    public async Task Apply_MaterializesOnlyNonLockedTokens()
    {
        var planned = await CreateClientAsync("PLANNED");
        var lockedAgent = await CreateClientAsync("LOCKED");
        var shiftPlanned = await CreateShiftAsync("SP");
        var shiftLocked = await CreateShiftAsync("SL");
        var group = await CreateGroupAsync("G");
        await AddGroupItemAsync(group.Id, shiftPlanned.Id);
        await AddGroupItemAsync(group.Id, shiftLocked.Id);

        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        cache.Store(jobId, new CoreScenario
        {
            Id = "s",
            Tokens =
            [
                Token(shiftPlanned.Id, planned.Id, isLocked: false),
                Token(shiftLocked.Id, lockedAgent.Id, isLocked: true),
            ]
        }, analyseToken: null);

        var (resource, createdIds) = await CreateApplyService(cache).ApplyAsScenarioAsync(jobId, group.Id, CancellationToken.None);

        createdIds.Count.ShouldBe(1, "only the non-locked token may be materialised as a Work; the locked token must be skipped by the `.Where(t => !t.IsLocked)` filter.");

        var works = await _context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == resource.Token && w.CurrentDate == WorkDate && !w.IsDeleted)
            .ToListAsync();

        works.Count.ShouldBe(1, $"exactly one Work must exist for the applied scenario; found {works.Count}.");
        works.Single().ClientId.ShouldBe(planned.Id, "the surviving Work must belong to the non-locked token's agent.");

        var lockedWorks = await _context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == resource.Token && w.ClientId == lockedAgent.Id && !w.IsDeleted)
            .ToListAsync();
        lockedWorks.Count.ShouldBe(0, "the locked token must NOT be materialised as a Work.");
    }

    [Test, Explicit("Read/write W1 apply escalation persistence against the real test DB (port 5434); cleans up after itself.")]
    public async Task Apply_PersistsEscalations_WithCorrectKindMapping_AndFilters()
    {
        var applied = await CreateClientAsync("APPLIED");
        var notApplied = await CreateClientAsync("NOTAPPLIED");
        var shift = await CreateShiftAsync("S");
        var group = await CreateGroupAsync("G");
        await AddGroupItemAsync(group.Id, shift.Id);

        // Use a rule name whose mapped kind differs from the name, so the assertion genuinely exercises MapKind.
        const string RuleName = "MaxConsecutiveDays";
        var expectedKind = SofteningKind.MaxConsecutiveWorkDays;

        var dateValid = WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dateOutOfPeriod = OutOfPeriodDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var escalations = new List<WizardEscalationDto>
        {
            // (a) valid: applied agent, in-period date -> must persist
            new(applied.Id.ToString(), dateValid, RuleName, "valid escalation"),
            // (b) date OUTSIDE the applied period (period == [WorkDate, WorkDate]) -> dropped
            new(applied.Id.ToString(), dateOutOfPeriod, RuleName, "out-of-period escalation"),
            // (c) agent NOT among the applied works -> dropped
            new(notApplied.Id.ToString(), dateValid, RuleName, "non-applied-agent escalation"),
        };

        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        cache.Store(jobId, new CoreScenario
        {
            Id = "s",
            Tokens = [Token(shift.Id, applied.Id, isLocked: false)]
        }, analyseToken: null, escalations: escalations);

        var (resource, _) = await CreateApplyService(cache).ApplyAsScenarioAsync(jobId, group.Id, CancellationToken.None);

        var softenings = await _context.WorkSoftening.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.AnalyseToken == resource.Token && !s.IsDeleted)
            .ToListAsync();

        softenings.Count.ShouldBe(1, $"exactly one escalation (the valid one) must persist; (b) out-of-period and (c) non-applied-agent must be dropped. Found {softenings.Count}.");

        var row = softenings.Single();
        row.ClientId.ShouldBe(applied.Id, "the persisted softening must belong to the applied agent.");
        row.CurrentDate.ShouldBe(WorkDate, "the persisted softening must carry the in-period date.");
        row.Kind.ShouldBe(expectedKind, "MapKind must map 'MaxConsecutiveDays' -> SofteningKind.MaxConsecutiveWorkDays.");
        row.RuleName.ShouldBe(RuleName, "the original rule name must be preserved.");

        var droppedAgentRows = await _context.WorkSoftening.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.ClientId == notApplied.Id && !s.IsDeleted)
            .ToListAsync();
        droppedAgentRows.Count.ShouldBe(0, "an escalation for an agent NOT among the applied works must be dropped.");

        var droppedDateRows = await _context.WorkSoftening.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.CurrentDate == OutOfPeriodDate && !s.IsDeleted)
            .ToListAsync();
        droppedDateRows.Count.ShouldBe(0, "an escalation whose date is OUTSIDE the applied period must be dropped.");
    }
}
