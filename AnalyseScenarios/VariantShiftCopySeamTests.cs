// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of ScenarioVariantShiftCopier, the stage-only copy of existing real order families (sealed order,
/// plannable copy and cut pieces) into a variant (key). A family is copied whole and once whichever member is chosen: new
/// ids, the key token on every row and group item, Lft and Rgt reset, the scenario source empty, OriginalId, ParentId and
/// RootId pointing into the copy and never at the original, expenses and required qualifications copied by the new shift
/// id without a token, and the originals untouched. A selection that is not a real sealed order, lies outside the key
/// period or carries a group outside the key hierarchy is refused before anything is staged. A whole group copies every
/// family of its hierarchy that fits the period and counts open orders, families outside the period and families with a
/// foreign group as skipped; soft-deleted rows are ignored everywhere. Only INTEGRATION_TEST_ rows are written and removed;
/// all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class VariantShiftCopySeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARIANTCOPY_";
    private const int PieceCount = 2;
    private const int OrderAndPlannableCount = 2;
    private const decimal ExpenseAmount = 2.5m;
    private static readonly DateOnly PeriodFrom = new(2099, 8, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 31);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private ScenarioVariantShiftCopier _copier = null!;
    private HashSet<Guid> _hidden = null!;
    private List<Guid> _keyGroupIds = null!;
    private Group _keyGroup = null!;
    private Group _subGroup = null!;
    private Group _otherGroup = null!;
    private Client _client = null!;
    private Qualification _qualification = null!;
    private AnalyseScenario _variant = null!;

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
        _hidden = [];
        var visibility = Substitute.For<IShiftVisibilityGuard>();
        visibility.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlySet<Guid>>(
                call.Arg<IReadOnlyCollection<Guid>>().Where(id => !_hidden.Contains(id)).ToHashSet()));
        _copier = new ScenarioVariantShiftCopier(_context, _provider.GetRequiredService<ScenarioGroupScopeReader>(), visibility);

        _keyGroup = await CreateGroupAsync("KEYGROUP", null);
        _subGroup = await CreateGroupAsync("SUBGROUP", _keyGroup.Id);
        _otherGroup = await CreateGroupAsync("OTHERGROUP", null);
        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(_client);
        _qualification = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = TestPrefix + "QUAL" } };
        await _context.Set<Qualification>().AddAsync(_qualification);
        await _context.SaveChangesAsync();

        _variant = await CreateVariantAsync("KEY");
        _keyGroupIds = await _provider.GetRequiredService<IAnalyseScenarioService>()
            .GetGroupHierarchyIdsAsync(_keyGroup.Id, CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _provider.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task CopyByIds_SelectingThePlannableCopy_CopiesTheWholeFamilyWithNewIdsTokenAndLinksIntoTheCopy()
    {
        var family = await SeedFamilyAsync("FAMILY", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var before = await SnapshotAsync(family.Ids);

        var result = await CopyByIdsAsync(family.Plannable.Id);

        result.CopiedFamilies.ShouldBe(1);
        result.SkippedFamilies.ShouldBe(0);
        var copies = await LoadCopiesAsync();
        copies.Count.ShouldBe(family.Ids.Count);
        var copyIds = copies.Select(copy => copy.Id).ToHashSet();
        copyIds.Overlaps(family.Ids).ShouldBeFalse();
        copies.ShouldAllBe(copy => copy.AnalyseToken == _variant.Token
            && copy.ScenarioSourceShiftId == null
            && copy.SourceChildCountSnapshot == 0
            && copy.Lft == null
            && copy.Rgt == null
            && copy.ClientId == _client.Id);
        copies.Select(copy => copy.Name).ShouldBe(family.Rows.Select(row => row.Name), ignoreOrder: true);
        copies.Select(copy => copy.Status).ShouldBe(family.Rows.Select(row => row.Status), ignoreOrder: true);

        var copyOrder = copies.Single(copy => copy.Status == ShiftStatus.SealedOrder);
        var copyPlannable = copies.Single(copy => copy.Status == ShiftStatus.OriginalShift);
        var copyPieces = copies.Where(copy => copy.Status == ShiftStatus.SplitShift).ToList();
        copyOrder.OriginalId.ShouldBeNull();
        copyOrder.ParentId.ShouldBeNull();
        copyOrder.RootId.ShouldBeNull();
        copyPlannable.OriginalId.ShouldBe(copyOrder.Id);
        copyPieces.Count.ShouldBe(PieceCount);
        copyPieces.ShouldAllBe(piece => piece.OriginalId == copyOrder.Id
            && piece.ParentId == copyPlannable.Id
            && piece.RootId == copyPlannable.Id);
        copyOrder.StartShift.ShouldBe(family.Order.StartShift);
        copyOrder.FromDate.ShouldBe(family.Order.FromDate);
        copyOrder.WorkTime.ShouldBe(family.Order.WorkTime);

        (await SnapshotAsync(family.Ids)).ShouldBe(before);
    }

    [Test]
    public async Task CopyByIds_CopiesGroupItemsWithTheTokenAndExpensesAndRequirementsByTheNewShiftIdWithoutOne()
    {
        var family = await SeedFamilyAsync("SATELLITES", PeriodFrom, PeriodUntil, _keyGroup.Id, _subGroup.Id);

        await CopyByIdsAsync(family.Order.Id);

        var copies = await LoadCopiesAsync();
        var copyIds = copies.Select(copy => copy.Id).ToList();
        var items = await _context.Set<GroupItem>().AsNoTracking()
            .Where(item => item.ShiftId != null && copyIds.Contains(item.ShiftId.Value) && !item.IsDeleted)
            .ToListAsync();
        items.Count.ShouldBe(copies.Count * 2);
        items.ShouldAllBe(item => item.AnalyseToken == _variant.Token);
        foreach (var copy in copies)
        {
            items.Where(item => item.ShiftId == copy.Id).Select(item => item.GroupId)
                .ShouldBe(new[] { _keyGroup.Id, _subGroup.Id }, ignoreOrder: true);
        }

        var expenses = await _context.Set<ShiftExpenses>().AsNoTracking()
            .Where(expense => copyIds.Contains(expense.ShiftId) && !expense.IsDeleted)
            .ToListAsync();
        expenses.Count.ShouldBe(OrderAndPlannableCount);
        expenses.ShouldAllBe(expense => expense.AnalyseToken == null
            && expense.Amount == ExpenseAmount
            && expense.Description == TestPrefix + "EXPENSE_SATELLITES");
        expenses.Select(expense => expense.ShiftId).Distinct().Count().ShouldBe(OrderAndPlannableCount);

        var requirements = await _context.Set<ShiftRequiredQualification>().AsNoTracking()
            .Where(requirement => copyIds.Contains(requirement.ShiftId) && !requirement.IsDeleted)
            .ToListAsync();
        requirements.Count.ShouldBe(OrderAndPlannableCount);
        requirements.ShouldAllBe(requirement => requirement.QualificationId == _qualification.Id
            && requirement.IsMandatory
            && requirement.MinLevel == QualificationLevel.Proficient);
        requirements.Select(requirement => requirement.ShiftId).Distinct().Count().ShouldBe(OrderAndPlannableCount);
    }

    [Test]
    public async Task CopyByIds_SeveralMembersOfOneFamilyAndTheSameIdTwice_CopiesTheFamilyOnce()
    {
        var family = await SeedFamilyAsync("ONCE", PeriodFrom, PeriodUntil, _keyGroup.Id);

        var result = await CopyByIdsAsync(family.Order.Id, family.Plannable.Id, family.Pieces[0].Id, family.Order.Id);

        result.CopiedFamilies.ShouldBe(1);
        (await LoadCopiesAsync()).Count.ShouldBe(family.Ids.Count);
    }

    [Test]
    public async Task CopyByIds_TwoFamilies_KeepsEachFamilysLinksInsideItsOwnCopy()
    {
        var first = await SeedFamilyAsync("FIRST", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var second = await SeedFamilyAsync("SECOND", PeriodFrom, PeriodUntil, _subGroup.Id);

        var result = await CopyByIdsAsync(first.Order.Id, second.Plannable.Id);

        result.CopiedFamilies.ShouldBe(OrderAndPlannableCount);
        var copies = await LoadCopiesAsync();
        copies.Count.ShouldBe(first.Ids.Count + second.Ids.Count);
        foreach (var name in new[] { "FIRST", "SECOND" })
        {
            var members = copies.Where(copy => copy.Name.StartsWith(TestPrefix + name + "_")).ToList();
            var order = members.Single(copy => copy.Status == ShiftStatus.SealedOrder);
            members.Where(copy => copy.Status != ShiftStatus.SealedOrder).ShouldAllBe(copy => copy.OriginalId == order.Id);
        }
    }

    [Test]
    public async Task CopyByIds_IgnoresSoftDeletedPiecesGroupItemsExpensesAndRequirements()
    {
        var family = await SeedFamilyAsync("DELETED", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var deletedPiece = NewShift("DELETED_GONE", ShiftStatus.SplitShift, PeriodFrom, PeriodUntil);
        deletedPiece.OriginalId = family.Order.Id;
        deletedPiece.ParentId = family.Plannable.Id;
        deletedPiece.RootId = family.Plannable.Id;
        deletedPiece.IsDeleted = true;
        await _context.Shift.AddAsync(deletedPiece);
        await _context.SaveChangesAsync();
        await _context.Set<GroupItem>().AddAsync(
            new GroupItem { Id = Guid.NewGuid(), GroupId = _otherGroup.Id, ShiftId = family.Order.Id, IsDeleted = true });
        await _context.Set<ShiftExpenses>().AddAsync(new ShiftExpenses
        {
            Id = Guid.NewGuid(),
            ShiftId = family.Order.Id,
            Amount = ExpenseAmount,
            Description = TestPrefix + "EXPENSE_GONE",
            IsDeleted = true
        });
        var secondQualification = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = TestPrefix + "QUAL2" } };
        await _context.Set<Qualification>().AddAsync(secondQualification);
        await _context.SaveChangesAsync();
        await _context.Set<ShiftRequiredQualification>().AddAsync(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = family.Order.Id,
            QualificationId = secondQualification.Id,
            IsMandatory = true,
            MinLevel = QualificationLevel.Proficient,
            IsDeleted = true
        });
        await _context.SaveChangesAsync();

        var result = await CopyByIdsAsync(family.Order.Id);

        result.CopiedFamilies.ShouldBe(1);
        var copies = await LoadCopiesAsync();
        copies.Count.ShouldBe(family.Ids.Count);
        copies.ShouldNotContain(copy => copy.Name == TestPrefix + "DELETED_GONE");
        var copyIds = copies.Select(copy => copy.Id).ToList();
        (await _context.Set<GroupItem>().AsNoTracking()
            .Where(item => item.ShiftId != null && copyIds.Contains(item.ShiftId.Value))
            .Select(item => item.GroupId).Distinct().ToListAsync()).ShouldBe(new[] { _keyGroup.Id });
        (await _context.Set<ShiftExpenses>().AsNoTracking()
            .Where(expense => copyIds.Contains(expense.ShiftId)).Select(expense => expense.Description).Distinct().ToListAsync())
            .ShouldBe(new[] { TestPrefix + "EXPENSE_DELETED" });
        (await _context.Set<ShiftRequiredQualification>().AsNoTracking()
            .Where(requirement => copyIds.Contains(requirement.ShiftId))
            .Select(requirement => requirement.QualificationId).Distinct().ToListAsync()).ShouldBe(new[] { _qualification.Id });
    }

    [Test]
    public async Task CopyByIds_ASelectedVariantShiftOrScenarioClone_IsRefusedAndNothingIsStaged()
    {
        var good = await SeedFamilyAsync("GOOD", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var inVariant = await SeedFamilyAsync("INVARIANT", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var otherToken = Guid.NewGuid();
        var clone = NewShift("CLONE", ShiftStatus.SealedOrder, PeriodFrom, PeriodUntil);
        clone.AnalyseToken = otherToken;
        clone.ScenarioSourceShiftId = good.Order.Id;
        await _context.Shift.AddAsync(clone);
        await _context.SaveChangesAsync();
        var order = await _context.Shift.FirstAsync(shift => shift.Id == inVariant.Order.Id);
        order.AnalyseToken = Guid.NewGuid();
        await _context.SaveChangesAsync();

        var cloneRefusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(good.Order.Id, clone.Id));
        var variantRefusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(good.Order.Id, inVariant.Order.Id));

        cloneRefusal.Message.ShouldBe(ScenarioVariantShiftCopier.NotRealMessage);
        variantRefusal.Message.ShouldBe(ScenarioVariantShiftCopier.NotRealMessage);
        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyByIds_AnOpenOrder_IsRefusedAndNothingIsStaged()
    {
        var good = await SeedFamilyAsync("GOOD", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var open = await SeedOpenOrderAsync("OPEN", PeriodFrom, PeriodUntil, _keyGroup.Id);

        var refusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(good.Order.Id, open.Id));

        refusal.Message.ShouldBe(ScenarioVariantShiftCopier.OpenOrderMessage);
        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyByIds_AShiftOfAForeignGroupOrWithOneForeignGroup_IsRefusedAndNothingIsStaged()
    {
        var good = await SeedFamilyAsync("GOOD", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var foreign = await SeedFamilyAsync("FOREIGN", PeriodFrom, PeriodUntil, _otherGroup.Id);
        var mixed = await SeedFamilyAsync("MIXED", PeriodFrom, PeriodUntil, _keyGroup.Id, _otherGroup.Id);

        var foreignRefusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(good.Order.Id, foreign.Order.Id));
        var mixedRefusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(good.Order.Id, mixed.Plannable.Id));

        foreignRefusal.Message.ShouldBe(ScenarioVariantShiftCopier.GroupsOutsideMessage);
        mixedRefusal.Message.ShouldBe(ScenarioVariantShiftCopier.GroupsOutsideMessage);
        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyByIds_AShiftWithoutAnyGroup_IsRefusedBecauseAKeyShiftNeedsOne()
    {
        var groupless = await SeedFamilyAsync("GROUPLESS", PeriodFrom, PeriodUntil);

        var refusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(groupless.Order.Id));

        refusal.Message.ShouldBe(ScenarioVariantShiftCopier.GroupsOutsideMessage);
        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyByIds_AShiftOutsideTheKeyPeriod_IsRefusedAndNothingIsStaged()
    {
        var good = await SeedFamilyAsync("GOOD", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var late = await SeedFamilyAsync("LATE", PeriodUntil.AddDays(1), PeriodUntil.AddDays(2), _keyGroup.Id);

        var refusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(good.Order.Id, late.Order.Id));

        refusal.Message.ShouldBe(ScenarioVariantShiftCopier.OutsidePeriodMessage);
        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyByIds_AnOpenEndedShiftThatStartedBeforeTheKey_LandsInsideTheKeyOnEveryRow()
    {
        var family = await SeedFamilyAsync("LONGRUNNING", PeriodFrom.AddDays(-100), null, _keyGroup.Id);

        await CopyByIdsAsync(family.Order.Id);

        var copies = await LoadCopiesAsync();
        copies.Count.ShouldBe(family.Ids.Count);
        copies.ShouldAllBe(copy => copy.FromDate == PeriodFrom && copy.UntilDate == PeriodUntil);
        var original = await _context.Shift.AsNoTracking().SingleAsync(shift => shift.Id == family.Order.Id);
        original.FromDate.ShouldBe(PeriodFrom.AddDays(-100));
        original.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task CopyByIds_NamesTheRealOrderOnTheCopiedSealedOrderWhicheverMemberWasChosen()
    {
        var family = await SeedFamilyAsync("PROVENANCE", PeriodFrom, PeriodUntil, _keyGroup.Id);

        await CopyByIdsAsync(family.Pieces[0].Id);

        var copies = await LoadCopiesAsync();
        copies.Single(copy => copy.Status == ShiftStatus.SealedOrder).CopiedFromShiftId.ShouldBe(family.Order.Id);
        copies.Where(copy => copy.Status != ShiftStatus.SealedOrder).ShouldAllBe(copy => copy.CopiedFromShiftId == null);
    }

    [Test]
    public async Task CopyByIds_PiecesAResetClosedBeforeTheKey_StayBehind_AndTheRestKeepsItsLinks()
    {
        var family = await SeedResetFamilyAsync("RESET", _keyGroup.Id);

        var result = await CopyByIdsAsync(family.Order.Id);

        result.CopiedFamilies.ShouldBe(1);
        var copies = await LoadCopiesAsync();
        copies.Select(copy => copy.Status).ShouldBe(new[] { ShiftStatus.SealedOrder, ShiftStatus.OriginalShift }, ignoreOrder: true);
        copies.Single(copy => copy.Status == ShiftStatus.OriginalShift).OriginalId
            .ShouldBe(copies.Single(copy => copy.Status == ShiftStatus.SealedOrder).Id);
        copies.ShouldAllBe(copy => copy.FromDate == PeriodFrom && copy.UntilDate == PeriodUntil);
    }

    [Test]
    public async Task CopyByIds_APieceInsideOnAParentOutside_IsRefusedAndNothingIsStaged()
    {
        var family = await SeedDateCutFamilyAsync("DATECUT", _keyGroup.Id);

        var refusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(family.Order.Id));

        refusal.Message.ShouldBe(ScenarioVariantShiftCopier.PartsOutsidePeriodMessage);
        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyByIds_AnOrderWithoutACustomer_IsRefusedWithTheMessageOfTheWriteGuard()
    {
        var family = await SeedFamilyAsync("NOCUSTOMER", PeriodFrom, PeriodUntil, _keyGroup.Id);
        await ClearCustomerAsync(family.Ids);

        var refusal = await Should.ThrowAsync<InvalidRequestException>(() => CopyByIdsAsync(family.Order.Id));

        refusal.Message.ShouldBe(VariantShiftWriteGuard.CustomerRequiredMessage);
        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyGroup_FamiliesWithoutACustomerOrWithAPieceHangingOutside_AreSkippedAndCounted()
    {
        var withoutCustomer = await SeedFamilyAsync("NOCUSTOMER", PeriodFrom, PeriodUntil, _keyGroup.Id);
        await ClearCustomerAsync(withoutCustomer.Ids);
        await SeedDateCutFamilyAsync("DATECUT", _keyGroup.Id);
        var fits = await SeedFamilyAsync("FITS", PeriodFrom, PeriodUntil, _keyGroup.Id);

        var result = await CopyGroupAsync(_keyGroup.Id);

        result.CopiedFamilies.ShouldBe(1);
        result.SkippedFamilies.ShouldBe(OrderAndPlannableCount);
        (await LoadCopiesAsync()).Count.ShouldBe(fits.Ids.Count);
    }

    [Test]
    public async Task CopyByIds_AMissingId_IsNotFoundAndNothingIsStaged()
    {
        var good = await SeedFamilyAsync("GOOD", PeriodFrom, PeriodUntil, _keyGroup.Id);

        await Should.ThrowAsync<KeyNotFoundException>(() => CopyByIdsAsync(good.Order.Id, Guid.NewGuid()));

        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyByIds_ASoftDeletedSelectedShift_IsNotFound()
    {
        var family = await SeedFamilyAsync("GONE", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var order = await _context.Shift.FirstAsync(shift => shift.Id == family.Order.Id);
        order.IsDeleted = true;
        await _context.SaveChangesAsync();

        await Should.ThrowAsync<KeyNotFoundException>(() => CopyByIdsAsync(family.Order.Id));

        await NothingWasStagedAsync();
    }

    [Test]
    public async Task CopyGroup_CopiesEveryFamilyOfTheHierarchyThatFitsAndCountsOpenOrdersAndOutsidePeriodFamiliesAsSkipped()
    {
        var inRoot = await SeedFamilyAsync("INROOT", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var inSub = await SeedFamilyAsync("INSUB", PeriodFrom, PeriodUntil, _subGroup.Id);
        await SeedFamilyAsync("LATE", PeriodUntil.AddDays(1), PeriodUntil.AddDays(2), _subGroup.Id);
        await SeedOpenOrderAsync("OPEN", PeriodFrom, PeriodUntil, _subGroup.Id);
        await SeedFamilyAsync("FOREIGNGROUP", PeriodFrom, PeriodUntil, _otherGroup.Id);
        var deleted = await SeedFamilyAsync("DELETEDFAMILY", PeriodFrom, PeriodUntil, _keyGroup.Id);
        await SoftDeleteAsync(deleted.Ids);
        var before = await SnapshotAsync(inRoot.Ids.Concat(inSub.Ids));

        var result = await CopyGroupAsync(_keyGroup.Id);

        result.CopiedFamilies.ShouldBe(OrderAndPlannableCount);
        result.SkippedFamilies.ShouldBe(OrderAndPlannableCount);
        var copies = await LoadCopiesAsync();
        copies.Count.ShouldBe(inRoot.Ids.Count + inSub.Ids.Count);
        copies.Select(copy => copy.Name).Distinct().ShouldBe(
            inRoot.Rows.Concat(inSub.Rows).Select(row => row.Name).Distinct(), ignoreOrder: true);
        (await SnapshotAsync(inRoot.Ids.Concat(inSub.Ids))).ShouldBe(before);
    }

    [Test]
    public async Task CopyGroup_WithASubGroupAsSource_CopiesOnlyThatSubtree()
    {
        await SeedFamilyAsync("INROOT", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var inSub = await SeedFamilyAsync("INSUB", PeriodFrom, PeriodUntil, _subGroup.Id);

        var result = await CopyGroupAsync(_subGroup.Id);

        result.CopiedFamilies.ShouldBe(1);
        result.SkippedFamilies.ShouldBe(0);
        var copies = await LoadCopiesAsync();
        copies.Count.ShouldBe(inSub.Ids.Count);
        copies.ShouldNotContain(copy => copy.Name.StartsWith(TestPrefix + "INROOT"));
    }

    [Test]
    public async Task CopyGroup_AFamilyWithAGroupOutsideTheKeyHierarchy_IsSkippedAndCounted()
    {
        await SeedFamilyAsync("MIXED", PeriodFrom, PeriodUntil, _keyGroup.Id, _otherGroup.Id);
        var fits = await SeedFamilyAsync("FITS", PeriodFrom, PeriodUntil, _keyGroup.Id);

        var result = await CopyGroupAsync(_keyGroup.Id);

        result.CopiedFamilies.ShouldBe(1);
        result.SkippedFamilies.ShouldBe(1);
        (await LoadCopiesAsync()).Count.ShouldBe(fits.Ids.Count);
    }

    [Test]
    public async Task CopyGroup_PinsThePeriodIntersectionAtItsBoundariesAndForOpenEndedShifts()
    {
        await SeedFamilyAsync("ENDSBEFORE", PeriodFrom.AddDays(-2), PeriodFrom.AddDays(-1), _keyGroup.Id);
        await SeedFamilyAsync("ENDSONFROM", PeriodFrom.AddDays(-2), PeriodFrom, _keyGroup.Id);
        await SeedFamilyAsync("STARTSONUNTIL", PeriodUntil, PeriodUntil.AddDays(2), _keyGroup.Id);
        await SeedFamilyAsync("STARTSAFTER", PeriodUntil.AddDays(1), PeriodUntil.AddDays(2), _keyGroup.Id);
        await SeedFamilyAsync("OPENENDED", PeriodFrom.AddDays(-30), null, _keyGroup.Id);

        var result = await CopyGroupAsync(_keyGroup.Id);

        result.CopiedFamilies.ShouldBe(3);
        result.SkippedFamilies.ShouldBe(OrderAndPlannableCount);
        var copies = await LoadCopiesAsync();
        copies.Select(copy => copy.Name.Split('_')[^2]).Distinct().ShouldBe(
            new[] { "ENDSONFROM", "STARTSONUNTIL", "OPENENDED" }, ignoreOrder: true);
        copies.ShouldAllBe(copy => copy.FromDate >= PeriodFrom && copy.UntilDate != null && copy.UntilDate <= PeriodUntil);
    }

    [Test]
    public async Task CopyGroup_LeavesOutFamiliesTheCallerCannotSeeWithoutCountingThem()
    {
        var hiddenFamily = await SeedFamilyAsync("HIDDEN", PeriodFrom, PeriodUntil, _keyGroup.Id);
        var visibleFamily = await SeedFamilyAsync("VISIBLE", PeriodFrom, PeriodUntil, _keyGroup.Id);
        _hidden.UnionWith(hiddenFamily.Ids);

        var result = await CopyGroupAsync(_keyGroup.Id);

        result.CopiedFamilies.ShouldBe(1);
        result.SkippedFamilies.ShouldBe(0);
        (await LoadCopiesAsync()).Count.ShouldBe(visibleFamily.Ids.Count);
    }

    [Test]
    public async Task CopyGroup_AGroupWithoutShifts_CopiesNothing()
    {
        var result = await CopyGroupAsync(_otherGroup.Id);

        result.CopiedFamilies.ShouldBe(0);
        result.SkippedFamilies.ShouldBe(0);
        await NothingWasStagedAsync();
    }

    private async Task<CopyShiftsToVariantResult> CopyByIdsAsync(params Guid[] shiftIds)
    {
        var result = await _copier.StageCopyOfShiftsAsync(_variant, _keyGroupIds, shiftIds);
        await _context.SaveChangesAsync();
        return result;
    }

    private async Task<CopyShiftsToVariantResult> CopyGroupAsync(Guid sourceGroupId)
    {
        var result = await _copier.StageCopyOfGroupAsync(_variant, _keyGroupIds, sourceGroupId);
        await _context.SaveChangesAsync();
        return result;
    }

    private async Task NothingWasStagedAsync()
    {
        await _context.SaveChangesAsync();
        (await LoadCopiesAsync()).ShouldBeEmpty();
    }

    private async Task<List<Shift>> LoadCopiesAsync()
    {
        _context.ChangeTracker.Clear();
        return await _context.Shift.AsNoTracking()
            .Where(shift => shift.AnalyseToken == _variant.Token && !shift.IsDeleted)
            .ToListAsync();
    }

    private async Task<List<string>> SnapshotAsync(IEnumerable<Guid> ids)
    {
        var idList = ids.ToList();
        var rows = await _context.Shift.AsNoTracking().IgnoreQueryFilters()
            .Where(shift => idList.Contains(shift.Id))
            .OrderBy(shift => shift.Id)
            .ToListAsync();
        var items = await _context.Set<GroupItem>().AsNoTracking().IgnoreQueryFilters()
            .Where(item => item.ShiftId != null && idList.Contains(item.ShiftId.Value))
            .ToListAsync();
        var expenses = await _context.Set<ShiftExpenses>().AsNoTracking().IgnoreQueryFilters()
            .Where(expense => idList.Contains(expense.ShiftId))
            .ToListAsync();
        var requirements = await _context.Set<ShiftRequiredQualification>().AsNoTracking().IgnoreQueryFilters()
            .Where(requirement => idList.Contains(requirement.ShiftId))
            .ToListAsync();

        return rows.Select(shift => string.Join(
            "|",
            shift.Id,
            shift.Status,
            shift.AnalyseToken,
            shift.ScenarioSourceShiftId,
            shift.OriginalId,
            shift.ParentId,
            shift.RootId,
            shift.Lft,
            shift.Rgt,
            shift.IsDeleted,
            items.Count(item => item.ShiftId == shift.Id && item.AnalyseToken == null && !item.IsDeleted),
            expenses.Count(expense => expense.ShiftId == shift.Id && !expense.IsDeleted),
            requirements.Count(requirement => requirement.ShiftId == shift.Id && !requirement.IsDeleted))).ToList();
    }

    private async Task SoftDeleteAsync(IEnumerable<Guid> ids)
    {
        var idList = ids.ToList();
        var rows = await _context.Shift.Where(shift => idList.Contains(shift.Id)).ToListAsync();
        rows.ForEach(shift => shift.IsDeleted = true);
        await _context.SaveChangesAsync();
    }

    private async Task<Family> SeedFamilyAsync(string suffix, DateOnly from, DateOnly? until, params Guid[] groupIds)
    {
        var order = NewShift(suffix + "_ORDER", ShiftStatus.SealedOrder, from, until);
        await _context.Shift.AddAsync(order);
        await _context.SaveChangesAsync();

        var plannable = NewShift(suffix + "_PLAN", ShiftStatus.OriginalShift, from, until);
        plannable.OriginalId = order.Id;
        plannable.Lft = 1;
        plannable.Rgt = (PieceCount * 2) + 2;
        await _context.Shift.AddAsync(plannable);
        await _context.SaveChangesAsync();

        var pieces = new List<Shift>();
        for (var index = 0; index < PieceCount; index++)
        {
            var piece = NewShift(suffix + "_PIECE" + index, ShiftStatus.SplitShift, from, until);
            piece.OriginalId = order.Id;
            piece.ParentId = plannable.Id;
            piece.RootId = plannable.Id;
            piece.Lft = 2 + (index * 2);
            piece.Rgt = 3 + (index * 2);
            await _context.Shift.AddAsync(piece);
            pieces.Add(piece);
        }

        await _context.SaveChangesAsync();
        var family = new Family(order, plannable, pieces);

        foreach (var row in family.Rows)
        {
            foreach (var groupId in groupIds)
            {
                await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = row.Id });
            }
        }

        foreach (var row in new[] { order, plannable })
        {
            await _context.Set<ShiftExpenses>().AddAsync(new ShiftExpenses
            {
                Id = Guid.NewGuid(),
                ShiftId = row.Id,
                Amount = ExpenseAmount,
                Description = TestPrefix + "EXPENSE_" + suffix
            });
            await _context.Set<ShiftRequiredQualification>().AddAsync(new ShiftRequiredQualification
            {
                Id = Guid.NewGuid(),
                ShiftId = row.Id,
                QualificationId = _qualification.Id,
                IsMandatory = true,
                MinLevel = QualificationLevel.Proficient
            });
        }

        await _context.SaveChangesAsync();
        return family;
    }

    private async Task<Family> SeedResetFamilyAsync(string suffix, Guid groupId)
    {
        var start = PeriodFrom.AddDays(-30);
        var order = NewShift(suffix + "_ORDER", ShiftStatus.SealedOrder, start, null);
        await _context.Shift.AddAsync(order);
        await _context.SaveChangesAsync();

        var closed = NewShift(suffix + "_CLOSED", ShiftStatus.SplitShift, start, PeriodFrom.AddDays(-1));
        closed.OriginalId = order.Id;
        closed.RootId = closed.Id;
        var plannable = NewShift(suffix + "_PLAN", ShiftStatus.OriginalShift, PeriodFrom, null);
        plannable.OriginalId = order.Id;
        return await StoreFamilyAsync(order, plannable, [closed], groupId);
    }

    private async Task<Family> SeedDateCutFamilyAsync(string suffix, Guid groupId)
    {
        var start = PeriodFrom.AddDays(-30);
        var order = NewShift(suffix + "_ORDER", ShiftStatus.SealedOrder, start, PeriodUntil);
        await _context.Shift.AddAsync(order);
        await _context.SaveChangesAsync();

        var parent = NewShift(suffix + "_PARENT", ShiftStatus.SplitShift, start, PeriodFrom.AddDays(-1));
        parent.OriginalId = order.Id;
        parent.RootId = parent.Id;
        var child = NewShift(suffix + "_CHILD", ShiftStatus.SplitShift, PeriodFrom, PeriodUntil);
        child.OriginalId = order.Id;
        child.ParentId = parent.Id;
        child.RootId = parent.Id;
        await _context.Shift.AddAsync(parent);
        await _context.SaveChangesAsync();
        return await StoreFamilyAsync(order, null, [child], groupId, parent);
    }

    private async Task<Family> StoreFamilyAsync(Shift order, Shift? plannable, List<Shift> pieces, Guid groupId, params Shift[] alreadyStored)
    {
        if (plannable is not null)
        {
            await _context.Shift.AddAsync(plannable);
        }

        await _context.Shift.AddRangeAsync(pieces);
        await _context.SaveChangesAsync();

        var rows = new[] { order, plannable }.OfType<Shift>().Concat(alreadyStored).Concat(pieces).ToList();
        foreach (var row in rows)
        {
            await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = row.Id });
        }

        await _context.SaveChangesAsync();
        return new Family(order, plannable ?? alreadyStored[0], pieces);
    }

    private async Task ClearCustomerAsync(IEnumerable<Guid> ids)
    {
        var idList = ids.ToList();
        var rows = await _context.Shift.Where(shift => idList.Contains(shift.Id)).ToListAsync();
        rows.ForEach(shift => shift.ClientId = null);
        await _context.SaveChangesAsync();
    }

    private async Task<Shift> SeedOpenOrderAsync(string suffix, DateOnly from, DateOnly? until, Guid groupId)
    {
        var open = NewShift(suffix, ShiftStatus.OriginalOrder, from, until);
        await _context.Shift.AddAsync(open);
        await _context.SaveChangesAsync();
        await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = open.Id });
        await _context.SaveChangesAsync();
        return open;
    }

    private Shift NewShift(string suffix, ShiftStatus status, DateOnly from, DateOnly? until)
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Variant copy seam",
            Status = status,
            FromDate = from,
            UntilDate = until,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            ClientId = _client.Id
        };
    }

    private async Task<Group> CreateGroupAsync(string suffix, Guid? parent)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = parent
        };
        await _context.Set<Group>().AddAsync(group);
        await _context.SaveChangesAsync();
        return group;
    }

    private async Task<AnalyseScenario> CreateVariantAsync(string suffix)
    {
        var variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            GroupId = _keyGroup.Id,
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
            DELETE FROM shift_required_qualification WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR qualification_id IN (SELECT id FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%');
            DELETE FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM client_shift_preference WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL, original_id = NULL, parent_id = NULL, root_id = NULL
                WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed record Family(Shift Order, Shift Plannable, List<Shift> Pieces)
    {
        public List<Shift> Rows => new[] { Order, Plannable }.Concat(Pieces).ToList();

        public List<Guid> Ids => Rows.Select(row => row.Id).ToList();
    }
}
