// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proofs of the fixes from the security review of the variant (key) feature, because each of them is a LINQ
/// query or a commit EF InMemory cannot show: the shift search sees real rows only and applies the group restriction before
/// count and cap in a stable order; the client's shift ids leave tokened rows out; the world reader tells a key shift from a
/// clone and sees soft-deleted rows; a scenario with a work on a key shift it does not carry is refused at the accept; a cut
/// in a key takes its groups from the hierarchy, whatever the request carries; child rows are found although a key tree has
/// no nested-set values; and of two siblings sharing a key only one accept wins, the other changes nothing. The retry of the
/// key commit is proven in VariantAcceptRefusalSeamTests: this fixture's context has no EnableRetryOnFailure. Only
/// INTEGRATION_TEST_ rows are written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class VariantSecurityFindingsSeamTests : VariantPromotionSeamTestBase
{
    private const string SearchTerm = TestPrefix + "SEARCH";
    private const int SearchLimit = 10;

    [Test]
    public async Task ShiftSearch_LeavesKeyRowsOut_ForAnUnrestrictedCaller()
    {
        var variant = await CreateVariantAsync("SEARCH_VARIANT");
        await CreateOrderAsync("SEARCH_KEY", variant.Token);
        var (_, realPlannable) = await CreateOrderAsync("SEARCH_REAL", null);

        var result = await new ShiftSearchRepository(_context).SearchAsync(null, SearchTerm, SearchLimit);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(realPlannable.Id);
    }

    [Test]
    public async Task ShiftSearch_AppliesTheRestrictionBeforeCountAndCap()
    {
        var (_, visible) = await CreateOrderAsync("SEARCH_VISIBLE", null);
        await SetFromDateAsync(visible.Id, PeriodFrom);
        var (_, hidden) = await CreateOrderAsync("SEARCH_HIDDEN", null);
        await SetFromDateAsync(hidden.Id, PeriodUntil);
        await MoveToGroupAsync(hidden.Id, await AddGroupAsync("SEARCH_FOREIGN"));
        var search = new ShiftSearchRepository(_context);

        var restricted = await search.SearchAsync([_group.Id], SearchTerm, limit: 1);

        restricted.TotalCount.ShouldBe(1);
        restricted.Items.Single().Id.ShouldBe(visible.Id);
    }

    [Test]
    public async Task ShiftSearch_EqualDates_CapsInAStableOrder()
    {
        var (_, first) = await CreateOrderAsync("SEARCH_EQUAL_A", null);
        var (_, second) = await CreateOrderAsync("SEARCH_EQUAL_B", null);
        var expected = await _context.Shift.AsNoTracking()
            .Where(s => s.Id == first.Id || s.Id == second.Id)
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .FirstAsync();

        var result = await new ShiftSearchRepository(_context).SearchAsync(null, SearchTerm, limit: 1);

        result.TotalCount.ShouldBe(2);
        result.Items.Single().Id.ShouldBe(expected);
    }

    [Test]
    public async Task ShiftIdsByClient_LeaveTokenedRowsOut()
    {
        var variant = await CreateVariantAsync("BYCLIENT_VARIANT");
        var (keyOrder, keyPlannable) = await CreateOrderAsync("BYCLIENT_KEY", variant.Token);
        var (realOrder, realPlannable) = await CreateOrderAsync("BYCLIENT_REAL", null);

        var ids = await _shiftRepository.GetShiftIdsByClientAsync(_customer.Id);

        ids.ShouldBe(new[] { realOrder.Id, realPlannable.Id }, ignoreOrder: true);
        ids.ShouldNotContain(keyOrder.Id);
        ids.ShouldNotContain(keyPlannable.Id);
    }

    [Test]
    public async Task WorldReader_TellsAKeyShiftFromAClone_SeesDeletedRows_AndLeavesRealRowsOut()
    {
        var variant = await CreateVariantAsync("WORLD_VARIANT");
        var family = await CreateCutOrderAsync("WORLD_KEY", variant.Token);
        var scenario = await CreateScenarioAsync(variant.Id);
        var cloneId = await CloneOfAsync(scenario.Token, family.SecondPiece);
        await _context.Shift.Where(s => s.Id == family.Child)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsDeleted, true));
        var (_, realPlannable) = await CreateOrderAsync("WORLD_REAL", null);

        var worlds = await new ShiftVisibilityReadRepository(_context).GetTokenedShiftWorldsAsync(
            [family.Child, family.SecondPiece, cloneId, realPlannable.Id, Guid.NewGuid()]);

        worlds.Count.ShouldBe(3);
        worlds.Single(world => world.ShiftId == family.Child).IsVariant.ShouldBeTrue();
        worlds.Single(world => world.ShiftId == family.SecondPiece).IsVariant.ShouldBeTrue();
        var clone = worlds.Single(world => world.ShiftId == cloneId);
        clone.IsVariant.ShouldBeFalse();
        clone.AnalyseToken.ShouldBe(scenario.Token);
    }

    [Test]
    public async Task Accept_ScenarioWithoutKeys_HoldingAWorkOnAKeyShift_IsAConflict_AndNothingIsPromoted()
    {
        var variant = await CreateVariantAsync("UNCARRIED_VARIANT");
        var (_, keyPlannable) = await CreateOrderAsync("UNCARRIED_KEY", variant.Token);
        var scenario = await CreateScenarioAsync();
        var workId = await AddWorkDirectlyAsync(scenario.Token, keyPlannable.Id);

        var ex = await Should.ThrowAsync<ConflictException>(() => AcceptAsync(scenario.Id));

        ex.Message.ShouldBe(ScenarioAcceptConflictValidator.WorkOnUncarriedVariantShiftMessage);
        _context.ChangeTracker.Clear();
        (await _context.Work.AsNoTracking().SingleAsync(w => w.Id == workId)).AnalyseToken.ShouldBe(scenario.Token);
        (await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == scenario.Id))
            .Status.ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task CutInAKey_IgnoresTheGroupsOfTheRequest_OnANewPieceAndOnAnUpdatedOne()
    {
        var foreignGroupId = await AddGroupAsync("CUT_FOREIGN");
        var variant = await CreateVariantAsync("CUT_VARIANT");
        var (sealedOrder, plannable) = await CreateOrderAsync("CUT_KEY", variant.Token);
        var updated = await PieceOfAsync(plannable.Id, plannable.Id, PeriodFrom, FirstPieceUntil);
        var created = await PieceOfAsync(plannable.Id, Guid.NewGuid(), SecondPieceFrom, PeriodUntil);
        updated.Groups = [new SimpleGroupResource { Id = foreignGroupId }];
        created.Groups = [new SimpleGroupResource { Id = foreignGroupId }];

        await _facade.ProcessBatchCutsAsync([Update(updated), Create(sealedOrder.Id, created)]);

        _context.ChangeTracker.Clear();
        var items = await _context.GroupItem.AsNoTracking()
            .Where(item => !item.IsDeleted && (item.ShiftId == updated.Id || item.ShiftId == created.Id))
            .ToListAsync();
        items.Count.ShouldBe(2);
        items.ShouldAllBe(item => item.GroupId == _group.Id && item.AnalyseToken == variant.Token);
    }

    [Test]
    public async Task HasChildren_FindsTheRowsOfAKeyTree_ThatHasNoNestedSetValues()
    {
        var variant = await CreateVariantAsync("CHILD_VARIANT");
        var family = await CreateCutOrderAsync("CHILD_KEY", variant.Token);

        (await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == family.FirstPiece)).Lft.ShouldBeNull();
        (await _shiftRepository.HasChildrenAsync(family.FirstPiece)).ShouldBeTrue();
        (await _shiftRepository.HasChildrenAsync(family.SecondPiece)).ShouldBeFalse();
    }

    [Test]
    public async Task TwoSiblingsSharingAKey_OnlyOneAcceptWins_AndTheLoserChangesNothing()
    {
        var variant = await CreateVariantAsync("RACE_VARIANT");
        var family = await CreateCutOrderAsync("RACE_KEY", variant.Token);
        var winner = await CreateScenarioAsync(variant.Id);
        var loser = await CreateScenarioAsync(variant.Id);
        var winnerWork = await AddScenarioWorkAsync(winner.Token, family.SecondPiece, WorkDate);
        var loserWork = await AddScenarioWorkAsync(loser.Token, family.SecondPiece, SecondWorkDate);
        var loserHandler = AcceptHandler(_scenarioService, afterTheGates: () => AcceptInARivalContextAsync(winner.Id));

        var ex = await Should.ThrowAsync<ScenarioVariantConsumedException>(() => loserHandler.Handle(
            new AcceptAnalyseScenarioCommand(loser.Id), CancellationToken.None));

        ex.VariantNames.ShouldBe(new[] { variant.Name });
        _context.ChangeTracker.Clear();
        (await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == variant.Id))
            .Status.ShouldBe(AnalyseScenarioStatus.Accepted);
        (await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == winner.Id))
            .Status.ShouldBe(AnalyseScenarioStatus.Accepted);
        (await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == loser.Id))
            .Status.ShouldBe(AnalyseScenarioStatus.Active);
        (await _context.Work.AsNoTracking().SingleAsync(w => w.Id == loserWork)).AnalyseToken.ShouldBe(loser.Token);
        await AssertRealWorkOnAsync(winnerWork, family.SecondPiece);
    }

    [Test]
    public async Task Accept_WithKeys_StampsTheConsumedKeyLikeAnyOtherUpdate()
    {
        var variant = await CreateVariantAsync("STAMP_VARIANT");
        await CreateOrderAsync("STAMP_KEY", variant.Token);
        var scenario = await CreateScenarioAsync(variant.Id);
        var before = await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == variant.Id);

        await AcceptAsync(scenario.Id);

        var stored = await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == variant.Id);
        stored.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
        stored.UpdateTime.ShouldNotBe(before.UpdateTime);
    }

    private async Task AcceptInARivalContextAsync(Guid scenarioId)
    {
        await using var rival = NewContext();
        await using var provider = AnalyseScenarioTestServiceFactory.CreateProvider(rival);
        var handler = AcceptHandler(provider.GetRequiredService<IAnalyseScenarioService>(), rival);

        await handler.Handle(new AcceptAnalyseScenarioCommand(scenarioId), CancellationToken.None);
    }

    private async Task<Guid> CloneOfAsync(Guid token, Guid sourceShiftId)
    {
        return await _context.Shift.AsNoTracking()
            .Where(s => s.AnalyseToken == token && s.ScenarioSourceShiftId == sourceShiftId)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private async Task<Guid> AddWorkDirectlyAsync(Guid token, Guid shiftId)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _worker.Id,
            ShiftId = shiftId,
            CurrentDate = WorkDate,
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

    private async Task<Guid> AddGroupAsync(string suffix)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        await _context.Set<Group>().AddAsync(group);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return group.Id;
    }

    private async Task MoveToGroupAsync(Guid shiftId, Guid groupId)
    {
        await _context.GroupItem
            .Where(item => item.ShiftId == shiftId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.GroupId, groupId));
    }

    private async Task SetFromDateAsync(Guid shiftId, DateOnly fromDate)
    {
        await _context.Shift
            .Where(s => s.Id == shiftId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.FromDate, fromDate));
    }
}
