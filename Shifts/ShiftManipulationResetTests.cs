// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The reset of a cut family and the billing rule for shifts without a customer, against PostgreSQL through the real facade
/// and loaders: from the first day the family goes back to one plannable shift with the content of the sealed row; from a
/// later day every piece that begins before it stays, with or without work, and ends the day before, the pieces from the day
/// on go, and the plannable shift starts on the day; every refusal leaves all rows as they were, a scenario refusal included;
/// a container cannot be cut or reset; the billing export leaves a shift without a customer out.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Exports;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Shifts;

public partial class ShiftManipulationIntegrationTests
{
    private static readonly DateOnly ResetOrderFrom = new(2026, 1, 1);
    private static readonly DateOnly ResetDay = new(2026, 4, 1);
    private static readonly DateOnly ResetLaterPieceFrom = new(2026, 7, 1);

    private async Task<(Guid SealedOrder, Guid First, Guid Later)> CreateUiCutFamilyAsync(string suffix, Action<ShiftResource>? arrangeOrder = null)
    {
        var order = CreateTestShiftResource(suffix, ShiftStatus.SealedOrder, fromDate: ResetOrderFrom);
        arrangeOrder?.Invoke(order);
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(order), CancellationToken.None);
        var plannable = created!.Id;
        var sealedOrder = created.OriginalId!.Value;
        _context.ChangeTracker.Clear();

        var first = await LoadThroughTheWireAsync(plannable);
        first.Status = ShiftStatus.SplitShift;
        first.UntilDate = ResetLaterPieceFrom.AddDays(-1);
        var later = await LoadThroughTheWireAsync(plannable);
        later.Id = Guid.NewGuid();
        later.Status = ShiftStatus.SplitShift;
        later.FromDate = ResetLaterPieceFrom;
        later.UntilDate = null;
        later.DefaultExpenses = [];

