// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of promoting variant (key) shifts through the real create, accept and reject handlers, the real
/// clone and promote collaborators, the real cut facade and tree numbering: an accept turns every live row of the linked
/// keys into a real shift with status, customer and links unchanged, the keys go Accepted, the links stay, works of a clone
/// land on exactly its key shift (also through a scenario built on the key scenario), and the promoted trees carry the
/// values a real cut of the same shape carries. Before the accept neither the payroll loader nor a wizard run without a
/// shift list sees anything of a key. A sibling on a consumed key is refused and stays discardable without touching the
/// now real rows. Open slots block nothing, a failed numbering costs no data and can be repeated, the next real cut repairs
/// the tree it touches only, and a scenario without keys changes nothing around a key next to it. Key families are built by
/// the real cut facade, so they follow the stored tree convention. Only INTEGRATION_TEST_ rows are written and removed; all
/// dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Exports;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class VariantPromotionSeamTests : VariantPromotionSeamTestBase
{
    [Test]
    public async Task Accept_TwoKeys_TurnsEveryLiveRowReal_WithStatusCustomerAndLinksUnchanged()
    {
        var cutKey = await CreateVariantAsync("CUTKEY");
        var uncutKey = await CreateVariantAsync("UNCUTKEY");
        var cut = await CreateCutOrderAsync("CUT", cutKey.Token);
        var uncut = await CreateOrderAsync("UNCUT", uncutKey.Token);
        var rowIds = cut.Ids.Concat([uncut.SealedOrder.Id, uncut.Plannable.Id]).ToList();
        var before = await SnapshotAsync(rowIds);
        var scenario = await CreateScenarioAsync(cutKey.Id, uncutKey.Id);

        var result = await AcceptAsync(scenario.Id);

        result.Accepted.ShouldBeTrue();
        (await SnapshotAsync(rowIds)).ShouldBe(before);
        var rows = await LoadRowsAsync(rowIds);
        rows.ShouldAllBe(row => row.AnalyseToken == null && row.ScenarioSourceShiftId == null && !row.IsDeleted);
        rows.ShouldAllBe(row => row.ClientId == _customer.Id);
        rows.ShouldAllBe(row => row.GroupItems.Count == 1);
        rows.SelectMany(row => row.GroupItems).ShouldAllBe(item => item.AnalyseToken == null);
        rows.Single(row => row.Id == cut.SealedOrder).Status.ShouldBe(ShiftStatus.SealedOrder);
        rows.Single(row => row.Id == uncut.Plannable.Id).Status.ShouldBe(ShiftStatus.OriginalShift);
        rows.Single(row => row.Id == uncut.Plannable.Id).OriginalId.ShouldBe(uncut.SealedOrder.Id);
        rows.Single(row => row.Id == cut.Child).ParentId.ShouldBe(cut.FirstPiece);
        rows.Where(row => row.Id == cut.FirstPiece || row.Id == cut.SecondPiece || row.Id == cut.Child)
            .ShouldAllBe(row => row.Status == ShiftStatus.SplitShift && row.OriginalId == cut.SealedOrder);
    }

    [Test]
    public async Task Accept_ConsumesTheKeys_AndKeepsTheLinksAsEvidence_AndDiscardsTheClones()
    {
        var variant = await CreateVariantAsync("KEY");
        var order = await CreateOrderAsync("ORDER", variant.Token);
        var scenario = await CreateScenarioAsync(variant.Id);

        await AcceptAsync(scenario.Id);

        var stored = await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == variant.Id);
        stored.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
        (await _context.Set<AnalyseScenarioVariant>().AsNoTracking()
            .CountAsync(link => link.ScenarioToken == scenario.Token && !link.IsDeleted)).ShouldBe(1);
        var clones = await _context.Shift.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.AnalyseToken == scenario.Token && s.ScenarioSourceShiftId == order.Plannable.Id)
            .ToListAsync();
        clones.Count.ShouldBe(1);
        clones[0].IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task Accept_WorksOfAClone_LandOnExactlyItsKeyShift()
    {
        var cutKey = await CreateVariantAsync("CUTKEY");
        var uncutKey = await CreateVariantAsync("UNCUTKEY");
        var cut = await CreateCutOrderAsync("CUT", cutKey.Token);
        var uncut = await CreateOrderAsync("UNCUT", uncutKey.Token);
        var scenario = await CreateScenarioAsync(cutKey.Id, uncutKey.Id);
        var workOnPiece = await AddScenarioWorkAsync(scenario.Token, cut.SecondPiece, WorkDate);
        var workOnCopy = await AddScenarioWorkAsync(scenario.Token, uncut.Plannable.Id, SecondWorkDate);

        await AcceptAsync(scenario.Id);

        await AssertRealWorkOnAsync(workOnPiece, cut.SecondPiece);
        await AssertRealWorkOnAsync(workOnCopy, uncut.Plannable.Id);
    }

    [Test]
    public async Task Accept_AScenarioOnTheKeyScenario_LandsTheWorksOnTheKeyShifts_WithoutADuplicate()
    {
        var cutKey = await CreateVariantAsync("CUTKEY");
        var uncutKey = await CreateVariantAsync("UNCUTKEY");
        var cut = await CreateCutOrderAsync("CUT", cutKey.Token);
        var uncut = await CreateOrderAsync("UNCUT", uncutKey.Token);
        var source = await CreateScenarioAsync(cutKey.Id, uncutKey.Id);
        await AddScenarioWorkAsync(source.Token, cut.Child, WorkDate);
        await AddScenarioWorkAsync(source.Token, uncut.Plannable.Id, SecondWorkDate);
        var chained = await CreateChainedScenarioAsync(source);

        await AcceptAsync(chained.Id);

        var realWorks = await _context.Work.AsNoTracking()
            .Where(w => w.ClientId == _worker.Id && w.AnalyseToken == null && !w.IsDeleted)
            .ToListAsync();
        realWorks.Count.ShouldBe(ExpectedRealWorks);
        realWorks.Single(w => w.CurrentDate == WorkDate).ShiftId.ShouldBe(cut.Child);
        realWorks.Single(w => w.CurrentDate == SecondWorkDate).ShiftId.ShouldBe(uncut.Plannable.Id);
    }

    [Test]
    public async Task Accept_NumbersThePromotedTrees_LikeARealCutOfTheSameShape()
    {
        var variant = await CreateVariantAsync("KEY");
        var key = await CreateCutOrderAsync("KEYORDER", variant.Token);
        var real = await CreateCutOrderAsync("REALORDER", null);
        var scenario = await CreateScenarioAsync(variant.Id);

        (await LoadRowsAsync(key.Ids)).ShouldAllBe(row => row.Lft == null && row.Rgt == null);

        await AcceptAsync(scenario.Id);

        var keyRows = (await LoadRowsAsync(key.Ids)).ToDictionary(row => row.Id);
        var realRows = (await LoadRowsAsync(real.Ids)).ToDictionary(row => row.Id);
        AssertSameValues(keyRows[key.FirstPiece], realRows[real.FirstPiece]);
        AssertSameValues(keyRows[key.Child], realRows[real.Child]);
        AssertSameValues(keyRows[key.SecondPiece], realRows[real.SecondPiece]);
        keyRows[key.FirstPiece].Lft.ShouldBe(FirstLeft);
        keyRows[key.FirstPiece].Rgt.ShouldBe(FirstLeft + ParentWidth);
        keyRows[key.Child].Lft.ShouldBe(FirstLeft + LeafWidth);
        keyRows[key.Child].Rgt.ShouldBe(FirstLeft + LeafWidth + LeafWidth);
        keyRows[key.SecondPiece].Lft.ShouldBe(FirstLeft);
        keyRows[key.SecondPiece].Rgt.ShouldBe(FirstLeft + LeafWidth);
        keyRows[key.SealedOrder].Lft.ShouldBe(realRows[real.SealedOrder].Lft);
    }

    [Test]
    public async Task Accept_NumbersATreeInTheOrderRootConvention_AsAForest()
    {
        var variant = await CreateVariantAsync("KEY");
        var order = NewShift("FOREST", ShiftStatus.SealedOrder, null, variant.Token);
        await AddShiftsAsync(order);
        var firstPiece = NewShift("FOREST", ShiftStatus.SplitShift, order.Id, variant.Token);
        firstPiece.RootId = order.Id;
        firstPiece.FromDate = PeriodFrom;
        firstPiece.UntilDate = FirstPieceUntil;
        var secondPiece = NewShift("FOREST", ShiftStatus.SplitShift, order.Id, variant.Token);
        secondPiece.RootId = order.Id;
        secondPiece.FromDate = SecondPieceFrom;
        await AddShiftsAsync(firstPiece, secondPiece);
        var scenario = await CreateScenarioAsync(variant.Id);

        await AcceptAsync(scenario.Id);

        var rows = (await LoadRowsAsync([firstPiece.Id, secondPiece.Id])).ToDictionary(row => row.Id);
        rows[firstPiece.Id].Lft.ShouldBe(FirstLeft);
        rows[firstPiece.Id].Rgt.ShouldBe(FirstLeft + LeafWidth);
        rows[secondPiece.Id].Lft.ShouldBe(FirstLeft + LeafWidth + LeafWidth);
        rows[secondPiece.Id].Rgt.ShouldBe(FirstLeft + LeafWidth + LeafWidth + LeafWidth);
    }

    [Test]
    public async Task Accept_LeavesASoftDeletedKeyRowDeleted_AndOutOfTheRoots()
    {
        var variant = await CreateVariantAsync("KEY");
        var order = await CreateOrderAsync("ORDER", variant.Token);
        var deleted = NewShift("DELETED", ShiftStatus.SplitShift, order.SealedOrder.Id, variant.Token);
        deleted.RootId = deleted.Id;
        deleted.IsDeleted = true;
        await AddShiftsAsync(deleted);
        var scenario = await CreateScenarioAsync(variant.Id);

        (await _scenarioService.GetVariantTreeRootIdsAsync(scenario.Token, CancellationToken.None)).ShouldBeEmpty();

        await AcceptAsync(scenario.Id);

        var stored = await _context.Shift.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == deleted.Id);
        stored.IsDeleted.ShouldBeTrue();
        stored.AnalyseToken.ShouldBe(variant.Token);
    }

    [Test]
    public async Task PromotedOrder_IsFoundByTheRealReaders_AndCutsLikeARealOrder()
    {
        var variant = await CreateVariantAsync("KEY");
        var cut = await CreateCutOrderAsync("ORDER", variant.Token);
        var scenario = await CreateScenarioAsync(variant.Id);

        (await _shiftRepository.GetSealedOrder(cut.SealedOrder)).ShouldBeNull();

        await AcceptAsync(scenario.Id);

        var sealedOrder = await _shiftRepository.GetSealedOrder(cut.SealedOrder);
        sealedOrder.ShouldNotBeNull();
        sealedOrder.ClientId.ShouldBe(_customer.Id);
        _context.ChangeTracker.Clear();
        (await _shiftRepository.CutList(cut.SealedOrder)).Select(row => row.Id)
            .ShouldBe(new[] { cut.FirstPiece, cut.Child, cut.SecondPiece }, ignoreOrder: true);

        var newChild = await PieceOfAsync(cut.SecondPiece, Guid.NewGuid(), SecondPieceFrom, SecondChildUntil);
        await _facade.ProcessBatchCutsAsync([Create(cut.SecondPiece, newChild)]);
        _context.ChangeTracker.Clear();

        var rows = (await LoadRowsAsync([cut.SecondPiece, newChild.Id])).ToDictionary(row => row.Id);
        rows[newChild.Id].AnalyseToken.ShouldBeNull();
        rows[newChild.Id].ParentId.ShouldBe(cut.SecondPiece);
        rows[newChild.Id].RootId.ShouldBe(cut.SecondPiece);
        rows[newChild.Id].OriginalId.ShouldBe(cut.SealedOrder);
        rows[cut.SecondPiece].Rgt.ShouldBe(FirstLeft + ParentWidth);
        rows[newChild.Id].Lft.ShouldBe(FirstLeft + LeafWidth);
    }

    [Test]
    public async Task PayrollLoader_SeesNothingOfAKeyBeforeTheAccept_AndTheWorkAfterIt()
    {
        var variant = await CreateVariantAsync("KEY");
        var order = await CreateOrderAsync("ORDER", variant.Token);
        var scenario = await CreateScenarioAsync(variant.Id);
        var workId = await AddScenarioWorkAsync(scenario.Token, order.Plannable.Id, WorkDate);
        await SetLockLevelAsync(workId, WorkLockLevel.Closed);

        var before = await new PayrollExportDataLoader(_context).LoadAsync(PeriodFrom, PeriodUntil, new[] { _worker.Id });

        before.Employees.ShouldBeEmpty();

        await SetLockLevelAsync(workId, WorkLockLevel.None);
        await AcceptAsync(scenario.Id);
        await SetLockLevelAsync(workId, WorkLockLevel.Closed);

        var after = await new PayrollExportDataLoader(_context).LoadAsync(PeriodFrom, PeriodUntil, new[] { _worker.Id });

        var employee = after.Employees.Single(e => e.ClientId == _worker.Id);
        employee.Entries.ShouldContain(entry => entry.Kind == PayrollEntryKind.WorkHours
            && entry.Date == WorkDate
            && entry.Quantity == WorkHours);
    }

    [Test]
    public async Task WizardWithoutShiftList_SeesNoKeyShiftBeforeTheAccept_AndThePromotedShiftsAfterIt()
    {
        var variant = await CreateVariantAsync("KEY");
        var order = await CreateOrderAsync("ORDER", variant.Token);
        var real = await CreateOrderAsync("REAL", null);
        var scenario = await CreateScenarioAsync(variant.Id);
        var keyShift = order.Plannable.Id.ToString();
        var builder = new WizardShiftBuilder(_context);

        var before = await builder.BuildAsync(null, PeriodFrom, WizardUntil, null, CancellationToken.None);

        before.Select(shift => shift.Id).ShouldContain(real.Plannable.Id.ToString());
        before.Select(shift => shift.Id).ShouldNotContain(keyShift);

        await AcceptAsync(scenario.Id);

        var after = await builder.BuildAsync(null, PeriodFrom, WizardUntil, null, CancellationToken.None);

        after.Select(shift => shift.Id).ShouldContain(keyShift);
    }

    [Test]
    public async Task Sibling_OnAConsumedKey_IsRefused_AndStaysDiscardable_WithoutTouchingTheRealRows()
    {
        var variant = await CreateVariantAsync("KEY");
        var cut = await CreateCutOrderAsync("ORDER", variant.Token);
        var expenseId = await AddExpenseAsync(cut.SecondPiece);
        var accepted = await CreateScenarioAsync(variant.Id);
        var sibling = await CreateScenarioAsync(variant.Id);
        var siblingWork = await AddScenarioWorkAsync(sibling.Token, cut.SecondPiece, WorkDate);
        await AcceptAsync(accepted.Id);
        var realBefore = await SnapshotAsync(cut.Ids);

        await Should.ThrowAsync<ScenarioVariantConsumedException>(() => AcceptAsync(sibling.Id));
        await Should.ThrowAsync<ScenarioVariantConsumedException>(
            () => AcceptAsync(sibling.Id, overrideBlock: true, overrideStale: true));

        (await RejectHandler().Handle(new RejectAnalyseScenarioCommand(sibling.Id), CancellationToken.None)).ShouldBeTrue();
        _context.ChangeTracker.Clear();

        (await SnapshotAsync(cut.Ids)).ShouldBe(realBefore);
        var rows = await LoadRowsAsync(cut.Ids);
        rows.ShouldAllBe(row => !row.IsDeleted && row.AnalyseToken == null);
        rows.ShouldAllBe(row => row.GroupItems.Count == 1);
        (await _context.Set<ShiftExpenses>().AsNoTracking().CountAsync(e => e.Id == expenseId)).ShouldBe(1);
        (await _context.Shift.AsNoTracking().CountAsync(s => s.AnalyseToken == sibling.Token)).ShouldBe(0);
        (await _context.Work.AsNoTracking().CountAsync(w => w.Id == siblingWork && !w.IsDeleted)).ShouldBe(0);
        var stored = await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == sibling.Id);
        stored.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
    }

    [Test]
    public async Task OpenSlots_DoNotBlockTheAccept()
    {
        var variant = await CreateVariantAsync("KEY");
        var order = await CreateOrderAsync("ORDER", variant.Token);
        var scenario = await CreateScenarioAsync(variant.Id);

        var result = await AcceptAsync(scenario.Id);

        result.Accepted.ShouldBeTrue();
        (await LoadRowsAsync([order.Plannable.Id])).ShouldAllBe(row => row.AnalyseToken == null);
    }

    [Test]
    public async Task FailedNumbering_LosesNothing_AndCanBeRepeated()
    {
        var variant = await CreateVariantAsync("KEY");
        var cut = await CreateCutOrderAsync("ORDER", variant.Token);
        var scenario = await CreateScenarioAsync(variant.Id);
        var workId = await AddScenarioWorkAsync(scenario.Token, cut.FirstPiece, WorkDate);
        var rootIds = await _scenarioService.GetVariantTreeRootIdsAsync(scenario.Token, CancellationToken.None);
        rootIds.ShouldBe(new[] { cut.FirstPiece, cut.SecondPiece }, ignoreOrder: true);

        await using var failingProvider = FailingTreeProvider();
        var result = await AcceptAsync(scenario.Id, failingProvider.GetRequiredService<IAnalyseScenarioService>());

        result.Accepted.ShouldBeTrue();
        await AssertRealWorkOnAsync(workId, cut.FirstPiece);
        var unnumbered = await LoadRowsAsync(cut.Ids);
        unnumbered.ShouldAllBe(row => row.AnalyseToken == null && row.Lft == null && row.Rgt == null);

        await _scenarioService.RecalculateVariantTreesAsync(rootIds, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var numbered = await SnapshotValuesAsync(cut.Ids);

        await _scenarioService.RecalculateVariantTreesAsync(rootIds, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await SnapshotValuesAsync(cut.Ids)).ShouldBe(numbered);
        var rows = (await LoadRowsAsync(cut.Ids)).ToDictionary(row => row.Id);
        rows[cut.FirstPiece].Rgt.ShouldBe(FirstLeft + ParentWidth);
        rows[cut.SecondPiece].Rgt.ShouldBe(FirstLeft + LeafWidth);
    }

    [Test]
    public async Task FailedNumbering_TheNextCutRepairsTheTreeItTouches_AndOnlyThatOne()
    {
        var variant = await CreateVariantAsync("KEY");
        var cut = await CreateCutOrderAsync("ORDER", variant.Token);
        var scenario = await CreateScenarioAsync(variant.Id);
        await using var failingProvider = FailingTreeProvider();
        await AcceptAsync(scenario.Id, failingProvider.GetRequiredService<IAnalyseScenarioService>());

        var newChild = await PieceOfAsync(cut.SecondPiece, Guid.NewGuid(), SecondPieceFrom, SecondChildUntil);
        await _facade.ProcessBatchCutsAsync([Create(cut.SecondPiece, newChild)]);
        _context.ChangeTracker.Clear();

        var rows = (await LoadRowsAsync(cut.Ids)).ToDictionary(row => row.Id);
        rows[cut.SecondPiece].Lft.ShouldBe(FirstLeft);
        rows[cut.SecondPiece].Rgt.ShouldBe(FirstLeft + ParentWidth);
        rows[cut.FirstPiece].Lft.ShouldBeNull();
        rows[cut.Child].Lft.ShouldBeNull();
    }

    [Test]
    public async Task ScenarioWithoutKeys_LeavesAKeyNextToItAlone_AndNumbersNoTree()
    {
        var variant = await CreateVariantAsync("KEY");
        var key = await CreateOrderAsync("KEYORDER", variant.Token);
        var real = await CreateOrderAsync("REAL", null, SentinelLft, SentinelRgt);
        var scenario = await CreateScenarioAsync();
        var workId = await AddScenarioWorkAsync(scenario.Token, real.Plannable.Id, WorkDate);

        (await _scenarioService.GetVariantTreeRootIdsAsync(scenario.Token, CancellationToken.None)).ShouldBeEmpty();

        var result = await AcceptAsync(scenario.Id);

        result.Accepted.ShouldBeTrue();
        await AssertRealWorkOnAsync(workId, real.Plannable.Id);
        var keyRows = await LoadRowsAsync([key.SealedOrder.Id, key.Plannable.Id]);
        keyRows.ShouldAllBe(row => row.AnalyseToken == variant.Token && !row.IsDeleted);
        keyRows.SelectMany(row => row.GroupItems).ShouldAllBe(item => item.AnalyseToken == variant.Token);
        (await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == variant.Id)).Status
            .ShouldBe(AnalyseScenarioStatus.Active);
        var realRow = (await LoadRowsAsync([real.Plannable.Id])).Single();
        realRow.Lft.ShouldBe(SentinelLft);
        realRow.Rgt.ShouldBe(SentinelRgt);
    }

    [Test]
    public async Task CloneOfAKeyTheScenarioDoesNotCarry_IsAConflict_BeforeItsWorksCouldLandOnAHiddenShift()
    {
        var variant = await CreateVariantAsync("KEY");
        var order = await CreateOrderAsync("ORDER", variant.Token);
        var token = Guid.NewGuid();
        await _scenarioService.CloneScenarioDataAsync(
            _group.Id,
            PeriodFrom,
            PeriodUntil,
            token,
            new[] { order.SealedOrder.Id, order.Plannable.Id },
            null,
            CancellationToken.None,
            new HashSet<Guid> { variant.Token });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ex = await Should.ThrowAsync<ConflictException>(
            () => _scenarioService.ValidateNoAcceptConflictsAsync(token, CancellationToken.None));

        ex.Message.ShouldBe(ScenarioAcceptConflictValidator.UncarriedVariantSourceMessage);
    }
}
