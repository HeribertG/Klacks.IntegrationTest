// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The shift hierarchy is server-owned, end to end against PostgreSQL (IT database klacks): POST Shifts only creates
/// orders and discards order reference and cut-tree fields of the request; the cut batch keeps the stored order
/// reference, customer and tree fields on an UPDATE, derives them from the parent on a CREATE (also along a chain
/// inside one batch) and refuses an UPDATE of a sealed order with nothing written. PUT Shifts keeps a stored draft order
/// a draft or seals it, any other status is refused with nothing written. The UI batch form (pieces cloned from their
/// parent), cut_shift and cut_shift_by_date keep working. Every new cut piece starts with default expenses of its own:
/// copies of the rows of the plannable shift or piece it is cut from (fresh ids, never the source rows), also when the
/// UI form sends the parent's rows along. Every row carries the INTEGRATION_TEST_SHIFTHIER_ prefix and the cleanup
/// deletes exactly those rows.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using ShiftExpenses = Klacks.Api.Domain.Models.Schedules.ShiftExpenses;
using ShiftPostHandler = Klacks.Api.Application.Handlers.Shifts.PostCommandHandler;
using ShiftPutHandler = Klacks.Api.Application.Handlers.Shifts.PutCommandHandler;

namespace Klacks.IntegrationTest.Shifts;

