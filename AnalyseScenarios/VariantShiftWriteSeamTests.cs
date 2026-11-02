// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of shifts in a variant (key), through the real handlers, guard and repositories: POST writes the
/// sealed order and its plannable copy with the token on both shifts and all group items; PUT keeps the token on the shift
/// and on kept and new group items, and mirrors the new groups onto the other row of the pair (VariantShiftFamilySeamTests
/// covers the mirror in full); DELETE removes the whole family; the real-plan readers and the sealed order export
/// show neither key shifts nor scenario clones (GetOpenOrdersAsync is not covered: it reads drafts only, and a key shift is
/// never a draft); satellites carry no token, are copied into a scenario and go with the key.
/// A real shift and its scenario clone sit next to the key shifts, so a filter that is too narrow or too wide shows up as
/// a wrong result instead of an empty one. Only INTEGRATION_TEST_ rows are written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Exports;
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
public class VariantShiftWriteSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARIANTWRITE_";
    private const int FamilyRowCount = 2;
    private static readonly DateOnly PeriodFrom = new(2099, 8, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 31);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private ShiftRepository _shiftRepository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private VariantShiftWriteGuard _writeGuard = null!;
    private AnalyseScenarioService _scenarioService = null!;
    private Group _group = null!;
    private Group _subGroup = null!;
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
            new ShiftValidator(),
            _mapper,
            TestCompanyClock.Utc());

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.CompleteAsync().Returns(_ => _context.SaveChangesAsync());

        var groupGuard = Substitute.For<IGroupVisibilityGuard>();
        groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);
        groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _writeGuard = new VariantShiftWriteGuard(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            new ScenarioVariantRepository(_context),
            _scenarioService,
            groupGuard);

        _group = await CreateGroupAsync("GROUP", null);
        _subGroup = await CreateGroupAsync("SUBGROUP", _group.Id);
        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false,
            Type = EntityTypeEnum.Customer
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
    public async Task Post_WritesSealedOrderAndPlannableCopy_WithTheTokenOnBothShiftsAndAllGroupItems()
    {
        var variant = await CreateVariantAsync("VARIANT");

        await PostKeyShiftAsync("POSTED", variant.Token);

        var family = await LoadFamilyAsync(variant.Token);
        family.Count.ShouldBe(FamilyRowCount);
        family.Select(s => s.Status).ShouldBe(new[] { ShiftStatus.SealedOrder, ShiftStatus.OriginalShift }, ignoreOrder: true);
        family.Single(s => s.Status == ShiftStatus.OriginalShift).OriginalId
            .ShouldBe(family.Single(s => s.Status == ShiftStatus.SealedOrder).Id);
        await ShouldHoldGroupItemParityAsync(variant.Token, expectedItems: FamilyRowCount);
    }

    [Test]
    public async Task Put_KeepsTheTokenOnTheShiftAndOnKeptAndNewGroupItems()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var posted = await PostKeyShiftAsync("EDITED", variant.Token);
        var resource = _mapper.ToShiftResource((await _shiftRepository.Get(posted.Id))!);
        resource.AnalyseToken = null;
        resource.Client = null;
        resource.Name = TestPrefix + "RENAMED";
        resource.Groups = [new SimpleGroupResource { Id = _group.Id }, new SimpleGroupResource { Id = _subGroup.Id }];
        _context.ChangeTracker.Clear();

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        _context.ChangeTracker.Clear();
        var edited = await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == posted.Id);
        edited.AnalyseToken.ShouldBe(variant.Token);
        edited.Name.ShouldBe(TestPrefix + "RENAMED");
        await ShouldHoldGroupItemParityAsync(variant.Token, expectedItems: FamilyRowCount * 2);
    }

    [Test]
    public async Task Put_KeepsTheCopyReference_BecauseTheFormDoesNotCarryIt()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var posted = await PostKeyShiftAsync("COPIED", variant.Token);
        var realOrderId = await CreateRealOrderIdAsync("REALORDER");
        await _context.Shift.Where(s => s.Id == posted.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.CopiedFromShiftId, realOrderId));
        _context.ChangeTracker.Clear();
        var resource = _mapper.ToShiftResource((await _shiftRepository.Get(posted.Id))!);
        resource.AnalyseToken = null;
        resource.Client = null;
        resource.Name = TestPrefix + "RENAMED";
        resource.Groups = [new SimpleGroupResource { Id = _group.Id }];
        _context.ChangeTracker.Clear();

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        _context.ChangeTracker.Clear();
        var edited = await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == posted.Id);
        edited.Name.ShouldBe(TestPrefix + "RENAMED");
        edited.CopiedFromShiftId.ShouldBe(realOrderId);
    }

    [Test]
    public async Task PutAndDelete_OfAScenarioClone_StayRefusedByTheRepository()
    {
        var (_, cloneId) = await CreateRealShiftWithCloneAsync("CLONED");

        var clone = await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == cloneId);
        _context.ChangeTracker.Clear();

        (await Should.ThrowAsync<InvalidOperationException>(() => _shiftRepository.Put(clone)))
            .Message.ShouldContain("scenario mode");
        (await Should.ThrowAsync<InvalidOperationException>(() => _shiftRepository.Delete(cloneId)))
            .Message.ShouldContain("scenario mode");
    }

    [Test]
    public async Task RepositoryPutAndDelete_OfAKeyShift_AreNotRefused()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var posted = await PostKeyShiftAsync("REPOSITORY", variant.Token);
        var stored = await _context.Shift.AsNoTracking().Include(s => s.GroupItems).SingleAsync(s => s.Id == posted.Id);
        _context.ChangeTracker.Clear();

        (await _shiftRepository.Put(stored)).ShouldNotBeNull();
        (await _shiftRepository.Delete(posted.Id)).ShouldNotBeNull();
    }

    [Test]
    public async Task Delete_RemovesTheWholeFamilyWithItsGroupItems_AddressedByThePlannableCopy()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var posted = await PostKeyShiftAsync("DELETED", variant.Token);
        posted.Status.ShouldBe(ShiftStatus.OriginalShift);
        _context.ChangeTracker.Clear();

        await DeleteHandler().Handle(new DeleteCommand<ShiftResource>(posted.Id), CancellationToken.None);

        _context.ChangeTracker.Clear();
        (await LoadFamilyAsync(variant.Token)).ShouldBeEmpty();
        (await _context.Set<GroupItem>().AsNoTracking().CountAsync(gi => gi.AnalyseToken == variant.Token && !gi.IsDeleted))
            .ShouldBe(0);
    }

    [Test]
    public async Task Delete_InAConsumedKey_IsRefusedAndTheFamilyStays()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var posted = await PostKeyShiftAsync("KEPT", variant.Token);
        await SetVariantStatusAsync(variant, AnalyseScenarioStatus.Accepted);
        _context.ChangeTracker.Clear();

        await Should.ThrowAsync<ConflictException>(
            () => DeleteHandler().Handle(new DeleteCommand<ShiftResource>(posted.Id), CancellationToken.None));

        _context.ChangeTracker.Clear();
        (await LoadFamilyAsync(variant.Token)).Count.ShouldBe(FamilyRowCount);
    }

    [Test]
    public async Task Get_ServesAKeyShift_AndStillRefusesAScenarioClone()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var posted = await PostKeyShiftAsync("READ", variant.Token);
        var (_, cloneId) = await CreateRealShiftWithCloneAsync("READ_CLONED");
        var handler = GetHandler();

        (await handler.Handle(new GetQuery<ShiftResource>(posted.Id), CancellationToken.None)).Id.ShouldBe(posted.Id);
        await Should.ThrowAsync<InvalidRequestException>(
            () => handler.Handle(new GetQuery<ShiftResource>(cloneId), CancellationToken.None));
    }

    [Test]
    public async Task RealPlanReaders_ShowNoKeyShift()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var name = TestPrefix + "HIDDEN";
        await PostKeyShiftAsync("HIDDEN", variant.Token);
        var real = await CreateShiftAsync("VISIBLE", null);
        var keyIds = (await LoadFamilyAsync(variant.Token)).Select(s => s.Id).ToList();
        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Original,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true,
            SearchString = string.Empty,
            IncludeClientName = false,
            IsSealedOrder = true,
            OrderBy = string.Empty,
            SortOrder = string.Empty
        };

        var plannable = await _shiftRepository.FindPlannableByNameAsync(name, null, FamilyRowCount);
        var listed = await _shiftRepository.FilterShifts(filter, PeriodFrom, null)
            .Where(s => s.Name.StartsWith(TestPrefix))
            .ToListAsync();
        var placement = await _shiftRepository.GetShiftsForCityGroupPlacementAsync(TestPrefix);

        plannable.ShouldBeEmpty();
        listed.Select(s => s.Id).ShouldContain(real.Id, "the filter must keep real shifts, or it proves nothing");
        listed.ShouldNotContain(s => keyIds.Contains(s.Id));
        placement.ShouldNotContain(s => keyIds.Contains(s.Id));
    }

    [Test]
    public async Task SealedOrderExport_ListsNeitherKeyShiftsNorScenarioClones()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var real = await CreateShiftAsync("EXPORT_REAL", null, clientId: _client.Id);
        var key = await CreateShiftAsync("EXPORT_KEY", variant.Token, clientId: _client.Id);
        var clone = await CreateShiftAsync("EXPORT_CLONE", Guid.NewGuid(), sourceShiftId: real.Id, clientId: _client.Id);
        var promotedLookingClone = await CreateShiftAsync("EXPORT_SOURCED", null, sourceShiftId: real.Id, clientId: _client.Id);
        var loader = new SealedOrderListLoader(_context, new ShiftDescendantResolver(_context));

        var listed = await loader.LoadAsync(null, null, null, TestPrefix);

        listed.Select(item => item.Id).ShouldBe(new[] { real.Id });
        listed.ShouldNotContain(item => item.Id == key.Id || item.Id == clone.Id || item.Id == promotedLookingClone.Id);
    }

    [Test]
    public async Task SealedOrderExport_FindsAnOrderThroughARealPieceButNotThroughAKeyPiece()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var real = await CreateShiftAsync("EXPORT_ORDER", null, clientId: _client.Id);
        var other = await CreateShiftAsync("EXPORT_OTHER", null, clientId: _client.Id);
        await CreateShiftAsync("EXPORT_REALPIECE", null, ShiftStatus.OriginalShift, originalId: real.Id);
        await CreateShiftAsync("EXPORT_KEYPIECE", variant.Token, ShiftStatus.OriginalShift, originalId: other.Id);
        var loader = new SealedOrderListLoader(_context, new ShiftDescendantResolver(_context));

        var byRealPiece = await loader.LoadAsync(null, null, null, TestPrefix + "EXPORT_REALPIECE");
        var byKeyPiece = await loader.LoadAsync(null, null, null, TestPrefix + "EXPORT_KEYPIECE");

        byRealPiece.Select(item => item.Id).ShouldBe(new[] { real.Id });
        byKeyPiece.ShouldBeEmpty();
    }

    [Test]
    public async Task DescendantResolver_FollowsRealPiecesOnly()
    {
        var variant = await CreateVariantAsync("VARIANT");
        var order = await CreateShiftAsync("TREE_ORDER", null);
        var realPiece = await CreateShiftAsync("TREE_REAL", null, ShiftStatus.OriginalShift, originalId: order.Id);
        await CreateShiftAsync("TREE_KEY", variant.Token, ShiftStatus.OriginalShift, originalId: order.Id);
        await CreateShiftAsync("TREE_CLONE", Guid.NewGuid(), ShiftStatus.OriginalShift, originalId: order.Id, sourceShiftId: realPiece.Id);

        var map = await new ShiftDescendantResolver(_context).ResolveAsync([order.Id], includeRoot: false);

        map[order.Id].ShouldBe(new[] { realPiece.Id }, ignoreOrder: true);
    }

    [Test]
    public async Task Satellites_CarryNoToken_AreCopiedIntoAScenario_AndGoWithTheKey()
    {
        var variant = await CreateVariantAsync("VARIANT");
        await PostKeyShiftAsync("SATELLITES", variant.Token, withExpense: true);
        var family = await LoadFamilyAsync(variant.Token);
        var sealedOrderId = family.Single(s => s.Status == ShiftStatus.SealedOrder).Id;
        var preference = new ClientShiftPreference
        {
            Id = Guid.NewGuid(),
            ClientId = _client.Id,
            ShiftId = sealedOrderId,
            PreferenceType = default
        };
        await _context.Set<ClientShiftPreference>().AddAsync(preference);
        await _context.SaveChangesAsync();
        var familyIds = family.Select(s => s.Id).ToList();

        var expenses = await _context.Set<ShiftExpenses>().AsNoTracking()
            .Where(e => familyIds.Contains(e.ShiftId)).ToListAsync();
        expenses.Count.ShouldBe(FamilyRowCount, "the plannable copy takes the default expense over");
        expenses.ShouldAllBe(e => e.AnalyseToken == null);

        var scenarioToken = Guid.NewGuid();
        var targetShift = await CreateShiftAsync("SATELLITE_TARGET", scenarioToken, ShiftStatus.OriginalShift);
        var cloner = _provider.GetRequiredService<ScenarioShiftSatelliteCloner>();
        var shiftIdMap = new Dictionary<Guid, Guid> { [sealedOrderId] = targetShift.Id };
        await cloner.CloneShiftExpenses(shiftIdMap, scenarioToken, ScenarioCloneSource.RealPlan, CancellationToken.None);
        await cloner.CloneClientShiftPreferences(shiftIdMap, scenarioToken, ScenarioCloneSource.RealPlan, CancellationToken.None);
        await _context.SaveChangesAsync();

        (await _context.Set<ShiftExpenses>().AsNoTracking().CountAsync(e => e.ShiftId == targetShift.Id && e.AnalyseToken == scenarioToken))
            .ShouldBe(1);
        (await _context.Set<ClientShiftPreference>().AsNoTracking().CountAsync(p => p.ShiftId == targetShift.Id && p.AnalyseToken == scenarioToken))
            .ShouldBe(1);

        await _scenarioService.SoftDeleteScenarioDataAsync(variant.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await LoadFamilyAsync(variant.Token)).ShouldBeEmpty();
        (await _context.Set<ShiftExpenses>().AsNoTracking().CountAsync(e => familyIds.Contains(e.ShiftId) && !e.IsDeleted)).ShouldBe(0);
        (await _context.Set<ClientShiftPreference>().AsNoTracking().CountAsync(p => familyIds.Contains(p.ShiftId) && !p.IsDeleted)).ShouldBe(0);
        (await _context.Set<GroupItem>().IgnoreQueryFilters().AsNoTracking()
            .CountAsync(gi => gi.ShiftId != null && familyIds.Contains(gi.ShiftId.Value) && !gi.IsDeleted)).ShouldBe(0);
    }

    [Test]
    public async Task DiscardingAKey_LeavesTheRealShiftAndItsSatellitesAlone()
    {
        var variant = await CreateVariantAsync("VARIANT");
        await PostKeyShiftAsync("DISCARDED", variant.Token, withExpense: true);
        var real = await CreateShiftAsync("UNTOUCHED", null, ShiftStatus.OriginalShift);
        await _context.Set<ShiftExpenses>().AddAsync(new ShiftExpenses
        {
            Id = Guid.NewGuid(),
            ShiftId = real.Id,
            Amount = 1m,
            Description = TestPrefix + "EXPENSE"
        });
        await _context.SaveChangesAsync();

        await _scenarioService.SoftDeleteScenarioDataAsync(variant.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _context.Shift.AsNoTracking().CountAsync(s => s.Id == real.Id && !s.IsDeleted)).ShouldBe(1);
        (await _context.Set<ShiftExpenses>().AsNoTracking().CountAsync(e => e.ShiftId == real.Id && !e.IsDeleted)).ShouldBe(1);
    }

    private async Task<ShiftResource> PostKeyShiftAsync(string suffix, Guid token, bool withExpense = false)
    {
        var resource = new ShiftResource
        {
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            ClientId = _client.Id,
            Status = ShiftStatus.OriginalOrder,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            AnalyseToken = token,
            Groups = [new SimpleGroupResource { Id = _group.Id }],
            DefaultExpenses = withExpense
                ? [new ShiftExpensesResource { Amount = 1m, Description = TestPrefix + "EXPENSE" }]
                : []
        };

        var posted = await PostHandler().Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);
        return posted!;
    }

    private async Task<Guid> CreateRealOrderIdAsync(string suffix)
    {
        var (realId, _) = await CreateRealShiftWithCloneAsync(suffix);
        return realId;
    }

    private async Task<List<Shift>> LoadFamilyAsync(Guid token)
    {
        return await _context.Shift.AsNoTracking().Where(s => s.AnalyseToken == token && !s.IsDeleted).ToListAsync();
    }

    private async Task ShouldHoldGroupItemParityAsync(Guid token, int expectedItems)
    {
        var familyIds = (await LoadFamilyAsync(token)).Select(s => (Guid?)s.Id).ToList();
        var items = await _context.Set<GroupItem>().AsNoTracking()
            .Where(gi => familyIds.Contains(gi.ShiftId) && !gi.IsDeleted)
            .ToListAsync();

        items.Count.ShouldBe(expectedItems);
        items.ShouldAllBe(gi => gi.AnalyseToken == token);
    }

    private async Task<(Guid RealId, Guid CloneId)> CreateRealShiftWithCloneAsync(string suffix)
    {
        var real = await CreateShiftAsync(suffix, null, ShiftStatus.OriginalShift);
        var idMap = await _scenarioService.CloneScenarioDataAsync(
            groupId: null,
            fromDate: PeriodFrom,
            untilDate: PeriodUntil,
            token: Guid.NewGuid(),
            additionalShiftIds: new[] { real.Id },
            null,
            ct: CancellationToken.None);
        await _context.SaveChangesAsync();
        return (real.Id, idMap[real.Id]);
    }

    private async Task<Shift> CreateShiftAsync(
        string suffix,
        Guid? analyseToken,
        ShiftStatus status = ShiftStatus.SealedOrder,
        Guid? originalId = null,
        Guid? sourceShiftId = null,
        Guid? clientId = null)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Variant write seam",
            Status = status,
            FromDate = PeriodFrom,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            OriginalId = originalId,
            ScenarioSourceShiftId = sourceShiftId,
            AnalyseToken = analyseToken,
            ClientId = clientId
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
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

    private async Task SetVariantStatusAsync(AnalyseScenario variant, AnalyseScenarioStatus status)
    {
        variant.Status = status;
        await _context.SaveChangesAsync();
    }

    private PostCommandHandler PostHandler()
    {
        var sealing = Substitute.For<IOrderSealingService>();
        sealing.CollectMissingRequirements(Arg.Any<Shift>()).Returns(Array.Empty<string>());
        return new PostCommandHandler(
            _shiftRepository,
            _mapper,
            _unitOfWork,
            Substitute.For<IDefaultShiftMacroResolver>(),
            sealing,
            Substitute.For<IShiftVisibilityGuard>(),
            Substitute.For<IClientReferenceGuard>(),
            _writeGuard,
            Substitute.For<ILogger<PostCommandHandler>>());
    }

    private PutCommandHandler PutHandler()
    {
        var visibility = Substitute.For<IShiftVisibilityGuard>();
        visibility.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return new PutCommandHandler(
            _shiftRepository,
            Substitute.For<IShiftRequiredQualificationRepository>(),
            _mapper,
            _unitOfWork,
            visibility,
            Substitute.For<IClientReferenceGuard>(),
            _writeGuard,
            Substitute.For<Klacks.Api.Application.Interfaces.IShiftFamilyEndService>(),
            Substitute.For<ILogger<PutCommandHandler>>());
    }

    private DeleteCommandHandler DeleteHandler()
    {
        var visibility = Substitute.For<IShiftVisibilityGuard>();
        visibility.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>()).Returns(true);
        return new DeleteCommandHandler(
            _shiftRepository,
            _mapper,
            _unitOfWork,
            Substitute.For<ISealedOrderFamilyDeletion>(),
            Substitute.For<IHttpContextAccessor>(),
            visibility,
            _writeGuard,
            Substitute.For<ILogger<DeleteCommandHandler>>());
    }

    private GetQueryHandler GetHandler()
    {
        var groupFilter = Substitute.For<IShiftGroupFilterService>();
        groupFilter.ResolveShiftListGroupIdsAsync(Arg.Any<Guid?>()).Returns(Task.FromResult<List<Guid>?>(null));
        var tokenGuard = Substitute.For<IScenarioTokenAccessGuard>();
        tokenGuard.IsVariantTokenVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.ArgAt<Guid>(0);
                return _context.Set<AnalyseScenario>().AnyAsync(s => s.Token == token && s.Kind == AnalyseScenarioKind.Variant);
            });
        return new GetQueryHandler(_shiftRepository, groupFilter, _mapper, tokenGuard, Substitute.For<ILogger<GetQueryHandler>>());
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
            UPDATE shift SET scenario_source_shift_id = NULL, original_id = NULL WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
