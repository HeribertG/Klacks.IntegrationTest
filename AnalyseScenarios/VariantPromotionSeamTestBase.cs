// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Fixture of VariantPromotionSeamTests: the real create, accept and reject handlers, the real cut facade and the real
/// scenario collaborators on one DataBaseContext, plus the builders of key families, scenarios and works. Only
/// INTEGRATION_TEST_ rows are written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

public abstract class VariantPromotionSeamTestBase
{
    protected const string TestPrefix = "INTEGRATION_TEST_VARIANTPROMO_";
    protected const string NumberingFailure = "numbering failed";
    protected const decimal ExpenseAmount = 12.5m;
    protected const decimal WorkHours = 8m;
    protected const int SentinelLft = 7;
    protected const int SentinelRgt = 8;
    protected const int LeafWidth = 1;
    protected const int ParentWidth = 3;
    protected const int FirstLeft = 1;
    protected const int ExpectedRealWorks = 2;
    protected static readonly DateOnly PeriodFrom = new(2099, 10, 1);
    protected static readonly DateOnly PeriodUntil = new(2099, 10, 31);
    protected static readonly DateOnly WizardUntil = new(2099, 10, 7);
    protected static readonly DateOnly ChildUntil = new(2099, 10, 7);
    protected static readonly DateOnly FirstPieceUntil = new(2099, 10, 15);
    protected static readonly DateOnly SecondPieceFrom = new(2099, 10, 16);
    protected static readonly DateOnly SecondChildUntil = new(2099, 10, 20);
    protected static readonly DateOnly WorkDate = new(2099, 10, 5);
    protected static readonly DateOnly SecondWorkDate = new(2099, 10, 6);

