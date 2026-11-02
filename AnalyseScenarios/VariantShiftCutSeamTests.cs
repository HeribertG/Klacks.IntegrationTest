// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of cutting and resetting cuts inside a variant (key), through the real ShiftCutFacade,
/// repositories and variant guard: a real cut is unchanged (pieces without a token, nested-set values recalculated); a key
/// cut keeps the key token on every row and group link, leaves ScenarioSourceShiftId empty and numbers no tree; a mixed
/// batch numbers the real trees only and leaves a real tree outside the batch as it was; a reset inside a key writes the new
/// plannable copy with the token and closes only rows of the key; a scenario clone and a consumed key are refused without
/// a write; the reset date range of a key order is found through the stored token. A real order sits next to the key order,
/// so a filter that is too narrow or too wide shows up as a wrong result instead of an empty one. Only
/// INTEGRATION_TEST_ rows are written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
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

[TestFixture]
[Category("RealDatabase")]
public class VariantShiftCutSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARIANTCUT_";
    private const int SentinelLft = 7;
    private const int SentinelRgt = 8;
    private const int RootLft = 1;
    private const int RootRgt = 2;
    private const int PieceCount = 2;
    private static readonly DateOnly PeriodFrom = new(2099, 8, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 31);
    private static readonly DateOnly FirstPieceUntil = new(2099, 8, 15);
    private static readonly DateOnly SecondPieceFrom = new(2099, 8, 16);
    private static readonly DateOnly ResetFrom = new(2099, 8, 20);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private ShiftRepository _shiftRepository = null!;
    private ScheduleMapper _mapper = null!;
    private IShiftVisibilityGuard _visibility = null!;
    private VariantShiftWriteGuard _writeGuard = null!;
    private ShiftCutFacade _facade = null!;
    private AnalyseScenarioService _scenarioService = null!;
    private Group _group = null!;
    private Client _client = null!;

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
        _scenarioService = (AnalyseScenarioService)_provider.GetRequiredService<IAnalyseScenarioService>();
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

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(_ => _context.SaveChangesAsync());
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<List<Shift>>>>())
            .Returns(call => call.Arg<Func<Task<List<Shift>>>>()());

        var groupGuard = Substitute.For<IGroupVisibilityGuard>();
        groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);
        groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _writeGuard = new VariantShiftWriteGuard(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            new ScenarioVariantRepository(_context),
            _scenarioService,
            groupGuard);

        _visibility = Substitute.For<IShiftVisibilityGuard>();
        _visibility.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _visibility.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>()).Returns(true);

        _facade = new ShiftCutFacade(
            _shiftRepository,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            new ShiftTreeService(_context, Substitute.For<ILogger<ShiftTreeService>>()),
            ShiftFamilyResetFactory.Create(_context, _shiftRepository, new ShiftTreeService(_context, Substitute.For<ILogger<ShiftTreeService>>()), _visibility, unitOfWork),
            shiftValidator,
            _mapper,
            unitOfWork,
            _visibility,
            new ShiftExpensesRepository(_context, Substitute.For<ILogger<ShiftExpenses>>()),
            _writeGuard,
            Substitute.For<ILogger<ShiftCutFacade>>());

        _group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "GROUP",
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        await _context.Set<Group>().AddAsync(_group);
        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(_client);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _provider.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task RealCut_StaysUnchanged_PiecesWithoutTokenAndTheTreeNumbered()
    {
        var (sealedOrder, plannable) = await CreateOrderAsync("REAL", null);

        await _facade.ProcessBatchCutsAsync(await TwoPieceBatchAsync(sealedOrder, plannable));

        var rows = await LoadRowsAsync(sealedOrder.Id);
        rows.Count.ShouldBe(PieceCount);
        rows.ShouldAllBe(row => row.AnalyseToken == null && row.Status == ShiftStatus.SplitShift);
        rows.ShouldAllBe(row => row.Lft == RootLft && row.Rgt == RootRgt);
        rows.SelectMany(row => row.GroupItems).ShouldAllBe(item => item.AnalyseToken == null);
    }

    [Test]
    public async Task KeyCut_KeepsTheTokenOnEveryRowAndGroupLink_AndNumbersNoTree()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (sealedOrder, plannable) = await CreateOrderAsync("KEY", variant.Token);

        await _facade.ProcessBatchCutsAsync(await TwoPieceBatchAsync(sealedOrder, plannable));

        var rows = await LoadRowsAsync(sealedOrder.Id);
        rows.Count.ShouldBe(PieceCount);
        rows.ShouldAllBe(row => row.AnalyseToken == variant.Token && row.Status == ShiftStatus.SplitShift);
        rows.ShouldAllBe(row => row.ScenarioSourceShiftId == null);
        rows.ShouldAllBe(row => row.Lft == null && row.Rgt == null);
        rows.ShouldAllBe(row => row.OriginalId == sealedOrder.Id);
        rows.SelectMany(row => row.GroupItems).Count().ShouldBe(PieceCount);
        rows.SelectMany(row => row.GroupItems).ShouldAllBe(item => item.AnalyseToken == variant.Token);
        (await _context.Shift.AsNoTracking().CountAsync(s => s.OriginalId == sealedOrder.Id && s.AnalyseToken == null))
            .ShouldBe(0);
    }

    [Test]
    public async Task KeyCut_LeavesARealOrderAndItsTreeValuesAlone()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (keyOrder, keyPlannable) = await CreateOrderAsync("KEY", variant.Token);
        var (realOrder, realPlannable) = await CreateOrderAsync("REAL", null, SentinelLft, SentinelRgt);

        await _facade.ProcessBatchCutsAsync(await TwoPieceBatchAsync(keyOrder, keyPlannable));

        var realRows = await LoadRowsAsync(realOrder.Id);
        realRows.Count.ShouldBe(1);
        realRows[0].Id.ShouldBe(realPlannable.Id);
        realRows[0].Status.ShouldBe(ShiftStatus.OriginalShift);
        realRows[0].AnalyseToken.ShouldBeNull();
        realRows[0].Lft.ShouldBe(SentinelLft);
        realRows[0].Rgt.ShouldBe(SentinelRgt);
    }

    [Test]
    public async Task MixedBatch_NumbersTheRealTreesOnly()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (keyOrder, keyPlannable) = await CreateOrderAsync("KEY", variant.Token);
        var (realOrder, realPlannable) = await CreateOrderAsync("REAL", null);
        var batch = await TwoPieceBatchAsync(keyOrder, keyPlannable);
        batch.AddRange(await TwoPieceBatchAsync(realOrder, realPlannable));

        await _facade.ProcessBatchCutsAsync(batch);

        var realRows = await LoadRowsAsync(realOrder.Id);
        realRows.Count.ShouldBe(PieceCount);
        realRows.ShouldAllBe(row => row.Lft == RootLft && row.Rgt == RootRgt && row.AnalyseToken == null);
        var keyRows = await LoadRowsAsync(keyOrder.Id);
        keyRows.Count.ShouldBe(PieceCount);
        keyRows.ShouldAllBe(row => row.Lft == null && row.Rgt == null && row.AnalyseToken == variant.Token);
    }

    [Test]
    public async Task KeyReset_RemovesThePiecesOfTheKeyOnly_AndTheNewPlannableCopyKeepsTheTokenFromTheStartOfTheKey()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (keyOrder, keyPlannable) = await CreateOrderAsync("KEY", variant.Token);
        var (realOrder, _) = await CreateOrderAsync("REAL", null);
        await _facade.ProcessBatchCutsAsync(await TwoPieceBatchAsync(keyOrder, keyPlannable));
        _context.ChangeTracker.Clear();

        var result = await _facade.ResetCutsAsync(keyOrder.Id, ResetFrom);

        _context.ChangeTracker.Clear();
        var rows = await LoadRowsAsync(keyOrder.Id);
        var copy = rows.ShouldHaveSingleItem();
        copy.Status.ShouldBe(ShiftStatus.OriginalShift);
        copy.AnalyseToken.ShouldBe(variant.Token);
        copy.FromDate.ShouldBe(keyOrder.FromDate);
        copy.OriginalId.ShouldBe(keyOrder.Id);
        copy.GroupItems.Count.ShouldBe(1);
        copy.GroupItems.ShouldAllBe(item => item.AnalyseToken == variant.Token);
        result.ShouldHaveSingleItem().Id.ShouldBe(copy.Id);
        (await LoadRowsAsync(realOrder.Id)).ShouldAllBe(row => row.UntilDate == PeriodUntil && row.AnalyseToken == null);
        (await _context.Shift.AsNoTracking().CountAsync(s => s.OriginalId == keyOrder.Id && s.AnalyseToken == null))
            .ShouldBe(0);
    }

    [Test]
    public async Task KeyCut_APieceWithoutAnEnd_IsRefusedWithoutAWrite_AlthoughTheValidatorLetsItPass()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (keyOrder, keyPlannable) = await CreateOrderAsync("KEY", variant.Token);
        var batch = await TwoPieceBatchAsync(keyOrder, keyPlannable);
        batch[1].Data.UntilDate = null;
        _context.ChangeTracker.Clear();

        var refusal = await Should.ThrowAsync<BadRequestException>(() => _facade.ProcessBatchCutsAsync(batch));

        refusal.Message.ShouldBe(VariantShiftWriteGuard.PeriodOutsideVariantMessage);
        _context.ChangeTracker.Clear();
        var rows = await LoadRowsAsync(keyOrder.Id);
        rows.ShouldHaveSingleItem().Id.ShouldBe(keyPlannable.Id);
    }

    [Test]
    public async Task RealCut_APieceWithoutAnEnd_StaysAllowed()
    {
        var (realOrder, realPlannable) = await CreateOrderAsync("REAL", null);
        var open = await PieceOfAsync(realPlannable.Id, realPlannable.Id, PeriodFrom, PeriodUntil);
        open.UntilDate = null;

        await _facade.ProcessBatchCutsAsync([Update(open)]);

        _context.ChangeTracker.Clear();
        (await LoadRowsAsync(realOrder.Id)).Single().UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task KeyReset_ToADayOutsideTheKey_IsRefusedWithoutAWrite()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (keyOrder, keyPlannable) = await CreateOrderAsync("KEY", variant.Token);
        await _facade.ProcessBatchCutsAsync(await TwoPieceBatchAsync(keyOrder, keyPlannable));
        _context.ChangeTracker.Clear();

        var refusal = await Should.ThrowAsync<BadRequestException>(() => _facade.ResetCutsAsync(keyOrder.Id, PeriodUntil.AddDays(1)));

        refusal.Message.ShouldBe(VariantShiftWriteGuard.PeriodOutsideVariantMessage);
        _context.ChangeTracker.Clear();
        (await LoadRowsAsync(keyOrder.Id)).Count.ShouldBe(PieceCount);
    }

    [Test]
    public async Task ResetDateRange_OfAKeyOrder_IsFoundThroughTheStoredToken()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (keyOrder, _) = await CreateOrderAsync("KEY", variant.Token);
        var handler = new GetResetDateRangeQueryHandler(
            _shiftRepository, _visibility, _writeGuard, new OrderFamilyEndRepository(_context), Substitute.For<ILogger<GetResetDateRangeQueryHandler>>());

        var range = await handler.Handle(new GetResetDateRangeQuery(keyOrder.Id), CancellationToken.None);

        range.EarliestResetDate.ShouldBe(PeriodFrom);
        range.UntilDate.ShouldBe(PeriodUntil);
    }

    [Test]
    public async Task CutList_OfAKeyOrder_ReturnsTheRowsOfTheKey_AndOfARealOrderTheRealRows()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (keyOrder, keyPlannable) = await CreateOrderAsync("KEY", variant.Token);
        var (realOrder, realPlannable) = await CreateOrderAsync("REAL", null);
        await _facade.ProcessBatchCutsAsync(await TwoPieceBatchAsync(keyOrder, keyPlannable));
        _context.ChangeTracker.Clear();
        var groupFilter = Substitute.For<IShiftGroupFilterService>();
        groupFilter.ResolveShiftListGroupIdsAsync(Arg.Any<Guid?>()).Returns(Task.FromResult<List<Guid>?>(null));
        var handler = new CutListQueryHandler(
            _shiftRepository, groupFilter, _mapper, _writeGuard, Substitute.For<ILogger<CutListQueryHandler>>());

        var keyCuts = (await handler.Handle(new CutListQuery(keyOrder.Id), CancellationToken.None)).ToList();
        var realCuts = (await handler.Handle(new CutListQuery(realOrder.Id), CancellationToken.None)).ToList();

        keyCuts.Count.ShouldBe(PieceCount);
        keyCuts.ShouldAllBe(cut => cut.AnalyseToken == variant.Token && cut.Status == ShiftStatus.SplitShift);
        realCuts.Count.ShouldBe(1);
        realCuts[0].Id.ShouldBe(realPlannable.Id);
        realCuts[0].AnalyseToken.ShouldBeNull();
    }

    [Test]
    public async Task CutAndReset_OfAScenarioClone_AreRefused_WithoutAWrite()
    {
        var real = await CreateShiftAsync("CLONED", null, ShiftStatus.OriginalShift);
        var idMap = await _scenarioService.CloneScenarioDataAsync(
            groupId: null,
            fromDate: PeriodFrom,
            untilDate: PeriodUntil,
            token: Guid.NewGuid(),
            additionalShiftIds: new[] { real.Id },
            null,
            ct: CancellationToken.None);
        await _context.SaveChangesAsync();
        var cloneId = idMap[real.Id];
        var piece = await PieceOfAsync(cloneId, cloneId, PeriodFrom, FirstPieceUntil);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _facade.ProcessBatchCutsAsync([Update(piece)]));
        await Should.ThrowAsync<InvalidOperationException>(
            () => _facade.ResetCutsAsync(cloneId, ResetFrom));

        _context.ChangeTracker.Clear();
        var clone = await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == cloneId);
        clone.Status.ShouldBe(ShiftStatus.OriginalShift);
        clone.UntilDate.ShouldBe(PeriodUntil);
        (await _context.Shift.AsNoTracking().CountAsync(s => s.OriginalId == cloneId)).ShouldBe(0);
    }

    [Test]
    public async Task CutAndReset_InAConsumedKey_AreAConflict_WithoutAWrite()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var (keyOrder, keyPlannable) = await CreateOrderAsync("KEY", variant.Token);
        var batch = await TwoPieceBatchAsync(keyOrder, keyPlannable);
        await _context.Set<AnalyseScenario>()
            .Where(scenario => scenario.Id == variant.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(scenario => scenario.Status, AnalyseScenarioStatus.Accepted));
        _context.ChangeTracker.Clear();

        await Should.ThrowAsync<ConflictException>(() => _facade.ProcessBatchCutsAsync(batch));
        await Should.ThrowAsync<ConflictException>(() => _facade.ResetCutsAsync(keyOrder.Id, ResetFrom));

        _context.ChangeTracker.Clear();
        var rows = await LoadRowsAsync(keyOrder.Id);
        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe(keyPlannable.Id);
        rows[0].Status.ShouldBe(ShiftStatus.OriginalShift);
    }

    private async Task<List<CutOperation>> TwoPieceBatchAsync(Shift sealedOrder, Shift plannable)
    {
        var first = await PieceOfAsync(plannable.Id, plannable.Id, PeriodFrom, FirstPieceUntil);
        var second = await PieceOfAsync(plannable.Id, Guid.NewGuid(), SecondPieceFrom, PeriodUntil);
        return [Update(first), Create(sealedOrder.Id, second)];
    }

    private async Task<ShiftResource> PieceOfAsync(Guid sourceShiftId, Guid pieceId, DateOnly from, DateOnly until)
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

    private static CutOperation Create(Guid parentId, ShiftResource data) =>
        new() { Type = CutOperation.CreateType, ParentId = parentId.ToString(), Data = data };

    private static CutOperation Update(ShiftResource data) =>
        new() { Type = CutOperation.UpdateType, ParentId = string.Empty, Data = data };

    private async Task<List<Shift>> LoadRowsAsync(Guid sealedOrderId)
    {
        return await _context.Shift.AsNoTracking()
            .Include(s => s.GroupItems.Where(item => !item.IsDeleted))
            .Where(s => s.OriginalId == sealedOrderId && !s.IsDeleted)
            .OrderBy(s => s.FromDate)
            .ToListAsync();
    }

    private async Task<(Shift SealedOrder, Shift Plannable)> CreateOrderAsync(string suffix, Guid? token, int? lft = null, int? rgt = null)
    {
        var sealedOrder = NewShift(suffix, ShiftStatus.SealedOrder, null, token);
        var plannable = NewShift(suffix, ShiftStatus.OriginalShift, sealedOrder.Id, token);
        plannable.Lft = lft;
        plannable.Rgt = rgt;
        await _context.Shift.AddRangeAsync(sealedOrder, plannable);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (sealedOrder, plannable);
    }

    private async Task<Shift> CreateShiftAsync(string suffix, Guid? token, ShiftStatus status)
    {
        var shift = NewShift(suffix, status, null, token);
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return shift;
    }

    private Shift NewShift(string suffix, ShiftStatus status, Guid? originalId, Guid? token)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Variant cut seam",
            Status = status,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            ClientId = _client.Id,
            OriginalId = originalId,
            AnalyseToken = token,
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = _group.Id, ShiftId = id, AnalyseToken = token }]
        };
    }

    private async Task<AnalyseScenario> CreateVariantAsync(string suffix)
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
            DELETE FROM client_shift_preference WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%'))
                OR group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL, original_id = NULL, parent_id = NULL, root_id = NULL WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
