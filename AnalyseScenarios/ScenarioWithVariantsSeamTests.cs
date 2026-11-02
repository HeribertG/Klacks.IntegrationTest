// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of creating a scenario with variants (keys) through the real create handler, repositories and
/// clone collaborators: the clone holds every family row of the key with its parent, root and original links inside the
/// clone and ScenarioSourceShiftId on the key shift; the key and the real plan stay as they were; the group of a scenario
/// never pulls key shifts in by itself; a work on a key clone is not refused as rootless; a key sub-cut after the creation
/// is an accept conflict while children that existed at creation validate cleanly; a clone of such a scenario keeps
/// pointing at the key shift. A real order sits next to the key, so a filter that is too narrow or too wide shows up as a
/// wrong result instead of an empty one. Only INTEGRATION_TEST_ rows are written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
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
public class ScenarioWithVariantsSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARIANTSCENARIO_";
    private const string SubtreeChangedFragment = "subtree changed";
    private const decimal ExpenseAmount = 12.5m;
    private const int ChildrenOfPieceAtCreation = 1;
    private const int ChildrenOfSealedAtCreation = 3;
    private const int ChildrenOfRealOrderAtCreation = 1;
    private const int FamilyRowCount = 4;
    private static readonly DateOnly PeriodFrom = new(2099, 9, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 9, 30);
    private static readonly DateOnly WorkDate = new(2099, 9, 7);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private ScenarioVariantRepository _variantRepository = null!;
    private CreateAnalyseScenarioCommandHandler _handler = null!;
    private Group _group = null!;
    private Group _subGroup = null!;
    private Client _client = null!;
    private Shift _realOrder = null!;

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
        _variantRepository = new ScenarioVariantRepository(_context);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(_ => _context.SaveChangesAsync());
        var groupGuard = Substitute.For<IGroupVisibilityGuard>();
        groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);
        groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _handler = new CreateAnalyseScenarioCommandHandler(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            _variantRepository,
            _scenarioService,
            unitOfWork,
            groupGuard,
            Substitute.For<ILogger<CreateAnalyseScenarioCommandHandler>>());

        _group = NewGroup("GROUP", null);
        _subGroup = NewGroup("SUBGROUP", _group.Id);
        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Group>().AddAsync(_group);
        await _context.SaveChangesAsync();
        await _context.Set<Group>().AddAsync(_subGroup);
        await _context.Set<Client>().AddAsync(_client);
        await _context.SaveChangesAsync();

        _realOrder = NewShift("REAL", ShiftStatus.SealedOrder, null, _group.Id);
        await AddAsync(_realOrder);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _provider.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task Clone_HoldsEveryFamilyRow_WithAllLinksInsideTheClone_AndTheSourceOnTheKeyShift()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);

        var scenario = await CreateScenarioAsync(variant.Id);

        var clones = await ClonesOfKeyAsync(scenario.Token, family);
        clones.Keys.ShouldBe(family.Ids, ignoreOrder: true);
        var sealedClone = clones[family.SealedOrder.Id];
        var plannableClone = clones[family.Plannable.Id];
        var pieceClone = clones[family.Piece.Id];
        var childClone = clones[family.Child.Id];

        sealedClone.Status.ShouldBe(ShiftStatus.SealedOrder);
        sealedClone.OriginalId.ShouldBeNull();
        plannableClone.Status.ShouldBe(ShiftStatus.OriginalShift);
        plannableClone.OriginalId.ShouldBe(sealedClone.Id);
        pieceClone.Status.ShouldBe(ShiftStatus.SplitShift);
        pieceClone.OriginalId.ShouldBe(sealedClone.Id);
        pieceClone.RootId.ShouldBe(pieceClone.Id);
        childClone.OriginalId.ShouldBe(sealedClone.Id);
        childClone.ParentId.ShouldBe(pieceClone.Id);
        childClone.RootId.ShouldBe(pieceClone.Id);
        clones.Values.SelectMany(clone => clone.GroupItems).Count().ShouldBe(FamilyRowCount);
        clones.Values.SelectMany(clone => clone.GroupItems).ShouldAllBe(item => item.AnalyseToken == scenario.Token);
    }

    [Test]
    public async Task Clone_BringsTheDefaultExpensesOfAKeyShift()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);
        await _context.Set<ShiftExpenses>().AddAsync(new ShiftExpenses
        {
            Id = Guid.NewGuid(),
            ShiftId = family.SealedOrder.Id,
            Amount = ExpenseAmount,
            Description = TestPrefix + "EXPENSE",
            Taxable = false
        });
        await _context.SaveChangesAsync();

        var scenario = await CreateScenarioAsync(variant.Id);

        var cloneId = (await ClonesOfKeyAsync(scenario.Token, family))[family.SealedOrder.Id].Id;
        var expenses = await _context.Set<ShiftExpenses>().AsNoTracking().Where(e => e.ShiftId == cloneId).ToListAsync();
        expenses.Count.ShouldBe(1);
        expenses[0].Amount.ShouldBe(ExpenseAmount);
        expenses[0].AnalyseToken.ShouldBe(scenario.Token);
    }

    [Test]
    public async Task Creation_WritesTheLinks_AndTheResourceNamesThem()
    {
        var groupKey = await CreateVariantAsync("KEY_A", _group.Id);
        var subgroupKey = await CreateVariantAsync("KEY_B", _subGroup.Id);
        await CreateFamilyAsync(groupKey, _group.Id);
        await CreateFamilyAsync(subgroupKey, _subGroup.Id);

        var scenario = await CreateScenarioAsync(groupKey.Id, subgroupKey.Id);

        scenario.VariantIds.ShouldBe(new[] { groupKey.Id, subgroupKey.Id }, ignoreOrder: true);
        (await _variantRepository.GetVariantIdsAsync(scenario.Token)).ShouldBe(new[] { groupKey.Id, subgroupKey.Id }, ignoreOrder: true);
        var batch = await _variantRepository.GetVariantIdsByScenarioTokensAsync(new[] { scenario.Token, Guid.NewGuid() });
        batch.Keys.ShouldBe(new[] { scenario.Token });
        batch[scenario.Token].ShouldBe(new[] { groupKey.Id, subgroupKey.Id }, ignoreOrder: true);
    }

    [Test]
    public async Task Creation_LeavesTheKeyUnchanged()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);
        var before = await SnapshotKeyAsync(family);

        await CreateScenarioAsync(variant.Id);

        (await SnapshotKeyAsync(family)).ShouldBe(before, ignoreOrder: true);
        (await _context.Set<AnalyseScenario>().AsNoTracking().SingleAsync(row => row.Id == variant.Id)).Status
            .ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task TheRealPlan_ShowsNothingOfTheKeyOrItsClone()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);

        var scenario = await CreateScenarioAsync(variant.Id);

        var cloneIds = (await ClonesOfKeyAsync(scenario.Token, family)).Values.Select(clone => clone.Id).ToList();
        var realShiftIds = await _context.Shift.AsNoTracking()
            .Where(s => s.AnalyseToken == null && s.Name.StartsWith(TestPrefix))
            .Select(s => s.Id)
            .ToListAsync();
        realShiftIds.ShouldBe(new[] { _realOrder.Id });
        (await _context.Set<GroupItem>().AsNoTracking()
            .CountAsync(item => item.AnalyseToken == null && item.ShiftId != null
                && (cloneIds.Contains(item.ShiftId.Value) || family.Ids.Contains(item.ShiftId.Value))))
            .ShouldBe(0);
    }

    [Test]
    public async Task TheScenarioGroup_DoesNotPullKeyShiftsIn_WithoutTheKey()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);

        var scenario = await CreateScenarioAsync();

        var sources = await _context.Shift.AsNoTracking()
            .Where(s => s.AnalyseToken == scenario.Token)
            .Select(s => s.ScenarioSourceShiftId)
            .ToListAsync();
        sources.ShouldBe(new Guid?[] { _realOrder.Id });
        scenario.VariantIds.ShouldBeEmpty();
        (await ClonesOfKeyAsync(scenario.Token, family)).ShouldBeEmpty();
    }

    [Test]
    public async Task TheRealOrderOfTheGroup_IsClonedOnce_NextToTheKey()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        await CreateFamilyAsync(variant, _group.Id);

        var scenario = await CreateScenarioAsync(variant.Id);

        (await _context.Shift.AsNoTracking()
            .CountAsync(s => s.AnalyseToken == scenario.Token && s.ScenarioSourceShiftId == _realOrder.Id))
            .ShouldBe(1);
    }

    [Test]
    public async Task AWorkOnAKeyClone_IsNotRefusedAsRootless()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);
        var scenario = await CreateScenarioAsync(variant.Id);
        var pieceClone = (await ClonesOfKeyAsync(scenario.Token, family))[family.Piece.Id];
        await _context.Work.AddAsync(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _client.Id,
            ShiftId = pieceClone.Id,
            AnalyseToken = scenario.Token,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m
        });
        await _context.SaveChangesAsync();

        await Should.NotThrowAsync(() => _scenarioService.ValidateNoAcceptConflictsAsync(scenario.Token, CancellationToken.None));
    }

    [Test]
    public async Task KeyChildrenThatExistedAtCreation_AreInTheSnapshot_AndValidateCleanly()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);

        var scenario = await CreateScenarioAsync(variant.Id);

        var clones = await ClonesOfKeyAsync(scenario.Token, family);
        clones[family.Piece.Id].SourceChildCountSnapshot.ShouldBe(ChildrenOfPieceAtCreation);
        await Should.NotThrowAsync(() => _scenarioService.ValidateNoAcceptConflictsAsync(scenario.Token, CancellationToken.None));
    }

    [Test]
    public async Task AKeySubCutAfterTheCreation_IsAnAcceptConflict()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);
        var scenario = await CreateScenarioAsync(variant.Id);

        var subCut = NewShift("SUBCUT", ShiftStatus.SplitShift, variant.Token, _group.Id);
        subCut.OriginalId = family.SealedOrder.Id;
        subCut.ParentId = family.Piece.Id;
        subCut.RootId = family.Piece.Id;
        await AddAsync(subCut);

        var conflict = await Should.ThrowAsync<ConflictException>(
            () => _scenarioService.ValidateNoAcceptConflictsAsync(scenario.Token, CancellationToken.None));
        conflict.Message.ShouldContain(SubtreeChangedFragment);
    }

    [Test]
    public async Task AFirstLevelKeyCutAfterTheCreation_IsAnAcceptConflict()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);
        var scenario = await CreateScenarioAsync(variant.Id);

        await AddFirstLevelCutAsync(variant.Token, family.SealedOrder);

        var conflict = await Should.ThrowAsync<ConflictException>(
            () => _scenarioService.ValidateNoAcceptConflictsAsync(scenario.Token, CancellationToken.None));
        conflict.Message.ShouldContain(SubtreeChangedFragment);
    }

    [Test]
    public async Task AFirstLevelKeyCutThatExistedAtTheCreation_IsInTheSnapshot_AndValidatesCleanly()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);
        await AddFirstLevelCutAsync(variant.Token, family.SealedOrder);

        var scenario = await CreateScenarioAsync(variant.Id);

        var clones = await ClonesOfKeyAsync(scenario.Token, family);
        clones[family.SealedOrder.Id].SourceChildCountSnapshot.ShouldBe(ChildrenOfSealedAtCreation + 1);
        clones[family.Piece.Id].SourceChildCountSnapshot.ShouldBe(ChildrenOfPieceAtCreation);
        await Should.NotThrowAsync(() => _scenarioService.ValidateNoAcceptConflictsAsync(scenario.Token, CancellationToken.None));
    }

    [Test]
    public async Task ARealShiftCount_StaysOnParentId_AndIgnoresRowsLinkedOnlyByOriginalId()
    {
        var realChild = NewShift("REALCHILD", ShiftStatus.SplitShift, null, _group.Id);
        realChild.OriginalId = _realOrder.Id;
        realChild.ParentId = _realOrder.Id;
        realChild.RootId = _realOrder.Id;
        await AddAsync(realChild);
        await AddFirstLevelCutAsync(null, _realOrder);

        var scenario = await CreateScenarioAsync();
        await AddFirstLevelCutAsync(null, _realOrder);

        var realClone = await _context.Shift.AsNoTracking()
            .SingleAsync(s => s.AnalyseToken == scenario.Token && s.ScenarioSourceShiftId == _realOrder.Id);
        realClone.SourceChildCountSnapshot.ShouldBe(ChildrenOfRealOrderAtCreation);
        await Should.NotThrowAsync(() => _scenarioService.ValidateNoAcceptConflictsAsync(scenario.Token, CancellationToken.None));
    }

    [Test]
    public async Task AScenarioOnTheKeyScenario_KeepsPointingAtTheKeyShift_WithTheSnapshotOfTheKey()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        var family = await CreateFamilyAsync(variant, _group.Id);
        var source = await CreateScenarioAsync(variant.Id);
        var childToken = Guid.NewGuid();

        await _scenarioService.CloneScenarioDataAsync(_group.Id, PeriodFrom, PeriodUntil, childToken, null, source.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var clones = await ClonesOfKeyAsync(childToken, family);
        clones.Keys.ShouldBe(family.Ids, ignoreOrder: true);
        clones[family.Piece.Id].SourceChildCountSnapshot.ShouldBe(ChildrenOfPieceAtCreation);
        clones[family.Child.Id].ParentId.ShouldBe(clones[family.Piece.Id].Id);
    }

    [Test]
    public async Task AScenarioOnTheKeyScenario_ValidatesCleanly_WithTheLinksItInheritsFromItsSource()
    {
        var variant = await CreateVariantAsync("KEY", _group.Id);
        await CreateFamilyAsync(variant, _group.Id);
        var source = await CreateScenarioAsync(variant.Id);
        var childToken = Guid.NewGuid();
        await _scenarioService.CloneScenarioDataAsync(_group.Id, PeriodFrom, PeriodUntil, childToken, null, source.Token, CancellationToken.None);
        await _context.SaveChangesAsync();

        await Should.NotThrowAsync(() => _scenarioService.ValidateNoAcceptConflictsAsync(childToken, CancellationToken.None));
    }

    private async Task<AnalyseScenarioResource> CreateScenarioAsync(params Guid[] variantIds)
    {
        var scenario = await _handler.Handle(
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

    private async Task<AnalyseScenario> CreateVariantAsync(string suffix, Guid groupId)
    {
        var variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            GroupId = groupId,
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

    private async Task<KeyFamily> CreateFamilyAsync(AnalyseScenario variant, Guid groupId)
    {
        var sealedOrder = NewShift("SEALED", ShiftStatus.SealedOrder, variant.Token, groupId);
        await AddAsync(sealedOrder);

        var plannable = NewShift("PLANNABLE", ShiftStatus.OriginalShift, variant.Token, groupId);
        plannable.OriginalId = sealedOrder.Id;
        await AddAsync(plannable);

        var piece = NewShift("PIECE", ShiftStatus.SplitShift, variant.Token, groupId);
        piece.OriginalId = sealedOrder.Id;
        piece.RootId = piece.Id;
        await AddAsync(piece);

        var child = NewShift("CHILD", ShiftStatus.SplitShift, variant.Token, groupId);
        child.OriginalId = sealedOrder.Id;
        child.ParentId = piece.Id;
        child.RootId = piece.Id;
        await AddAsync(child);

        return new KeyFamily(sealedOrder, plannable, piece, child);
    }

    private async Task AddFirstLevelCutAsync(Guid? token, Shift sealedOrder)
    {
        var cut = NewShift("FIRSTCUT", ShiftStatus.SplitShift, token, _group.Id);
        cut.OriginalId = sealedOrder.Id;
        cut.RootId = cut.Id;
        await AddAsync(cut);
    }

    private async Task AddAsync(Shift shift)
    {
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Shift NewShift(string suffix, ShiftStatus status, Guid? token, Guid groupId)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Scenario with variants seam",
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
            AnalyseToken = token,
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = id, AnalyseToken = token }]
        };
    }

    private static Group NewGroup(string suffix, Guid? parent) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        Description = string.Empty,
        ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Parent = parent
    };

    private async Task<Dictionary<Guid, Shift>> ClonesOfKeyAsync(Guid token, KeyFamily family)
    {
        var clones = await _context.Shift.AsNoTracking()
            .Include(s => s.GroupItems.Where(item => !item.IsDeleted))
            .Where(s => s.AnalyseToken == token
                && s.ScenarioSourceShiftId != null
                && family.Ids.Contains(s.ScenarioSourceShiftId.Value))
            .ToListAsync();

        return clones.ToDictionary(clone => clone.ScenarioSourceShiftId!.Value);
    }

    private async Task<List<KeyRowState>> SnapshotKeyAsync(KeyFamily family)
    {
        return await _context.Shift.AsNoTracking()
            .Where(s => family.Ids.Contains(s.Id))
            .Select(s => new KeyRowState(
                s.Id, s.AnalyseToken, s.Status, s.OriginalId, s.ParentId, s.RootId, s.ScenarioSourceShiftId, s.UntilDate, s.IsDeleted))
            .ToListAsync();
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
            DELETE FROM analyse_scenario_variants WHERE variant_id IN (SELECT id FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
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

    private sealed record KeyFamily(Shift SealedOrder, Shift Plannable, Shift Piece, Shift Child)
    {
        public List<Guid> Ids { get; } = [SealedOrder.Id, Plannable.Id, Piece.Id, Child.Id];
    }

    private sealed record KeyRowState(
        Guid Id,
        Guid? AnalyseToken,
        ShiftStatus Status,
        Guid? OriginalId,
        Guid? ParentId,
        Guid? RootId,
        Guid? ScenarioSourceShiftId,
        DateOnly? UntilDate,
        bool IsDeleted);
}
