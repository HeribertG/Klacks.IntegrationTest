// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proofs of the group rule on chained scenarios and of the freeze of a key in use. A scenario built on a
/// scenario only inherits the keys inside its own group hierarchy and leaves the clones of any other key behind; a clone that
/// gets past that rule is refused at the accept; a restricted caller cannot accept a key of a group they do not see, however it reached the scenario. While an
/// Active scenario carries a key, inherited links included, cut and reset are conflicts and the reads still work; discarding
/// the last carrier thaws the key. The key list reads the subgroups, visible ones only. Only INTEGRATION_TEST_ rows are
/// written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Handlers.ScenarioVariants;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.ScenarioVariants;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class VariantKeyInUseSeamTests : VariantPromotionSeamTestBase
{
    [Test]
    public async Task ChainedScenario_InheritsOnlyTheKeysInsideItsOwnHierarchy()
    {
        var (first, second) = await AddSubgroupsAsync();
        var inside = await CreateVariantInGroupAsync("INSIDE", second);
        var outside = await CreateVariantInGroupAsync("OUTSIDE", first);
        var source = await CreateScenarioAsync(inside.Id, outside.Id);

        var child = await CreateChildInGroupAsync(source, second);

        (await new ScenarioVariantRepository(_context).GetVariantIdsAsync(child.Token)).ShouldBe([inside.Id]);
    }

    [Test]
    public async Task ChildLeavesTheClonesOfAKeyItDoesNotInheritBehind_AndAcceptRefusesOneThatGotPastThatRule()
    {
        var (first, second) = await AddSubgroupsAsync();
        var outside = await CreateVariantInGroupAsync("HELD", first);
        var order = await CreateOrderAsync("HELD_ORDER", outside.Token);
        var source = await CreateScenarioAsync(outside.Id);
        var sourceClones = await _context.Shift.AsNoTracking()
            .Where(s => s.AnalyseToken == source.Token && s.ScenarioSourceShiftId == order.Plannable.Id)
            .Select(s => s.Id)
            .ToListAsync();

        var child = await CreateChildInGroupAsync(source, second, sourceClones);

        (await new ScenarioVariantRepository(_context).GetVariantIdsAsync(child.Token)).ShouldBeEmpty();
        (await _context.Shift.AsNoTracking().CountAsync(s => s.AnalyseToken == child.Token && s.ScenarioSourceShiftId == order.Plannable.Id))
            .ShouldBe(0);

        var heldClone = await _context.Shift.AsNoTracking().FirstAsync(s => s.Id == sourceClones[0]);
        heldClone.Id = Guid.NewGuid();
        heldClone.AnalyseToken = child.Token;
        await _context.Shift.AddAsync(heldClone);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ex = await Should.ThrowAsync<ConflictException>(
            () => _scenarioService.ValidateNoAcceptConflictsAsync(child.Token, CancellationToken.None));
        ex.Message.ShouldBe(ScenarioAcceptConflictValidator.UncarriedVariantSourceMessage);
    }

    [Test]
    public async Task Accept_ByACallerWhoCannotSeeAKeyGroup_IsAnsweredLikeAMissingScenario_AndChangesNothing()
    {
        var (hiddenGroup, _) = await AddSubgroupsAsync();
        var hidden = await CreateVariantInGroupAsync("HIDDEN", hiddenGroup);
        await CreateOrderAsync("HIDDEN_ORDER", hidden.Token);
        var scenario = await CreateScenarioAsync(hidden.Id);
        var restricted = Substitute.For<IGroupVisibilityGuard>();
        restricted.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) != hiddenGroup);
        await using var provider = AnalyseScenarioTestServiceFactory.CreateProvider(_context, groupVisibilityGuard: restricted);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => AcceptAsync(scenario.Id, provider.GetRequiredService<IAnalyseScenarioService>()));

        ex.Message.ShouldBe(string.Format(ScenarioGroupVisibility.ScenarioNotFoundFormat, scenario.Id));
        _context.ChangeTracker.Clear();
        (await StatusOfAsync(scenario.Id)).ShouldBe(AnalyseScenarioStatus.Active);
        (await StatusOfAsync(hidden.Id)).ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task CutAndReset_OfACarriedKey_AreConflicts_AndWriteNothing()
    {
        var variant = await CreateVariantAsync("CARRIED");
        var family = await CreateCutOrderAsync("CARRIED_ORDER", variant.Token);
        await CreateScenarioAsync(variant.Id);
        var before = await SnapshotAsync(family.Ids);
        var piece = await PieceOfAsync(family.SecondPiece, Guid.NewGuid(), SecondPieceFrom, SecondChildUntil);

        await ShouldBeInUseAsync(() => _facade.ProcessBatchCutsAsync([Create(family.SecondPiece, piece)]));
        await ShouldBeInUseAsync(() => _facade.ResetCutsAsync(family.SealedOrder, SecondPieceFrom));

        _context.ChangeTracker.Clear();
        (await SnapshotAsync(family.Ids)).ShouldBe(before);
        (await _context.Shift.AsNoTracking().CountAsync(s => s.Id == piece.Id)).ShouldBe(0);
    }

    [Test]
    public async Task AnInheritedLink_KeepsTheKeyFrozen_AfterTheSourceIsDiscarded_UntilTheChildIsDiscardedToo()
    {
        var variant = await CreateVariantAsync("INHERITED");
        var (sealedOrder, plannable) = await CreateOrderAsync("INHERITED_ORDER", variant.Token);
        var source = await CreateScenarioAsync(variant.Id);
        var child = await CreateChainedScenarioAsync(source);

        await RejectHandler().Handle(new RejectAnalyseScenarioCommand(source.Id), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var first = await PieceOfAsync(plannable.Id, plannable.Id, PeriodFrom, FirstPieceUntil);
        await ShouldBeInUseAsync(() => _facade.ProcessBatchCutsAsync([Update(first)]));

        await RejectHandler().Handle(new RejectAnalyseScenarioCommand(child.Id), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var second = await PieceOfAsync(plannable.Id, plannable.Id, PeriodFrom, FirstPieceUntil);
        await Should.NotThrowAsync(() => _facade.ProcessBatchCutsAsync([Update(second)]));
    }

    [Test]
    public async Task Reads_OfACarriedKey_KeepWorking()
    {
        var variant = await CreateVariantAsync("READ");
        var family = await CreateCutOrderAsync("READ_ORDER", variant.Token);
        await CreateScenarioAsync(variant.Id);
        var groupGuard = UnrestrictedGroupVisibility.Create();
        var writeGuard = new VariantShiftWriteGuard(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            new ScenarioVariantRepository(_context),
            _scenarioService,
            groupGuard);
        var handler = new CutListQueryHandler(
            _shiftRepository,
            Substitute.For<IShiftGroupFilterService>(),
            _mapper,
            writeGuard,
            Substitute.For<ILogger<CutListQueryHandler>>());

        var cuts = (await handler.Handle(new CutListQuery(family.SealedOrder), CancellationToken.None)).ToList();

        cuts.Select(cut => cut.Id).ShouldContain(family.FirstPiece);
        cuts.Select(cut => cut.Id).ShouldContain(family.SecondPiece);
    }

    [Test]
    public async Task KeyList_ReadsTheVisibleSubgroups_AndNoHiddenOne()
    {
        var (visibleSubgroup, hiddenSubgroup) = await AddSubgroupsAsync();
        var own = await CreateVariantInGroupAsync("LIST_OWN", _group.Id);
        var inSubgroup = await CreateVariantInGroupAsync("LIST_SUB", visibleSubgroup);
        await CreateVariantInGroupAsync("LIST_HIDDEN", hiddenSubgroup);
        var restricted = Substitute.For<IGroupVisibilityGuard>();
        restricted.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) != hiddenSubgroup);
        var handler = new ListScenarioVariantsQueryHandler(
            new ScenarioVariantRepository(_context),
            restricted,
            _scenarioService,
            TestCompanyClock.Utc(),
            Substitute.For<ILogger<ListScenarioVariantsQueryHandler>>());

        var result = await handler.Handle(new ListScenarioVariantsQuery(_group.Id), CancellationToken.None);

        result.Select(key => key.Id).ShouldBe([own.Id, inSubgroup.Id], ignoreOrder: true);
    }

    private static async Task ShouldBeInUseAsync(Func<Task> write)
    {
        var ex = await Should.ThrowAsync<ScenarioVariantConflictException>(write);

        ex.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.InUse);
    }

    private async Task<AnalyseScenarioStatus> StatusOfAsync(Guid id)
        => await _context.Set<AnalyseScenario>().AsNoTracking().Where(s => s.Id == id).Select(s => s.Status).SingleAsync();

    private async Task<(Guid First, Guid Second)> AddSubgroupsAsync()
    {
        var first = NewSubgroup("SUB_A");
        var second = NewSubgroup("SUB_B");
        await _context.Set<Group>().AddRangeAsync(first, second);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (first.Id, second.Id);
    }

    private Group NewSubgroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        Description = string.Empty,
        Parent = _group.Id,
        ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private async Task<AnalyseScenario> CreateVariantInGroupAsync(string suffix, Guid groupId)
    {
        var variant = await CreateVariantAsync(suffix);
        await _context.Set<AnalyseScenario>().Where(s => s.Id == variant.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.GroupId, groupId));
        _context.ChangeTracker.Clear();
        variant.GroupId = groupId;
        return variant;
    }

    private async Task<AnalyseScenario> CreateChildInGroupAsync(
        Klacks.Api.Application.DTOs.Schedules.AnalyseScenarioResource source, Guid groupId, IReadOnlyCollection<Guid>? additionalShiftIds = null)
    {
        var child = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CHILD",
            GroupId = groupId,
            Token = Guid.NewGuid(),
            SourceToken = source.Token,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active,
            Kind = AnalyseScenarioKind.Scenario,
            CreatedByUser = TestPrefix + "USER"
        };
        await _context.Set<AnalyseScenario>().AddAsync(child);
        await _scenarioService.CloneScenarioDataAsync(
            groupId, PeriodFrom, PeriodUntil, child.Token, additionalShiftIds, source.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return child;
    }
}