        await _batchCutsHandler.Handle(
            new PostBatchCutsCommand(
            [
                new CutOperation { Type = CutOperation.UpdateType, ParentId = string.Empty, Data = first },
                new CutOperation { Type = CutOperation.CreateType, ParentId = sealedOrder.ToString(), Data = later }
            ]),
            CancellationToken.None);
        _context.ChangeTracker.Clear();
        return (sealedOrder, first.Id, later.Id);
    }

    private async Task<List<(Guid Id, ShiftStatus Status, DateOnly? Until, bool Deleted)>> FamilySnapshotAsync(Guid sealedOrder)
    {
        _context.ChangeTracker.Clear();
        var familyIds = await _context.Shift.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == sealedOrder || (s.OriginalId == sealedOrder && s.ScenarioSourceShiftId == null))
            .Select(s => s.Id)
            .ToListAsync();
        var rows = await _context.Shift.IgnoreQueryFilters().AsNoTracking()
            .Where(s => familyIds.Contains(s.Id) || (s.ScenarioSourceShiftId != null && familyIds.Contains(s.ScenarioSourceShiftId.Value)))
            .Select(s => new { s.Id, s.Status, s.UntilDate, s.IsDeleted })
            .ToListAsync();
        return rows.Select(s => (s.Id, s.Status, s.UntilDate, s.IsDeleted)).OrderBy(s => s.Id).ToList();
    }

    private async Task<ShiftResetRefusedException> ResetIsRefusedAsync(Guid sealedOrder, DateOnly day, string reason)
    {
        var before = await FamilySnapshotAsync(sealedOrder);

        var thrown = await Should.ThrowAsync<ShiftResetRefusedException>(() =>
            _resetCutsHandler.Handle(new PostResetCutsCommand(sealedOrder, day), CancellationToken.None));

        thrown.ConflictCode.ShouldBe(ShiftResetRefusedException.ErrorCode);
        thrown.ConflictDetails![ShiftResetRefusedException.ReasonKey].ShouldBe(reason);
        (await FamilySnapshotAsync(sealedOrder)).ShouldBe(before);
        return thrown;
    }

    private GetResetDateRangeQueryHandler ResetRangeHandler() => new(
        _shiftRepository,
        AllShiftsVisible(),
        Substitute.For<IVariantShiftWriteGuard>(),
        _orderFamilyEndRepository,
        Substitute.For<ILogger<GetResetDateRangeQueryHandler>>());

    [Test]
    public async Task Reset_FromTheFirstDay_WithoutCustomerAndWork_EndsAsOnePlannableRow_WithTheContentOfTheSealedRow()
    {
        var family = await CreateUiCutFamilyAsync("Reset_Origin", order =>
        {
            order.StartShift = new TimeOnly(6, 30);
            order.EndShift = new TimeOnly(14, 15);
            order.Description = "Refuelling";
            order.Abbreviation = "RFL";
            order.Quantity = 2;
            order.Groups = [new() { Id = _groupAId }];
            order.DefaultExpenses =
            [
                new ShiftExpensesResource { Amount = 7.5m, Description = $"{TestShiftPrefix}Toll", Taxable = false },
                new ShiftExpensesResource { Amount = 20m, Description = $"{TestShiftPrefix}Allowance", Taxable = true }
            ];
        });
        var editedExpense = await _context.Set<ShiftExpenses>().FirstAsync(e => e.ShiftId == family.First);
        editedExpense.Amount = 999m;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await _resetCutsHandler.Handle(new PostResetCutsCommand(family.SealedOrder, ResetOrderFrom), CancellationToken.None);

        var plannable = await StoredAsync(result.ShouldHaveSingleItem().Id);
        var order = await StoredAsync(family.SealedOrder);
        plannable.Status.ShouldBe(ShiftStatus.OriginalShift);
        plannable.ClientId.ShouldBeNull();
        plannable.FromDate.ShouldBe(ResetOrderFrom, "from the first day the whole origin is back");
        plannable.UntilDate.ShouldBeNull();
        (plannable.StartShift, plannable.EndShift, plannable.Description, plannable.Abbreviation, plannable.Quantity)
            .ShouldBe((order.StartShift, order.EndShift, order.Description, order.Abbreviation, order.Quantity));
        plannable.ShiftType.ShouldBe(order.ShiftType);
        plannable.MacroId.ShouldBe(order.MacroId);
        plannable.GroupItems.Select(item => item.GroupId).ShouldBe([_groupAId]);
        plannable.ShiftExpenses.Select(e => (e.Amount, e.Description, e.Taxable)).OrderBy(e => e.Amount)
            .ShouldBe([(7.5m, $"{TestShiftPrefix}Toll", false), (20m, $"{TestShiftPrefix}Allowance", true)]);
        plannable.ShiftExpenses.Select(e => e.Id).ShouldNotContain(id => order.ShiftExpenses.Any(stored => stored.Id == id));
        order.ShiftExpenses.Count.ShouldBe(2, "the sealed row keeps its rows");

        var pieces = await _context.Shift.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == family.First || s.Id == family.Later).ToListAsync();
        pieces.Count.ShouldBe(2);
        pieces.ShouldAllBe(piece => piece.IsDeleted);
    }

    [Test]
    public async Task Reset_WithoutWork_KeepsThePieceBeforeTheDay_EndsItTheDayBefore_DropsTheLaterOne_AndStartsOnTheDay()
    {
        var family = await CreateUiCutFamilyAsync("Reset_KeepEarlier");

        var result = await _resetCutsHandler.Handle(new PostResetCutsCommand(family.SealedOrder, ResetDay), CancellationToken.None);

        result.Count.ShouldBe(2);
        var kept = await StoredAsync(family.First);
        kept.Status.ShouldBe(ShiftStatus.SplitShift);
        kept.IsDeleted.ShouldBeFalse();
        kept.FromDate.ShouldBe(ResetOrderFrom);
        kept.UntilDate.ShouldBe(ResetDay.AddDays(-1), "the piece reached over the day and ends the day before");
        var plannable = result.Single(row => row.Status == ShiftStatus.OriginalShift);
        plannable.FromDate.ShouldBe(ResetDay, "the chosen day counts without work too");
        plannable.UntilDate.ShouldBeNull();
        (await _context.Shift.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == family.Later)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task Reset_WithoutWork_TheKeptPieceCanStillBePlannedUpToTheDayBefore()
    {
        var family = await CreateUiCutFamilyAsync("Reset_PlanKept");
        await _resetCutsHandler.Handle(new PostResetCutsCommand(family.SealedOrder, ResetDay), CancellationToken.None);

        await AddWorkOnAsync(family.First, ResetDay.AddDays(-1));

        (await _context.Work.AsNoTracking().CountAsync(w => w.ShiftId == family.First && !w.IsDeleted)).ShouldBe(1);
        (await ResetRangeHandler().Handle(new GetResetDateRangeQuery(family.SealedOrder), CancellationToken.None))
            .EarliestResetDate.ShouldBe(ResetDay);
    }

    [Test]
    public async Task Reset_WithAWorkOnTheDay_IsRefusedWithItsDate_AndChangesNothing()
    {
        var family = await CreateUiCutFamilyAsync("Reset_WorkOnDay");
        await AddWorkOnAsync(family.First, ResetDay);

        var thrown = await ResetIsRefusedAsync(family.SealedOrder, ResetDay, ShiftResetRefusedException.WorksOnOrAfterReason);

        thrown.ConflictDetails![ShiftResetRefusedException.LastWorkDateKey].ShouldBe("2026-04-01");
    }

    [Test]
    public async Task Reset_WithAWorkAfterTheDay_IsRefused_AndChangesNothing()
    {
        var family = await CreateUiCutFamilyAsync("Reset_WorkAfter");
        await AddWorkOnAsync(family.Later, ResetLaterPieceFrom);

        await ResetIsRefusedAsync(family.SealedOrder, ResetDay, ShiftResetRefusedException.WorksOnOrAfterReason);
    }

    [Test]
    public async Task Reset_WithWorkBeforeTheDay_KeepsThatPieceClosed_DropsTheLaterOne_AndStartsOnTheDay()
    {
        var family = await CreateUiCutFamilyAsync("Reset_WorkBefore");
        await AddWorkOnAsync(family.First, ResetDay.AddDays(-1));

        var result = await _resetCutsHandler.Handle(new PostResetCutsCommand(family.SealedOrder, ResetDay), CancellationToken.None);

        result.Count.ShouldBe(2);
        var kept = await StoredAsync(family.First);
        kept.Status.ShouldBe(ShiftStatus.SplitShift);
        kept.UntilDate.ShouldBe(ResetDay.AddDays(-1));
        var plannable = result.Single(row => row.Status == ShiftStatus.OriginalShift);
        plannable.FromDate.ShouldBe(ResetDay);
        (await _context.Shift.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == family.Later)).IsDeleted.ShouldBeTrue();
        var works = await _context.Work.AsNoTracking().CountAsync(w => w.ShiftId == family.First && !w.IsDeleted);
        works.ShouldBe(1);
    }

    [Test]
    public async Task Reset_Twice_TheSecondIsRefused_AndThePieceBeforeAndOnePlannableShiftStay()
    {
        var family = await CreateUiCutFamilyAsync("Reset_Twice");
        await _resetCutsHandler.Handle(new PostResetCutsCommand(family.SealedOrder, ResetDay), CancellationToken.None);

        await ResetIsRefusedAsync(family.SealedOrder, ResetDay, ShiftResetRefusedException.NothingToResetReason);

        var live = (await FamilySnapshotAsync(family.SealedOrder)).Where(row => !row.Deleted && row.Id != family.SealedOrder).ToList();
        live.Count.ShouldBe(2);
        live.Single(row => row.Status == ShiftStatus.OriginalShift).Until.ShouldBeNull();
        live.Single(row => row.Status == ShiftStatus.SplitShift).Until.ShouldBe(ResetDay.AddDays(-1));
    }

    [Test]
    public async Task Reset_AtAnEarlierDayThanAFormerReset_RemovesThePlannableShiftOfTheFormerReset_AndStartsOnTheEarlierDay()
    {
        var family = await CreateUiCutFamilyAsync("Reset_Earlier");
        var first = await _resetCutsHandler.Handle(new PostResetCutsCommand(family.SealedOrder, ResetDay), CancellationToken.None);
        var formerPlannable = first.Single(row => row.Status == ShiftStatus.OriginalShift).Id;
        var earlierDay = ResetDay.AddDays(-31);

        var result = await _resetCutsHandler.Handle(new PostResetCutsCommand(family.SealedOrder, earlierDay), CancellationToken.None);

        result.Count.ShouldBe(2);
        (await StoredAsync(family.First)).UntilDate.ShouldBe(earlierDay.AddDays(-1));
        result.Single(row => row.Status == ShiftStatus.OriginalShift).FromDate.ShouldBe(earlierDay);
        (await _context.Shift.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == formerPlannable)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task Reset_WithAScenarioCloneHoldingWorkOnTheDay_IsRefusedWithoutADateOfTheWork_AndChangesNothing()
    {
        var family = await CreateUiCutFamilyAsync("Reset_ScenarioWork");
        await AddWorkOnAsync(family.First, ResetDay.AddDays(-1));
        var clone = await CloneIntoScenarioAsync(family.First);
        await AddScenarioWorkAsync(clone.CloneId, clone.Token, ResetDay);

        var thrown = await ResetIsRefusedAsync(family.SealedOrder, ResetDay, ShiftResetRefusedException.ScenarioWorksOnOrAfterReason);

        thrown.ConflictDetails!.ContainsKey(ShiftResetRefusedException.LastWorkDateKey).ShouldBeFalse();
    }

    [Test]
    public async Task Reset_WithAScenarioCloneWithoutWorkOnAPieceToDelete_IsRefusedWithoutADate_AndChangesNothing()
    {
        var family = await CreateUiCutFamilyAsync("Reset_ScenarioClone");
        await CloneIntoScenarioAsync(family.Later);

        var thrown = await ResetIsRefusedAsync(family.SealedOrder, ResetDay, ShiftResetRefusedException.RowsInUseReason);

        thrown.ConflictDetails!.Keys.ShouldBe([ShiftResetRefusedException.ReasonKey]);
    }

    [Test]
    public async Task Reset_WithAScenarioCloneOfAKeptPiece_AndNoWorkFromTheDay_DoesNotBlock()
    {
        var family = await CreateUiCutFamilyAsync("Reset_CloneOfKept");
        await AddWorkOnAsync(family.First, ResetDay.AddDays(-1));
        var clone = await CloneIntoScenarioAsync(family.First);
        await AddScenarioWorkAsync(clone.CloneId, clone.Token, ResetDay.AddDays(-1));

        var result = await _resetCutsHandler.Handle(new PostResetCutsCommand(family.SealedOrder, ResetDay), CancellationToken.None);

        result.Count.ShouldBe(2);
        (await StoredAsync(family.First)).UntilDate.ShouldBe(ResetDay.AddDays(-1));
    }

    [Test]
    public async Task ResetDateRange_ReportsTheOrderStartWithoutWork_AndTheDayAfterTheLastWorkWithWork()
    {
        var family = await CreateUiCutFamilyAsync("Reset_Range");

        (await ResetRangeHandler().Handle(new GetResetDateRangeQuery(family.SealedOrder), CancellationToken.None))
            .EarliestResetDate.ShouldBe(ResetOrderFrom);

        await AddWorkOnAsync(family.First, new DateOnly(2026, 2, 10));

        (await ResetRangeHandler().Handle(new GetResetDateRangeQuery(family.SealedOrder), CancellationToken.None))
            .EarliestResetDate.ShouldBe(new DateOnly(2026, 2, 11));
    }

    [Test]
    public async Task Container_CutAndReset_AreRefused_AndWriteNothing()
    {
        var created = await _postHandler.Handle(
            new PostCommand<ShiftResource>(CreateTestShiftResource("Reset_Container", ShiftStatus.SealedOrder, fromDate: ResetOrderFrom)),
            CancellationToken.None);
        var sealedOrder = created!.OriginalId!.Value;
        await _context.Shift.Where(s => s.Id == sealedOrder || s.OriginalId == sealedOrder)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.ShiftType, ShiftType.IsContainer));
        _context.ChangeTracker.Clear();
        var piece = CreateTestShiftResource("Reset_Container_Piece", ShiftStatus.SplitShift, fromDate: ResetLaterPieceFrom);

        await Should.ThrowAsync<BadRequestException>(() => _batchCutsHandler.Handle(
            new PostBatchCutsCommand([new CutOperation { Type = CutOperation.CreateType, ParentId = created.Id.ToString(), Data = piece }]),
            CancellationToken.None));
        _context.ChangeTracker.Clear();
        await Should.ThrowAsync<BadRequestException>(() =>
            _resetCutsHandler.Handle(new PostResetCutsCommand(sealedOrder, ResetDay), CancellationToken.None));

        _context.ChangeTracker.Clear();
        (await _context.Shift.AsNoTracking().CountAsync(s => s.OriginalId == sealedOrder)).ShouldBe(1);
        (await _context.Shift.AsNoTracking().AnyAsync(s => s.Id == piece.Id)).ShouldBeFalse();
    }

    [Test]
    public async Task BillingExport_LeavesAShiftWithoutACustomerOut_AndKeepsAnOrderWithOne()
    {
        var withoutCustomer = await _postHandler.Handle(
            new PostCommand<ShiftResource>(CreateTestShiftResource("Export_NoCustomer", ShiftStatus.SealedOrder, fromDate: ResetOrderFrom)),
            CancellationToken.None);
        var billed = CreateTestShiftResource("Export_Customer", ShiftStatus.SealedOrder, fromDate: ResetOrderFrom);
        billed.ClientId = _customerId;
        var withCustomer = await _postHandler.Handle(new PostCommand<ShiftResource>(billed), CancellationToken.None);
        var withEmployee = CreateTestShiftResource("Export_Employee", ShiftStatus.SealedOrder, fromDate: ResetOrderFrom);
        withEmployee.ClientId = _employeeId;
        var employeeOrder = await _postHandler.Handle(new PostCommand<ShiftResource>(withEmployee), CancellationToken.None);
        _context.ChangeTracker.Clear();
        var noCustomerId = withoutCustomer!.OriginalId!.Value;
        var customerId = withCustomer!.OriginalId!.Value;
        var employeeId = employeeOrder!.OriginalId!.Value;
        var resolver = new ShiftDescendantResolver(_context);

        var list = await new SealedOrderListLoader(_context, resolver)
            .LoadAsync(ResetOrderFrom, ResetOrderFrom, null, TestShiftPrefix, null);
        var details = new SealedOrderDetailsLoader(_context, resolver, Substitute.For<IPeriodClosedEntryFilter>());
        var export = await new OrderExportDataLoader(_context, resolver, TestCompanyClock.Utc())
            .LoadAsync([noCustomerId, customerId, employeeId]);

        list.Select(order => order.Id).ShouldBe([customerId]);
        (await details.LoadAsync(noCustomerId, null, null)).ShouldBeNull();
        (await details.LoadAsync(employeeId, null, null)).ShouldBeNull();
        (await details.LoadAsync(customerId, null, null)).ShouldNotBeNull();
        export.Orders.Select(order => order.OrderShiftId).ShouldBe([customerId]);
    }
}