[TestFixture]
[Category("RealDatabase")]
public class ShiftHierarchyOwnershipPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_SHIFTHIER_";
    private const string UpdateOperation = CutOperation.UpdateType;
    private const string CreateOperation = CutOperation.CreateType;
    private const decimal MealAmount = 12.50m;
    private const string MealDescription = Prefix + "MEAL";
    private const decimal ParkingAmount = 5.00m;
    private const string ParkingDescription = Prefix + "PARKING";
    private static readonly DateOnly OrderFrom = new(2026, 1, 1);

    private DataBaseContext _context = null!;
    private ScheduleMapper _scheduleMapper = null!;
    private IShiftRepository _shiftRepository = null!;
    private ShiftCutFacade _cutFacade = null!;
    private ShiftPostHandler _postHandler = null!;
    private ShiftPutHandler _putHandler = null!;
    private IShiftVisibilityGuard _allVisible = null!;

    private readonly Guid _customerA = Guid.NewGuid();
    private readonly Guid _customerB = Guid.NewGuid();

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        _context.Set<Client>().AddRange(NewCustomer(_customerA, "A"), NewCustomer(_customerB, "B"));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        BuildPipeline();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task Post_AsPlannableShiftOrPiece_OfAForeignOrder_IsRefused_AndNothingIsWritten(ShiftStatus status)
    {
        var (_, sealedOrderId) = await PostSealedOrderAsync("ORDER", _customerA);
        var copy = NewResource("SECOND_COPY", status, _customerA);
        copy.OriginalId = sealedOrderId;

        var error = await Should.ThrowAsync<BadRequestException>(
            () => _postHandler.Handle(new PostCommand<ShiftResource>(copy), CancellationToken.None));

        error.Message.ShouldBe(string.Format(ShiftHierarchyMessages.PostStatusNotAllowed, status));
        _context.ChangeTracker.Clear();
        (await _context.Shift.AnyAsync(s => s.Name == copy.Name)).ShouldBeFalse();
        (await _context.Shift.CountAsync(s => s.OriginalId == sealedOrderId)).ShouldBe(1, "only the derived plannable copy");
    }

    [Test]
    public async Task Post_WithOrderReferenceAndTreeFields_StoresTheOrderWithoutThem()
    {
        var (_, foreignOrderId) = await PostSealedOrderAsync("FOREIGN", _customerB);
        var draft = NewResource("DRAFT", ShiftStatus.OriginalOrder, _customerA);
        draft.OriginalId = foreignOrderId;
        draft.ParentId = foreignOrderId;
        draft.RootId = foreignOrderId;
        draft.Lft = 1;
        draft.Rgt = 2;

        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(draft), CancellationToken.None);

        var stored = await ReloadAsync(created!.Id);
        stored.Status.ShouldBe(ShiftStatus.OriginalOrder);
        stored.OriginalId.ShouldBeNull();
        stored.ParentId.ShouldBeNull();
        stored.RootId.ShouldBeNull();
        stored.Lft.ShouldBeNull();
        stored.Rgt.ShouldBeNull();
    }

    [Test]
    public async Task CutBatch_UiForm_ClonesOfTheParent_WithAChainInOneBatch_DeriveOrderAndCustomer()
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("UI", _customerA);
        var first = await StoredResourceAsync(plannableId);
        first.Status = ShiftStatus.SplitShift;
        first.UntilDate = new DateOnly(2026, 6, 30);
        var second = CloneForUi(first, Guid.NewGuid());
        second.FromDate = new DateOnly(2026, 7, 1);
        second.UntilDate = null;
        var third = CloneForUi(second, Guid.NewGuid());
        third.ParentId = second.Id;
        third.StartShift = new TimeOnly(12, 0);

        await _cutFacade.ProcessBatchCutsAsync(
        [
            new CutOperation { Type = UpdateOperation, ParentId = sealedOrderId.ToString(), Data = first },
            new CutOperation { Type = CreateOperation, ParentId = sealedOrderId.ToString(), Data = second },
            new CutOperation { Type = CreateOperation, ParentId = second.Id.ToString(), Data = third }
        ]);

        var storedFirst = await ReloadAsync(plannableId);
        var storedSecond = await ReloadAsync(second.Id);
        var storedThird = await ReloadAsync(third.Id);
        foreach (var piece in new[] { storedFirst, storedSecond, storedThird })
        {
            piece.Status.ShouldBe(ShiftStatus.SplitShift);
            piece.OriginalId.ShouldBe(sealedOrderId);
            piece.ClientId.ShouldBe(_customerA);
        }

        storedFirst.RootId.ShouldBe(plannableId);
        storedFirst.UntilDate.ShouldBe(new DateOnly(2026, 6, 30));
        storedSecond.RootId.ShouldBe(second.Id);
        storedSecond.ParentId.ShouldBeNull();
        storedThird.ParentId.ShouldBe(second.Id);
        storedThird.RootId.ShouldBe(second.Id);
    }

    [Test]
    public async Task CutBatch_ForeignOrderCustomerAndTreeFieldsInTheRequest_AreIgnored()
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("OWN", _customerA);
        var (_, foreignOrderId) = await PostSealedOrderAsync("FOREIGN", _customerB);
        var first = await StoredResourceAsync(plannableId);
        first.Status = ShiftStatus.SplitShift;
        first.UntilDate = new DateOnly(2026, 6, 30);
        var piece = CloneForUi(first, Guid.NewGuid());
        piece.FromDate = new DateOnly(2026, 7, 1);
        piece.UntilDate = null;
        foreach (var resource in new[] { first, piece })
        {
            resource.OriginalId = foreignOrderId;
            resource.ClientId = _customerB;
            resource.RootId = foreignOrderId;
            resource.ParentId = foreignOrderId;
        }

        await _cutFacade.ProcessBatchCutsAsync(
        [
            new CutOperation { Type = UpdateOperation, ParentId = sealedOrderId.ToString(), Data = first },
            new CutOperation { Type = CreateOperation, ParentId = sealedOrderId.ToString(), Data = piece }
        ]);

        var update = await StoredResourceAsync(piece.Id);
        update.StartShift = new TimeOnly(9, 0);
        update.OriginalId = foreignOrderId;
        update.ClientId = _customerB;
        update.RootId = foreignOrderId;
        update.ParentId = foreignOrderId;
        await _cutFacade.ProcessBatchCutsAsync(
            [new CutOperation { Type = UpdateOperation, ParentId = sealedOrderId.ToString(), Data = update }]);

        foreach (var id in new[] { plannableId, piece.Id })
        {
            var stored = await ReloadAsync(id);
            stored.OriginalId.ShouldBe(sealedOrderId);
            stored.ClientId.ShouldBe(_customerA);
            stored.RootId.ShouldBe(id);
            stored.ParentId.ShouldBeNull();
        }

        (await ReloadAsync(piece.Id)).StartShift.ShouldBe(new TimeOnly(9, 0), "the scalar change still applies");
        (await _context.Shift.CountAsync(s => s.OriginalId == foreignOrderId)).ShouldBe(1, "nothing joined the foreign order");
    }

    [TestCase(ShiftStatus.SealedOrder)]
    [TestCase(ShiftStatus.OriginalOrder)]
    public async Task CutBatch_UpdateOfAnOrder_IsRefused_AndNothingOfTheBatchIsWritten(ShiftStatus targetStatus)
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("TARGET", _customerA);
        var targetId = sealedOrderId;
        if (targetStatus == ShiftStatus.OriginalOrder)
        {
            var draft = await _postHandler.Handle(
                new PostCommand<ShiftResource>(NewResource("DRAFT_TARGET", ShiftStatus.OriginalOrder, _customerA)),
                CancellationToken.None);
            targetId = draft!.Id;
        }

        var orderUpdate = await StoredResourceAsync(targetId);
        orderUpdate.Status = ShiftStatus.SplitShift;
        orderUpdate.Name = Prefix + "RENAMED";
        var piece = CloneForUi(await StoredResourceAsync(plannableId), Guid.NewGuid());
        piece.Status = ShiftStatus.SplitShift;

        var error = await Should.ThrowAsync<BadRequestException>(() => _cutFacade.ProcessBatchCutsAsync(
        [
            new CutOperation { Type = CreateOperation, ParentId = plannableId.ToString(), Data = piece },
            new CutOperation { Type = UpdateOperation, ParentId = string.Empty, Data = orderUpdate }
        ]));

        error.Message.ShouldBe(string.Format(
            ShiftHierarchyMessages.CutUpdateStatusNotAllowed, targetId, targetStatus, ShiftStatus.SplitShift));
        _context.ChangeTracker.Clear();
        (await _context.Shift.AnyAsync(s => s.Id == piece.Id)).ShouldBeFalse();
        var stored = await ReloadAsync(targetId);
        stored.Status.ShouldBe(targetStatus);
        stored.Name.ShouldNotBe(Prefix + "RENAMED");
    }

    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task Put_DraftOrderWithAnotherStatus_IsRefused_AndNothingIsWritten(ShiftStatus sentStatus)
    {
        var draft = await _postHandler.Handle(
            new PostCommand<ShiftResource>(NewResource("PUT_DRAFT", ShiftStatus.OriginalOrder, _customerA)), CancellationToken.None);
        _context.ChangeTracker.Clear();
        var update = await StoredResourceAsync(draft!.Id);
        update.Status = sentStatus;
        update.Name = Prefix + "PUT_DRAFT_RENAMED";

        var error = await Should.ThrowAsync<BadRequestException>(
            () => _putHandler.Handle(new PutCommand<ShiftResource>(update), CancellationToken.None));

        error.Message.ShouldBe(string.Format(ShiftHierarchyMessages.DraftStatusChangeNotAllowed, draft.Id, sentStatus));
        var stored = await ReloadAsync(draft.Id);
        stored.Status.ShouldBe(ShiftStatus.OriginalOrder);
        stored.Name.ShouldBe(Prefix + "PUT_DRAFT");
        (await _context.Shift.CountAsync(s => s.OriginalId == draft.Id)).ShouldBe(0);
    }

    [Test]
    public async Task Put_DraftOrderSealed_IsSealed_AndDerivesThePlannableCopy()
    {
        var draft = await _postHandler.Handle(
            new PostCommand<ShiftResource>(NewResource("PUT_SEAL", ShiftStatus.OriginalOrder, _customerA)), CancellationToken.None);
        _context.ChangeTracker.Clear();
        var update = await StoredResourceAsync(draft!.Id);
        update.Status = ShiftStatus.SealedOrder;

        await _putHandler.Handle(new PutCommand<ShiftResource>(update), CancellationToken.None);

        (await ReloadAsync(draft.Id)).Status.ShouldBe(ShiftStatus.SealedOrder);
        _context.ChangeTracker.Clear();
        var copies = await _context.Shift.AsNoTracking().Where(s => s.OriginalId == draft.Id).ToListAsync();
        copies.ShouldContain(s => s.Status == ShiftStatus.OriginalShift);
    }

    [Test]
    public async Task Put_DraftOrderStaysADraft_IsSaved()
    {
        var draft = await _postHandler.Handle(
            new PostCommand<ShiftResource>(NewResource("PUT_KEEP", ShiftStatus.OriginalOrder, _customerA)), CancellationToken.None);
        _context.ChangeTracker.Clear();
        var update = await StoredResourceAsync(draft!.Id);
        update.Name = Prefix + "PUT_KEEP_RENAMED";

        await _putHandler.Handle(new PutCommand<ShiftResource>(update), CancellationToken.None);

        var stored = await ReloadAsync(draft.Id);
        stored.Status.ShouldBe(ShiftStatus.OriginalOrder);
        stored.Name.ShouldBe(Prefix + "PUT_KEEP_RENAMED");
    }

    [Test]
    public async Task CutShiftSkill_KeepsOrderAndCustomerOnAllParts()
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("SKILL", _customerA);

        var result = await CutSkill().ExecuteAsync(
            SkillContext(),
            new Dictionary<string, object>
            {
                ["shiftId"] = plannableId.ToString(),
                ["parts"] = "08:00-12:00,12:00-16:00",
                ["partNames"] = $"{Prefix}EARLY,{Prefix}LATE"
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        var parts = await PiecesOfAsync(sealedOrderId);
        parts.Count.ShouldBe(2);
        parts.ShouldAllBe(p => p.ClientId == _customerA && p.RootId == p.Id && p.ParentId == null);
        parts.ShouldContain(p => p.Id == plannableId);
    }

    [Test]
    public async Task CutShiftByDateSkill_CutsThePlannableShift_ThenAPiece_WithDerivedOrderAndCustomer()
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("BYDATE", _customerA);
        var skill = new CutShiftByDateSkill(_shiftRepository, new ShiftReader(_context), _cutFacade, _allVisible);

        var first = await skill.ExecuteAsync(SkillContext(),
            new Dictionary<string, object> { ["shiftId"] = plannableId.ToString(), ["cutDate"] = "2026-07-01" },
            CancellationToken.None);
        _context.ChangeTracker.Clear();
        first.Success.ShouldBeTrue(first.Message);
        var secondPiece = (await PiecesOfAsync(sealedOrderId)).Single(p => p.Id != plannableId);

        var second = await skill.ExecuteAsync(SkillContext(),
            new Dictionary<string, object> { ["shiftId"] = secondPiece.Id.ToString(), ["cutDate"] = "2026-10-01" },
            CancellationToken.None);
        _context.ChangeTracker.Clear();

        second.Success.ShouldBeTrue(second.Message);
        var pieces = await PiecesOfAsync(sealedOrderId);
        pieces.Count.ShouldBe(3);
        pieces.ShouldAllBe(p => p.ClientId == _customerA);
        var nested = pieces.Single(p => p.Id != plannableId && p.Id != secondPiece.Id);
        nested.ParentId.ShouldBe(secondPiece.Id);
        nested.RootId.ShouldBe(secondPiece.RootId);
        nested.FromDate.ShouldBe(new DateOnly(2026, 10, 1));
        (await ReloadAsync(secondPiece.Id)).UntilDate.ShouldBe(new DateOnly(2026, 9, 30));
    }

    [Test]
    public async Task CutShiftSkill_EveryPart_CarriesTheDefaultExpensesOfThePlannableShift_AsOwnRows()
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("SKILL_EXP", _customerA, MealExpense());
        await AddExpenseAsync(plannableId, ParkingAmount, ParkingDescription, taxable: true);

        var result = await CutSkill().ExecuteAsync(
            SkillContext(),
            new Dictionary<string, object>
            {
                ["shiftId"] = plannableId.ToString(),
                ["parts"] = "08:00-11:00,11:00-14:00,14:00-16:00",
                ["partNames"] = $"{Prefix}P1,{Prefix}P2,{Prefix}P3"
            },
            CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        var parts = await PiecesOfAsync(sealedOrderId);
        parts.Count.ShouldBe(3);
        await ShouldEachCarryMealAndParkingAsync(parts.Select(p => p.Id).ToList());
        (await ExpensesOfAsync(sealedOrderId)).Select(e => e.Description).ShouldBe([MealDescription], "the order stays unchanged");
    }

    [Test]
    public async Task CutShiftByDateSkill_NewPieces_CarryTheDefaultExpensesOfThePieceTheyAreCutFrom()
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("BYDATE_EXP", _customerA, MealExpense());
        await AddExpenseAsync(plannableId, ParkingAmount, ParkingDescription, taxable: true);
        var skill = new CutShiftByDateSkill(_shiftRepository, new ShiftReader(_context), _cutFacade, _allVisible);

        var first = await skill.ExecuteAsync(SkillContext(),
            new Dictionary<string, object> { ["shiftId"] = plannableId.ToString(), ["cutDate"] = "2026-07-01" },
            CancellationToken.None);
        _context.ChangeTracker.Clear();
        first.Success.ShouldBeTrue(first.Message);
        var secondPiece = (await PiecesOfAsync(sealedOrderId)).Single(p => p.Id != plannableId);

        var second = await skill.ExecuteAsync(SkillContext(),
            new Dictionary<string, object> { ["shiftId"] = secondPiece.Id.ToString(), ["cutDate"] = "2026-10-01" },
            CancellationToken.None);
        _context.ChangeTracker.Clear();

        second.Success.ShouldBeTrue(second.Message);
        var pieces = await PiecesOfAsync(sealedOrderId);
        pieces.Count.ShouldBe(3);
        await ShouldEachCarryMealAndParkingAsync(pieces.Select(p => p.Id).ToList());
    }

    [Test]
    public async Task CutBatch_UiForm_PieceClonedWithTheParentsExpenseRows_GetsFreshRows_WithoutDuplicates()
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("UI_EXP_CLONE", _customerA, MealExpense());
        var first = await StoredResourceAsync(plannableId);
        first.DefaultExpenses.Count.ShouldBe(1, "the stored plannable shift carries the order's expense");
        first.Status = ShiftStatus.SplitShift;
        first.UntilDate = new DateOnly(2026, 6, 30);
        var second = CloneForUi(first, Guid.NewGuid());
        second.FromDate = new DateOnly(2026, 7, 1);
        second.UntilDate = null;

        await _cutFacade.ProcessBatchCutsAsync(
        [
            new CutOperation { Type = UpdateOperation, ParentId = sealedOrderId.ToString(), Data = first },
            new CutOperation { Type = CreateOperation, ParentId = plannableId.ToString(), Data = second }
        ]);

        var plannableRows = await ExpensesOfAsync(plannableId);
        var pieceRows = await ExpensesOfAsync(second.Id);
        plannableRows.Select(e => e.Description).ShouldBe([MealDescription]);
        pieceRows.Select(e => e.Description).ShouldBe([MealDescription]);
        pieceRows.Single().Id.ShouldNotBe(plannableRows.Single().Id);
    }

    [Test]
    public async Task CutBatch_UiForm_PieceFromTheCutListWithoutExpenses_GetsTheParentsExpenses()
    {
        var (plannableId, sealedOrderId) = await PostSealedOrderAsync("UI_EXP_LIST", _customerA, MealExpense());
        var first = await StoredResourceAsync(plannableId);
        first.DefaultExpenses = [];
        first.Status = ShiftStatus.SplitShift;
        first.UntilDate = new DateOnly(2026, 6, 30);
        var second = CloneForUi(first, Guid.NewGuid());
        second.FromDate = new DateOnly(2026, 7, 1);
        second.UntilDate = null;

        await _cutFacade.ProcessBatchCutsAsync(
        [
            new CutOperation { Type = UpdateOperation, ParentId = sealedOrderId.ToString(), Data = first },
            new CutOperation { Type = CreateOperation, ParentId = plannableId.ToString(), Data = second }
        ]);

        (await ExpensesOfAsync(plannableId)).Select(e => e.Description).ShouldBe([MealDescription]);
        (await ExpensesOfAsync(second.Id)).Select(e => e.Description).ShouldBe([MealDescription]);
    }

    private async Task ShouldEachCarryMealAndParkingAsync(IReadOnlyCollection<Guid> pieceIds)
    {
        var allIds = new List<Guid>();
        foreach (var pieceId in pieceIds)
        {
            var rows = await ExpensesOfAsync(pieceId);
            rows.OrderBy(e => e.Description, StringComparer.Ordinal)
                .Select(e => $"{e.Description}|{e.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{e.Taxable}")
                .ShouldBe(
                [
                    $"{MealDescription}|12.50|False",
                    $"{ParkingDescription}|5.00|True"
                ], $"piece {pieceId}");
            allIds.AddRange(rows.Select(e => e.Id));
        }

        allIds.Distinct().Count().ShouldBe(allIds.Count, "every piece owns its rows; no row is shared");
    }

    private CutShiftSkill CutSkill() =>
        new(new ShiftTimeCutPlanner(_shiftRepository, new ShiftReader(_context), _allVisible), _cutFacade);

    private async Task<List<ShiftExpenses>> ExpensesOfAsync(Guid shiftId)
    {
        _context.ChangeTracker.Clear();
        return await _context.Set<ShiftExpenses>().AsNoTracking()
            .Where(e => e.ShiftId == shiftId)
            .ToListAsync();
    }

    private async Task AddExpenseAsync(Guid shiftId, decimal amount, string description, bool taxable)
    {
        _context.Set<ShiftExpenses>().Add(new ShiftExpenses
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            Amount = amount,
            Description = description,
            Taxable = taxable
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static ShiftExpensesResource MealExpense() => new()
    {
        Amount = MealAmount,
        Description = MealDescription,
        Taxable = false
    };

    private async Task<(Guid PlannableId, Guid SealedOrderId)> PostSealedOrderAsync(
        string suffix, Guid customerId, params ShiftExpensesResource[] expenses)
    {
        var order = NewResource(suffix, ShiftStatus.SealedOrder, customerId);
        order.DefaultExpenses = expenses.ToList();
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(order), CancellationToken.None);
        _context.ChangeTracker.Clear();
        created.ShouldNotBeNull();
        created!.Status.ShouldBe(ShiftStatus.OriginalShift);
        return (created.Id, created.OriginalId!.Value);
    }

    private async Task<ShiftResource> StoredResourceAsync(Guid shiftId)
    {
        var shift = await _shiftRepository.Get(shiftId);
        shift.ShouldNotBeNull();
        var resource = _scheduleMapper.ToShiftResource(shift);
        _context.ChangeTracker.Clear();
        return resource;
    }

    private static ShiftResource CloneForUi(ShiftResource parent, Guid id)
    {
        var clone = new ScheduleMapper().ToShiftResource(new ScheduleMapper().ToShiftEntity(parent));
        clone.Id = id;
        clone.Name = parent.Name + "_PIECE";
        clone.ParentId = parent.Id;
        clone.Status = ShiftStatus.SplitShift;
        return clone;
    }

    private async Task<List<Shift>> PiecesOfAsync(Guid sealedOrderId)
    {
        _context.ChangeTracker.Clear();
        return await _context.Shift.AsNoTracking()
            .Where(s => s.OriginalId == sealedOrderId && s.Status == ShiftStatus.SplitShift)
            .ToListAsync();
    }

    private async Task<Shift> ReloadAsync(Guid shiftId)
    {
        _context.ChangeTracker.Clear();
        return await _context.Shift.AsNoTracking().FirstAsync(s => s.Id == shiftId);
    }

    private static ShiftResource NewResource(string suffix, ShiftStatus status, Guid customerId) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Abbreviation = "SHO",
        Description = "Shift hierarchy ownership integration test",
        Status = status,
        ShiftType = ShiftType.IsTask,
        ClientId = customerId,
        FromDate = OrderFrom,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        WorkTime = 8m,
        IsMonday = true,
        IsTuesday = true,
        IsWednesday = true,
        IsThursday = true,
        IsFriday = true,
        Quantity = 1,
        SumEmployees = 1
    };

    private static Client NewCustomer(Guid id, string suffix) => new()
    {
        Id = id,
        Name = Prefix + suffix,
        FirstName = "Billing",
        Company = Prefix + suffix,
        Type = EntityTypeEnum.Customer,
        Gender = GenderEnum.Female,
        LegalEntity = true
    };

    private static SkillExecutionContext SkillContext() => new()
    {
        UserId = Guid.Empty,
        TenantId = Guid.Empty,
        UserName = "integration-test",
        UserPermissions = new List<string>()
    };

    private void BuildPipeline()
    {
        _scheduleMapper = new ScheduleMapper();
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
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
            _scheduleMapper,
            TestCompanyClock.Utc());

        _allVisible = Substitute.For<IShiftVisibilityGuard>();
        _allVisible.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _allVisible.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>()).Returns(true);

        var requirementRepository = new ShiftRequiredQualificationRepository(
            _context, Substitute.For<ILogger<ShiftRequiredQualification>>());
        _cutFacade = new ShiftCutFacade(
            _shiftRepository,
            requirementRepository,
            new ShiftTreeService(_context, Substitute.For<ILogger<ShiftTreeService>>()),
            new ShiftResetService(_shiftRepository, Substitute.For<ILogger<ShiftResetService>>()),
            shiftValidator,
            _scheduleMapper,
            unitOfWork,
            _allVisible,
            new ShiftExpensesRepository(_context, Substitute.For<ILogger<ShiftExpenses>>()),
            Substitute.For<ILogger<ShiftCutFacade>>());

        var sealing = Substitute.For<IOrderSealingService>();
        sealing.CollectMissingRequirements(Arg.Any<Shift>()).Returns(Array.Empty<string>());
        _postHandler = new ShiftPostHandler(
            _shiftRepository, _scheduleMapper, unitOfWork, Substitute.For<IDefaultShiftMacroResolver>(),
            sealing, _allVisible, Substitute.For<IClientReferenceGuard>(), Substitute.For<ILogger<ShiftPostHandler>>());
        _putHandler = new ShiftPutHandler(
            _shiftRepository, requirementRepository, _scheduleMapper, unitOfWork, _allVisible,
            Substitute.For<IClientReferenceGuard>(), Substitute.For<ILogger<ShiftPutHandler>>());
    }

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        var sql = $@"
            DELETE FROM shift_required_qualification WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM client WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