    protected string _connectionString = null!;
    protected DataBaseContext _context = null!;
    protected ServiceProvider _provider = null!;
    protected IAnalyseScenarioService _scenarioService = null!;
    protected ShiftRepository _shiftRepository = null!;
    protected ScheduleMapper _mapper = null!;
    protected ShiftCutFacade _facade = null!;
    protected CreateAnalyseScenarioCommandHandler _createHandler = null!;
    protected Group _group = null!;
    protected Client _customer = null!;
    protected Client _worker = null!;

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
        _provider = AnalyseScenarioTestServiceFactory.CreateProvider(_context);
        _scenarioService = _provider.GetRequiredService<IAnalyseScenarioService>();
        _mapper = new ScheduleMapper();
        var shiftValidator = new ShiftValidator();
        _shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            new ShiftQueryPipelineService(
                new DateRangeFilterService(),
                new ShiftSearchService(),
                new ShiftSortingService(),
                new ShiftStatusFilterService(),
                new ShiftPaginationService()),
            new ShiftGroupManagementService(_context, Substitute.For<ILogger<ShiftGroupManagementService>>()),
            new EntityCollectionUpdateService(_context),
            shiftValidator,
            _mapper,
            TestCompanyClock.Utc());

        var stagingUnitOfWork = Substitute.For<IUnitOfWork>();
        stagingUnitOfWork.CompleteAsync().Returns(_ => _context.SaveChangesAsync());
        var groupGuard = Substitute.For<IGroupVisibilityGuard>();
        groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);
        groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var variantRepository = new ScenarioVariantRepository(_context);
        var writeGuard = new VariantShiftWriteGuard(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            variantRepository,
            _scenarioService,
            groupGuard);
        var visibility = Substitute.For<IShiftVisibilityGuard>();
        visibility.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        visibility.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>()).Returns(true);

        _facade = new ShiftCutFacade(
            _shiftRepository,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            new ShiftTreeService(_context, Substitute.For<ILogger<ShiftTreeService>>()),
            ShiftFamilyResetFactory.Create(_context, _shiftRepository, new ShiftTreeService(_context, Substitute.For<ILogger<ShiftTreeService>>()), visibility, stagingUnitOfWork),
            shiftValidator,
            _mapper,
            stagingUnitOfWork,
            visibility,
            new ShiftExpensesRepository(_context, Substitute.For<ILogger<ShiftExpenses>>()),
            writeGuard,
            Substitute.For<ILogger<ShiftCutFacade>>());
        _createHandler = new CreateAnalyseScenarioCommandHandler(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            variantRepository,
            _scenarioService,
            stagingUnitOfWork,
            groupGuard,
            Substitute.For<ILogger<CreateAnalyseScenarioCommandHandler>>());

        _group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "GROUP",
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _customer = NewClient("CUSTOMER");
        _worker = NewClient("WORKER");
        _worker.Type = EntityTypeEnum.Employee;
        await _context.Set<Group>().AddAsync(_group);
        await _context.Set<Client>().AddRangeAsync(_customer, _worker);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _provider.DisposeAsync();
        await _context.DisposeAsync();
    }

    protected static void AssertSameValues(Shift promoted, Shift real)
    {
        promoted.Lft.ShouldNotBeNull();
        promoted.Rgt.ShouldNotBeNull();
        promoted.Lft.ShouldBe(real.Lft);
        promoted.Rgt.ShouldBe(real.Rgt);
    }

    protected async Task AssertRealWorkOnAsync(Guid workId, Guid shiftId)
    {
        _context.ChangeTracker.Clear();
        var work = await _context.Work.AsNoTracking().SingleAsync(w => w.Id == workId);
        work.AnalyseToken.ShouldBeNull();
        work.ShiftId.ShouldBe(shiftId);
        work.IsDeleted.ShouldBeFalse();
    }

    protected ServiceProvider FailingTreeProvider()
    {
        var failingTree = Substitute.For<IShiftTreeService>();
        failingTree.RecalculateTreesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException(NumberingFailure)));
        return AnalyseScenarioTestServiceFactory.CreateProvider(_context, shiftTreeService: failingTree);
    }

    protected async Task<AcceptAnalyseScenarioResult> AcceptAsync(
        Guid scenarioId,
        IAnalyseScenarioService? scenarioService = null,
        bool overrideBlock = false,
        bool overrideStale = false)
    {
        var result = await AcceptHandler(scenarioService ?? _scenarioService).Handle(
            new AcceptAnalyseScenarioCommand(scenarioId, OverrideBlock: overrideBlock, OverrideStale: overrideStale),
            CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    /// <param name="scenarioService">Scenario collaborators of the accept</param>
    /// <param name="context">Context of the accept; the fixture's one when omitted</param>
    /// <param name="afterTheGates">Runs after the gates passed and before anything is staged, to let a rival accept in</param>
    protected AcceptAnalyseScenarioCommandHandler AcceptHandler(
        IAnalyseScenarioService scenarioService, DataBaseContext? context = null, Func<Task>? afterTheGates = null)
    {
        var acceptContext = context ?? _context;
        var compliance = Substitute.For<IScenarioComplianceService>();
        compliance
            .EvaluateAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<ScenarioComplianceReport>>(async _ =>
            {
                if (afterTheGates != null)
                {
                    await afterTheGates();
                }

                return new ScenarioComplianceReport([], []);
            });
        var conditionRepository = new Klacks.Api.Infrastructure.Repositories.Assistant.AgentConditionRepository(acceptContext);
        return new AcceptAnalyseScenarioCommandHandler(
            new AnalyseScenarioRepository(acceptContext, Substitute.For<ILogger<AnalyseScenario>>()),
            scenarioService,
            new UnitOfWork(acceptContext, Substitute.For<ILogger<UnitOfWork>>()),
            Substitute.For<IWorkSofteningRepository>(),
            compliance,
            Substitute.For<ISupervisorOverrideAuthorizer>(),
            Substitute.For<IScheduleTimelineService>(),
            conditionRepository,
            new Klacks.Api.Application.Services.Assistant.Conditions.AgentConditionLedgerService(
                conditionRepository,
                TimeProvider.System,
                Substitute.For<ILogger<Klacks.Api.Application.Services.Assistant.Conditions.AgentConditionLedgerService>>()),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IPeriodHoursService>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IEscalationChainService>(),
            UnrestrictedGroupVisibility.Create(),
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    protected RejectAnalyseScenarioCommandHandler RejectHandler()
    {
        var conditionRepository = new Klacks.Api.Infrastructure.Repositories.Assistant.AgentConditionRepository(_context);
        return new RejectAnalyseScenarioCommandHandler(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            _scenarioService,
            new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>()),
            Substitute.For<IWizardRunCaptureRepository>(),
            conditionRepository,
            new Klacks.Api.Application.Services.Assistant.Conditions.AgentConditionLedgerService(
                conditionRepository,
                TimeProvider.System,
                Substitute.For<ILogger<Klacks.Api.Application.Services.Assistant.Conditions.AgentConditionLedgerService>>()),
            Substitute.For<IHttpContextAccessor>(),
            UnrestrictedGroupVisibility.Create(),
            Substitute.For<ILogger<RejectAnalyseScenarioCommandHandler>>());
    }

    protected async Task<AnalyseScenarioResource> CreateScenarioAsync(params Guid[] variantIds)
    {
        var scenario = await _createHandler.Handle(
            new CreateAnalyseScenarioCommand(new CreateAnalyseScenarioRequest
            {
                Name = TestPrefix + "SCENARIO",
                GroupId = _group.Id,
                FromDate = PeriodFrom,
                UntilDate = PeriodUntil,
                VariantIds = variantIds
            }),
            CancellationToken.None);
        _context.ChangeTracker.Clear();
        return scenario;
    }

    protected async Task<AnalyseScenario> CreateChainedScenarioAsync(AnalyseScenarioResource source)
    {
        var chained = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CHAINED",
            GroupId = _group.Id,
            Token = Guid.NewGuid(),
            SourceToken = source.Token,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active,
            Kind = AnalyseScenarioKind.Scenario,
            CreatedByUser = TestPrefix + "USER"
        };
        await _context.Set<AnalyseScenario>().AddAsync(chained);
        await _scenarioService.CloneScenarioDataAsync(
            _group.Id, PeriodFrom, PeriodUntil, chained.Token, null, source.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return chained;
    }

    protected async Task<AnalyseScenario> CreateVariantAsync(string suffix)
    {
        var variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            GroupId = _group.Id,
            Token = Guid.NewGuid(),
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active,
            Kind = AnalyseScenarioKind.Variant,
            CreatedByUser = TestPrefix + "USER"
        };
        await _context.Set<AnalyseScenario>().AddAsync(variant);
        await _context.SaveChangesAsync();
        return variant;
    }

    protected async Task<(Shift SealedOrder, Shift Plannable)> CreateOrderAsync(string suffix, Guid? token, int? lft = null, int? rgt = null)
    {
        var sealedOrder = NewShift(suffix, ShiftStatus.SealedOrder, null, token);
        var plannable = NewShift(suffix, ShiftStatus.OriginalShift, sealedOrder.Id, token);
        plannable.Lft = lft;
        plannable.Rgt = rgt;
        await AddShiftsAsync(sealedOrder, plannable);
        return (sealedOrder, plannable);
    }

    /// <summary>
    /// Builds an order through the real cut facade: the plannable copy becomes the first first-level piece, a second
    /// first-level piece follows, and the first piece gets one child.
    /// </summary>
    protected async Task<CutFamily> CreateCutOrderAsync(string suffix, Guid? token)
    {
        var (sealedOrder, plannable) = await CreateOrderAsync(suffix, token);

        var firstPiece = await PieceOfAsync(plannable.Id, plannable.Id, PeriodFrom, FirstPieceUntil);
        var secondPiece = await PieceOfAsync(plannable.Id, Guid.NewGuid(), SecondPieceFrom, PeriodUntil);
        await _facade.ProcessBatchCutsAsync([Update(firstPiece), Create(sealedOrder.Id, secondPiece)]);
        _context.ChangeTracker.Clear();

        var child = await PieceOfAsync(plannable.Id, Guid.NewGuid(), PeriodFrom, ChildUntil);
        await _facade.ProcessBatchCutsAsync([Create(plannable.Id, child)]);
        _context.ChangeTracker.Clear();

        return new CutFamily(sealedOrder.Id, firstPiece.Id, secondPiece.Id, child.Id);
    }

    protected async Task<ShiftResource> PieceOfAsync(Guid sourceShiftId, Guid pieceId, DateOnly from, DateOnly until)
    {
        var stored = await _shiftRepository.Get(sourceShiftId);
        var resource = _mapper.ToShiftResource(stored!);
        _context.ChangeTracker.Clear();
        resource.Id = pieceId;
        resource.Status = ShiftStatus.SplitShift;
        resource.FromDate = from;
        resource.UntilDate = until;
        resource.Client = null;
        return resource;
    }

    protected static CutOperation Create(Guid parentId, ShiftResource data) =>
        new() { Type = CutOperation.CreateType, ParentId = parentId.ToString(), Data = data };

    protected static CutOperation Update(ShiftResource data) =>
        new() { Type = CutOperation.UpdateType, ParentId = string.Empty, Data = data };

    protected async Task AddShiftsAsync(params Shift[] shifts)
    {
        await _context.Shift.AddRangeAsync(shifts);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    protected async Task<Guid> AddScenarioWorkAsync(Guid token, Guid keyShiftId, DateOnly date)
    {
        var cloneId = await _context.Shift.AsNoTracking()
            .Where(s => s.AnalyseToken == token && s.ScenarioSourceShiftId == keyShiftId)
            .Select(s => s.Id)
            .SingleAsync();
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _worker.Id,
            ShiftId = cloneId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = WorkHours,
            AnalyseToken = token
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return work.Id;
    }

    protected async Task<Guid> AddExpenseAsync(Guid shiftId)
    {
        var expense = new ShiftExpenses
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            Amount = ExpenseAmount,
            Description = TestPrefix + "EXPENSE",
            Taxable = false
        };
        await _context.Set<ShiftExpenses>().AddAsync(expense);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return expense.Id;
    }

    protected async Task SetLockLevelAsync(Guid workId, WorkLockLevel level)
    {
        await _context.Work
            .Where(w => w.Id == workId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.LockLevel, level));
        _context.ChangeTracker.Clear();
    }

    protected async Task<List<Shift>> LoadRowsAsync(IReadOnlyCollection<Guid> ids)
    {
        return await _context.Shift.AsNoTracking()
            .Include(s => s.GroupItems.Where(item => !item.IsDeleted))
            .Where(s => ids.Contains(s.Id))
            .ToListAsync();
    }

    protected async Task<List<FamilyRow>> SnapshotAsync(IReadOnlyCollection<Guid> ids)
    {
        return await _context.Shift.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.Id)
            .Select(s => new FamilyRow(s.Id, s.Status, s.ClientId, s.OriginalId, s.ParentId, s.RootId))
            .ToListAsync();
    }

    protected async Task<List<NestedSetRow>> SnapshotValuesAsync(IReadOnlyCollection<Guid> ids)
    {
        return await _context.Shift.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.Id)
            .Select(s => new NestedSetRow(s.Id, s.Lft, s.Rgt))
            .ToListAsync();
    }

    protected Shift NewShift(string suffix, ShiftStatus status, Guid? originalId, Guid? token)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Variant promotion seam",
            Status = status,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
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
            WorkTime = WorkHours,
            ClientId = _customer.Id,
            OriginalId = originalId,
            AnalyseToken = token,
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = _group.Id, ShiftId = id, AnalyseToken = token }]
        };
    }

    protected static Client NewClient(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        FirstName = "Test",
        Company = string.Empty,
        LegalEntity = false
    };

    protected DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    protected static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM analyse_scenario_variants WHERE variant_id IN (SELECT id FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM client_shift_preference WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL, original_id = NULL, parent_id = NULL, root_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    protected sealed record CutFamily(Guid SealedOrder, Guid FirstPiece, Guid SecondPiece, Guid Child)
    {
        public List<Guid> Ids { get; } = [SealedOrder, FirstPiece, SecondPiece, Child];
    }

    protected sealed record FamilyRow(Guid Id, ShiftStatus Status, Guid? ClientId, Guid? OriginalId, Guid? ParentId, Guid? RootId);

    protected sealed record NestedSetRow(Guid Id, int? Lft, int? Rgt);
}
