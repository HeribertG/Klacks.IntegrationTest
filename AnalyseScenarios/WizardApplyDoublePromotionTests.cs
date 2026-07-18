// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Grill 2026-06-19 defect H2: WizardApplyService.ApplyAsScenarioAsync cloned the existing movable real
/// works into the new scenario token AND bulk-added the planner's tokens for the same (shift, date)
/// cells, with no delete/dedup. On accept both are promoted -> over-staffing + doubled hours. The fix
/// removes the cloned movable works on exactly the (shift, date) slots the planner fills, while leaving
/// works on slots outside the run untouched (the wizard run can cover a narrower agent/shift set than
/// the group-scoped clone). These tests drive the real ApplyAsScenarioAsync against the test DB (5434)
/// with a mediator whose BulkAddWorks actually inserts works. Far-future dates (2099) isolate the rows.
/// </summary>

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
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
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
public class WizardApplyDoublePromotionTests
{
    private const string TestPrefix = "INTEGRATION_TEST_WIZAPPLY_";
    private static readonly DateOnly PeriodFrom = new(2099, 9, 6);
    private static readonly DateOnly PeriodUntil = new(2099, 9, 10);
    private static readonly DateOnly WorkDate = new(2099, 9, 8);

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
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%')
                OR shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%')
                OR shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
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
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, Abbreviation = "TST", Description = "Double-promotion test",
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

    private async Task CreateWorkAsync(Guid clientId, Guid shiftId, DateOnly date)
    {
        var w = new Work
        {
            Id = Guid.NewGuid(), ClientId = clientId, ShiftId = shiftId, CurrentDate = date,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), WorkTime = 8m,
            LockLevel = WorkLockLevel.None, AnalyseToken = null
        };
        await _context.Work.AddAsync(w);
        await _context.SaveChangesAsync();
    }

    private static CoreToken PlannerToken(Guid shiftId, Guid agentId) => new(
        WorkIds: [], ShiftTypeIndex: 0, Date: WorkDate, TotalHours: 8m,
        StartAt: WorkDate.ToDateTime(new TimeOnly(8, 0)), EndAt: WorkDate.ToDateTime(new TimeOnly(16, 0)),
        BlockId: Guid.NewGuid(), PositionInBlock: 0, IsLocked: false, LocationContext: null,
        ShiftRefId: shiftId, AgentId: agentId.ToString());

    // Builds the real WizardApplyService with a mediator whose BulkAddWorks inserts works on the shared
    // context, so the end-to-end DB state (clone + planner work, minus deletions) is observable.
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
        var softeningRepo = Substitute.For<IWorkSofteningRepository>();
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

    [Test, Explicit("Read/write W1 apply double-promotion against the real test DB (port 5434); cleans up after itself.")]
    public async Task ApplyAsScenario_MustNotDoubleBook_TheExistingMovableWork()
    {
        var incumbent = await CreateClientAsync("INCUMBENT");
        var planned = await CreateClientAsync("PLANNED");
        var shift = await CreateShiftAsync("S");
        var group = await CreateGroupAsync("G");
        await AddGroupItemAsync(group.Id, shift.Id);
        await CreateWorkAsync(incumbent.Id, shift.Id, WorkDate);

        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [PlannerToken(shift.Id, planned.Id)] }, analyseToken: null);

        var (resource, _) = await CreateApplyService(cache).ApplyAsScenarioAsync(jobId, group.Id, CancellationToken.None);

        var worksInSlot = await _context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == resource.Token && w.CurrentDate == WorkDate && !w.IsDeleted)
            .ToListAsync();

        worksInSlot.Count.ShouldBe(1,
            "after applying the wizard plan, the (shift, date) slot must hold exactly ONE work (the planner's " +
            "assignment); the cloned incumbent work must be removed, not left to double-book the slot (grill H2). " +
            $"Found {worksInSlot.Count} works for clients [{string.Join(", ", worksInSlot.Select(w => w.ClientId))}].");
    }

    [Test, Explicit("Read/write W1 apply scope-preservation against the real test DB (port 5434); cleans up after itself.")]
    public async Task ApplyAsScenario_PreservesMovableWorkOnASlotThePlannerDidNotPlan()
    {
        var incumbent = await CreateClientAsync("INC2");
        var planned = await CreateClientAsync("PLN2");
        var bystander = await CreateClientAsync("BYS2");
        var shiftPlanned = await CreateShiftAsync("SP");
        var shiftOther = await CreateShiftAsync("SO");
        var group = await CreateGroupAsync("G2");
        await AddGroupItemAsync(group.Id, shiftPlanned.Id);
        await AddGroupItemAsync(group.Id, shiftOther.Id);
        await CreateWorkAsync(incumbent.Id, shiftPlanned.Id, WorkDate);   // on the planned slot
        await CreateWorkAsync(bystander.Id, shiftOther.Id, WorkDate);     // on a slot OUTSIDE the run

        // The planner only plans shiftPlanned (reassigns it to 'planned'); shiftOther is not in the run.
        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        cache.Store(jobId, new CoreScenario { Id = "s", Tokens = [PlannerToken(shiftPlanned.Id, planned.Id)] }, analyseToken: null);

        var (resource, _) = await CreateApplyService(cache).ApplyAsScenarioAsync(jobId, group.Id, CancellationToken.None);

        var bystanderWorks = await _context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == resource.Token && w.ClientId == bystander.Id && !w.IsDeleted)
            .ToListAsync();
        bystanderWorks.Count.ShouldBe(1,
            "a movable work on a (shift, date) slot the planner did NOT plan must be preserved into the scenario; " +
            "deleting it would drop it on accept (the over-broad delete the grill caught).");

        var incumbentWorks = await _context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == resource.Token && w.ClientId == incumbent.Id && !w.IsDeleted)
            .ToListAsync();
        incumbentWorks.Count.ShouldBe(0, "the incumbent on the replanned slot is replaced by the planner's work");

        var plannedSlotWorks = await _context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == resource.Token && w.CurrentDate == WorkDate && w.ClientId == planned.Id && !w.IsDeleted)
            .ToListAsync();
        plannedSlotWorks.Count.ShouldBe(1, "the planner's assignment materialises exactly once on the planned slot");
    }
}
