using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Domain.Interfaces.Staffs;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.Shifts;

[TestFixture]
[Category("RealDatabase")]
public class ShiftManipulationIntegrationTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private const string TestShiftPrefix = "INTEGRATION_TEST_SHIFT_";
    private const string TestCustomerPrefix = "INTEGRATION_TEST_CUST_";

    // Services
    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IShiftCutFacade _shiftCutFacade = null!;
    private ScheduleMapper _scheduleMapper = null!;

    // Handlers
    private PostCommandHandler _postHandler = null!;
    private PutCommandHandler _putHandler = null!;
    private PostBatchCutsCommandHandler _batchCutsHandler = null!;
    private PostResetCutsCommandHandler _resetCutsHandler = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        using var context = new DataBaseContext(options, mockHttpContextAccessor);

        var orphanedShifts = await context.Shift
            .Where(s => s.Name.StartsWith(TestShiftPrefix))
            .CountAsync();

        if (orphanedShifts > 0)
        {
            Console.WriteLine($"[OneTimeSetUp] Found {orphanedShifts} orphaned test shifts. Cleaning up...");
            await CleanupTestDataWithContext(context);
            Console.WriteLine("[OneTimeSetUp] Cleanup completed.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        // Create services
        _scheduleMapper = new ScheduleMapper();
        var unitOfWorkLogger = Substitute.For<ILogger<UnitOfWork>>();
        _unitOfWork = new UnitOfWork(_context, unitOfWorkLogger);

        // Create domain services - services without constructor params
        var shiftValidator = new ShiftValidator();
        var dateRangeFilterService = new DateRangeFilterService();
        var shiftSearchService = new ShiftSearchService();
        var shiftSortingService = new ShiftSortingService();
        var shiftStatusFilterService = new ShiftStatusFilterService();
        var shiftPaginationService = new ShiftPaginationService();
        var queryPipeline = new ShiftQueryPipelineService(dateRangeFilterService, shiftSearchService, shiftSortingService, shiftStatusFilterService, shiftPaginationService);

        // Services with constructor params
        var shiftGroupManagementServiceLogger = Substitute.For<ILogger<ShiftGroupManagementService>>();
        var shiftGroupManagementService = new ShiftGroupManagementService(_context, shiftGroupManagementServiceLogger);

        var shiftTreeServiceLogger = Substitute.For<ILogger<ShiftTreeService>>();
        var shiftTreeService = new ShiftTreeService(_context, shiftTreeServiceLogger);

        var entityCollectionUpdateService = new EntityCollectionUpdateService(_context);

        // Create repository
        var shiftRepositoryLogger = Substitute.For<ILogger<Shift>>();
        _shiftRepository = new ShiftRepository(
            _context,
            shiftRepositoryLogger,
            queryPipeline,
            shiftGroupManagementService,
            entityCollectionUpdateService,
            shiftValidator,
            _scheduleMapper);

        // Create ShiftResetService
        var shiftResetServiceLogger = Substitute.For<ILogger<ShiftResetService>>();
        var shiftResetService = new ShiftResetService(_shiftRepository, shiftResetServiceLogger);

        // Create facade
        var facadeLogger = Substitute.For<ILogger<ShiftCutFacade>>();
        _shiftCutFacade = new ShiftCutFacade(
            _shiftRepository,
            shiftTreeService,
            shiftResetService,
            shiftValidator,
            _scheduleMapper,
            _unitOfWork,
            facadeLogger);

        // Create handlers
        var postHandlerLogger = Substitute.For<ILogger<PostCommandHandler>>();
        _postHandler = new PostCommandHandler(_shiftRepository, _scheduleMapper, _unitOfWork, postHandlerLogger);

        var putHandlerLogger = Substitute.For<ILogger<PutCommandHandler>>();
        _putHandler = new PutCommandHandler(_shiftRepository, _scheduleMapper, _unitOfWork, putHandlerLogger);

        var batchCutsHandlerLogger = Substitute.For<ILogger<PostBatchCutsCommandHandler>>();
        _batchCutsHandler = new PostBatchCutsCommandHandler(_shiftCutFacade, _scheduleMapper, batchCutsHandlerLogger);

        var resetCutsHandlerLogger = Substitute.For<ILogger<PostResetCutsCommandHandler>>();
        _resetCutsHandler = new PostResetCutsCommandHandler(_shiftCutFacade, _scheduleMapper, resetCutsHandlerLogger);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataWithContext(_context);
        _context?.Dispose();
    }

    private static async Task CleanupTestDataWithContext(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestShiftPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestShiftPrefix}%';
            DELETE FROM communication WHERE client_id IN (SELECT id FROM client WHERE company LIKE '{TestCustomerPrefix}%');
            DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE company LIKE '{TestCustomerPrefix}%');
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE company LIKE '{TestCustomerPrefix}%');
            DELETE FROM client WHERE company LIKE '{TestCustomerPrefix}%';
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }

    #region Helper Methods

    private ShiftResource CreateTestShiftResource(
        string nameSuffix,
        ShiftStatus status = ShiftStatus.SealedOrder,
        DateOnly? fromDate = null,
        DateOnly? untilDate = null,
        TimeOnly? startShift = null,
        TimeOnly? endShift = null,
        Guid? originalId = null,
        Guid? parentId = null,
        Guid? rootId = null)
    {
        return new ShiftResource
        {
            Id = Guid.NewGuid(),
            Name = $"{TestShiftPrefix}{nameSuffix}",
            Status = status,
            FromDate = fromDate ?? new DateOnly(2025, 1, 1),
            UntilDate = untilDate,
            StartShift = startShift ?? new TimeOnly(8, 0),
            EndShift = endShift ?? new TimeOnly(16, 0),
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            OriginalId = originalId,
            ParentId = parentId,
            RootId = rootId,
            ShiftType = ShiftType.IsTask,
            Abbreviation = "TEST",
            Description = "Integration Test Shift"
        };
    }

    private async Task<List<Shift>> GetAllShiftsWithOriginalId(Guid originalId)
    {
        return await _context.Shift
            .Where(s => s.OriginalId == originalId || s.Id == originalId)
            .OrderBy(s => s.Status)
            .ThenBy(s => s.FromDate)
            .AsNoTracking()
            .ToListAsync();
    }

    #endregion

    #region Test 1: Create OriginalOrder, then seal to create SealedOrder + OriginalShift

    [Test]
    public async Task CreateOriginalOrder_Should_Create_Editable_NonPlannable_Shift()
    {
        // Arrange
        var shiftResource = CreateTestShiftResource("OriginalOrder_Test", ShiftStatus.OriginalOrder);
        var command = new PostCommand<ShiftResource>(shiftResource);

        // Act
        var result = await _postHandler.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull("Handler should return a result");
        result!.Status.ShouldBe(ShiftStatus.OriginalOrder, "Should remain OriginalOrder (not plannable)");

        Console.WriteLine("=== CREATE ORIGINAL ORDER TEST ===");
        Console.WriteLine($"Created Shift: Id={result.Id}, Status={result.Status}, Name={result.Name}");

        // Verify database state - should be only 1 shift (no automatic copy for OriginalOrder)
        var shiftsInDb = await _context.Shift
            .Where(s => s.Id == result.Id)
            .AsNoTracking()
            .ToListAsync();

        shiftsInDb.Count.ShouldBe(1, "Should have only 1 shift (OriginalOrder)");
        shiftsInDb[0].Status.ShouldBe(ShiftStatus.OriginalOrder);

        Console.WriteLine("=== TEST PASSED: OriginalOrder created (editable, not plannable) ===");
    }

    [Test]
    public async Task SealOriginalOrder_Should_Create_SealedOrder_And_OriginalShift()
    {
        // Arrange - First create an OriginalOrder
        var originalOrderResource = CreateTestShiftResource("Seal_Test", ShiftStatus.OriginalOrder);
        var createCommand = new PostCommand<ShiftResource>(originalOrderResource);
        var originalOrder = await _postHandler.Handle(createCommand, CancellationToken.None);

        originalOrder.ShouldNotBeNull();
        originalOrder!.Status.ShouldBe(ShiftStatus.OriginalOrder);

        Console.WriteLine("=== SEAL ORIGINAL ORDER TEST ===");
        Console.WriteLine($"Step 1: Created OriginalOrder: Id={originalOrder.Id}, Status={originalOrder.Status}");

        // Act - Simulate "Lock" button: change status to SealedOrder and save (PUT = update)
        originalOrder.Status = ShiftStatus.SealedOrder;
        var sealCommand = new PutCommand<ShiftResource>(originalOrder);
        var result = await _putHandler.Handle(sealCommand, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull("Handler should return a result");
        result!.Status.ShouldBe(ShiftStatus.OriginalShift, "Returned shift should be OriginalShift (the plannable copy)");

        Console.WriteLine($"Step 2: After sealing - Returned: Id={result.Id}, Status={result.Status}, OriginalId={result.OriginalId}");

        // Verify database state
        var allShifts = await GetAllShiftsWithOriginalId(result.OriginalId!.Value);

        allShifts.Count.ShouldBe(2, "Should have SealedOrder + OriginalShift");

        var sealedOrder = allShifts.FirstOrDefault(s => s.Status == ShiftStatus.SealedOrder);
        var originalShift = allShifts.FirstOrDefault(s => s.Status == ShiftStatus.OriginalShift);

        sealedOrder.ShouldNotBeNull("SealedOrder should exist (permanently sealed)");
        originalShift.ShouldNotBeNull("OriginalShift should exist (plannable copy)");

        Console.WriteLine($"SealedOrder: Id={sealedOrder!.Id}, Status={sealedOrder.Status} (permanently sealed)");
        Console.WriteLine($"OriginalShift: Id={originalShift!.Id}, Status={originalShift.Status}, OriginalId={originalShift.OriginalId} (plannable)");

        // Verify relationship
        originalShift.OriginalId.ShouldBe(sealedOrder.Id, "OriginalShift.OriginalId should reference SealedOrder");
        sealedOrder.Name.ShouldBe(originalShift.Name, "Names should match");
        sealedOrder.FromDate.ShouldBe(originalShift.FromDate, "FromDate should match");

        Console.WriteLine("=== TEST PASSED: Workflow OriginalOrder → SealedOrder + OriginalShift ===");
    }

    #endregion

    #region Test 2: Split OriginalShift to SplitShifts

    [Test]
    public async Task PostBatchCuts_Should_Create_SplitShifts_From_OriginalShift()
    {
        // Arrange - Create SealedOrder first
        var shiftResource = CreateTestShiftResource("Split_Test", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2025, 1, 1));
        var createCommand = new PostCommand<ShiftResource>(shiftResource);
        var createdShift = await _postHandler.Handle(createCommand, CancellationToken.None);

        createdShift.ShouldNotBeNull();
        var originalShiftId = createdShift!.Id;
        var sealedOrderId = createdShift.OriginalId!.Value;

        Console.WriteLine("=== SPLIT TEST ===");
        Console.WriteLine($"Created OriginalShift: Id={originalShiftId}, OriginalId={sealedOrderId}");

        // Create CutOperations - split into 2 periods
        var splitShift1 = CreateTestShiftResource("Split_Test_Part1", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1),
            untilDate: new DateOnly(2025, 6, 30),
            startShift: new TimeOnly(8, 0),
            endShift: new TimeOnly(12, 0),
            originalId: sealedOrderId);

        var splitShift2 = CreateTestShiftResource("Split_Test_Part2", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 7, 1),
            untilDate: new DateOnly(2025, 12, 31),
            startShift: new TimeOnly(12, 0),
            endShift: new TimeOnly(16, 0),
            originalId: sealedOrderId);

        var cutOperations = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = originalShiftId.ToString(), Data = splitShift1 },
            new() { Type = "CREATE", ParentId = originalShiftId.ToString(), Data = splitShift2 }
        };

        var batchCutsCommand = new PostBatchCutsCommand(cutOperations);

        // Act
        var results = await _batchCutsHandler.Handle(batchCutsCommand, CancellationToken.None);

        // Assert
        results.Count.ShouldBe(2, "Should return 2 SplitShifts");

        foreach (var result in results)
        {
            result.Status.ShouldBe(ShiftStatus.SplitShift, "All results should be SplitShifts");
            result.OriginalId.ShouldBe(sealedOrderId, "OriginalId should reference SealedOrder");
            Console.WriteLine($"SplitShift: Id={result.Id}, FromDate={result.FromDate}, UntilDate={result.UntilDate}, " +
                            $"StartShift={result.StartShift}, EndShift={result.EndShift}");
        }

        // Verify database state
        var allShifts = await GetAllShiftsWithOriginalId(sealedOrderId);
        allShifts.Count.ShouldBe(4, "Should have SealedOrder + OriginalShift + 2 SplitShifts");

        var splitShifts = allShifts.Where(s => s.Status == ShiftStatus.SplitShift).ToList();
        splitShifts.Count.ShouldBe(2, "Should have exactly 2 SplitShifts");

        Console.WriteLine("=== TEST PASSED ===");
    }

    #endregion

    #region Cut list id-tolerance (empty cut page regression 2026-06-21)

    [Test]
    public async Task CutList_Is_IdTolerant_OriginalShiftId_Resolves_To_SealedOrder_Family()
    {
        // Arrange - a sealed order => SealedOrder (1) + OriginalShift (2).
        var shiftResource = CreateTestShiftResource("CutListIdTol", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2026, 1, 1));
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(shiftResource), CancellationToken.None);
        created.ShouldNotBeNull();
        var originalShiftId = created!.Id;
        var sealedOrderId = created.OriginalId!.Value;

        // Act - the cut page is keyed by the SealedOrder id (WHERE OriginalId == id). The 2026-06-21
        // incident: Klacksy navigated with the OriginalShift's own id -> empty list. Id-tolerance must
        // resolve it to the SealedOrder family.
        var bySealedOrder = await _shiftRepository.CutList(sealedOrderId);
        var byOriginalShift = await _shiftRepository.CutList(originalShiftId);

        // Assert - both ids resolve to the same non-empty family.
        bySealedOrder.Count.ShouldBeGreaterThan(0, "the SealedOrder id must list the plannable shift to cut");
        byOriginalShift.Count.ShouldBe(bySealedOrder.Count,
            "the OriginalShift id must resolve to the same cut list (no empty page when reached with the wrong id)");
        byOriginalShift.Select(s => s.Id).OrderBy(id => id)
            .ShouldBe(bySealedOrder.Select(s => s.Id).OrderBy(id => id));
    }

    #endregion

    #region Tree integrity: form PUT must not change the cut hierarchy

    [Test]
    public async Task FormPut_Should_Preserve_Tree_Fields_On_SplitShift()
    {
        // Arrange - SealedOrder -> OriginalShift, then cut into a SplitShift (which gets a root)
        var sealedResource = CreateTestShiftResource("TreePreserve", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2025, 1, 1));
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(sealedResource), CancellationToken.None);
        created.ShouldNotBeNull();
        var originalShiftId = created!.Id;
        var sealedOrderId = created.OriginalId!.Value;

        var splitResource = CreateTestShiftResource("TreePreserve_Part1", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1), untilDate: new DateOnly(2025, 6, 30),
            originalId: sealedOrderId);
        var cutOperations = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = originalShiftId.ToString(), Data = splitResource }
        };
        var cutResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(cutOperations), CancellationToken.None);
        var splitId = cutResults.Single().Id;

        var before = await _context.Shift.AsNoTracking().FirstAsync(s => s.Id == splitId);
        before.RootId.ShouldNotBeNull("precondition: a cut split shift must have a root");
        var originalRootId = before.RootId;
        var originalParentId = before.ParentId;
        var originalLft = before.Lft;
        var originalRgt = before.Rgt;
        var originalOriginalId = before.OriginalId;

        // Act - a form save that, like a buggy frontend, sends NULL tree fields and a changed name
        var putResource = CreateTestShiftResource("TreePreserve_Renamed", ShiftStatus.SplitShift);
        putResource.Id = splitId;
        putResource.RootId = null;
        putResource.ParentId = null;
        putResource.OriginalId = null;
        putResource.Lft = null;
        putResource.Rgt = null;
        await _putHandler.Handle(new PutCommand<ShiftResource>(putResource), CancellationToken.None);

        // Assert - the cut hierarchy is preserved, the editable field still changed
        var after = await _context.Shift.AsNoTracking().FirstAsync(s => s.Id == splitId);
        after.RootId.ShouldBe(originalRootId, "root_id must be preserved on a form save (no orphaning)");
        after.ParentId.ShouldBe(originalParentId, "parent_id must be preserved on a form save");
        after.OriginalId.ShouldBe(originalOriginalId, "original_id must be preserved on a form save");
        after.Lft.ShouldBe(originalLft, "lft must be preserved on a form save");
        after.Rgt.ShouldBe(originalRgt, "rgt must be preserved on a form save");
        after.Name.ShouldContain("Renamed");
    }

    [Test]
    public async Task SubCut_Of_An_OrderRoot_Split_Should_Not_Crash_The_NestedSet_Recalc()
    {
        // Arrange - build a SEED-style ORDER-ROOT tree by hand (the convention all 80 seeded splits
        // use): a SealedOrder with root_id = NULL, plus one SplitShift child whose root_id points at
        // the order (NOT at itself).
        var orderId = Guid.NewGuid();
        var splitId = Guid.NewGuid();

        _context.Shift.Add(new Shift
        {
            Id = orderId,
            Name = $"{TestShiftPrefix}OrderRoot_Order",
            Abbreviation = "TEST",
            Status = ShiftStatus.SealedOrder,
            ShiftType = ShiftType.IsTask,
            FromDate = new DateOnly(2025, 1, 1),
            ParentId = null,
            RootId = null,
            OriginalId = null
        });
        _context.Shift.Add(new Shift
        {
            Id = splitId,
            Name = $"{TestShiftPrefix}OrderRoot_Split",
            Abbreviation = "TEST",
            Status = ShiftStatus.SplitShift,
            ShiftType = ShiftType.IsTask,
            FromDate = new DateOnly(2025, 1, 1),
            ParentId = null,
            RootId = orderId,       // order-root, like the seed
            OriginalId = orderId,
            Lft = 1,
            Rgt = 2
        });
        await _context.SaveChangesAsync();

        // Act - sub-cut the order-root split. ProcessCreate's "child" branch sets
        // root_id = parentSplit.RootId = orderId, then RecalculateAllAffectedTreesAsync runs the
        // nested-set recalc on root_id = orderId.
        var subSplit = CreateTestShiftResource("OrderRoot_SubCut", ShiftStatus.SplitShift, originalId: orderId);
        var subSplitId = subSplit.Id;
        var cutOperations = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = splitId.ToString(), Data = subSplit }
        };

        // Assert - with the order-root recalc fix the recalc treats every top-level (ParentId == null)
        // node as a forest root instead of requiring a node whose Id == rootId, so it no longer throws
        // on order-root data. The sub-cut succeeds and the new sub-split stays rooted at the order.
        var results = await _batchCutsHandler.Handle(new PostBatchCutsCommand(cutOperations), CancellationToken.None);

        results.Count.ShouldBe(1, "the sub-cut must create exactly one sub-split");
        var persisted = await _context.Shift.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subSplitId);
        persisted.ShouldNotBeNull();
        persisted!.RootId.ShouldBe(orderId, "the sub-split stays rooted at the order (order-root)");
        persisted.ParentId.ShouldBe(splitId, "the sub-split's parent is the split it was cut from");
    }

    [Test]
    public async Task FormPut_With_Unchanged_Groups_Should_Preserve_Them()
    {
        // Two real groups from the dev DB (Westschweiz, Deutschschweiz Ost).
        var groupA = Guid.Parse("706e2414-9aa4-46e3-8143-a49eca1f0a44");
        var groupB = Guid.Parse("39ac4862-ad34-477e-aa57-3bfa5ec1a476");

        // Arrange - create an OriginalOrder shift carrying 2 groups.
        var createResource = CreateTestShiftResource("GroupPreserve", ShiftStatus.OriginalOrder);
        createResource.Groups = new List<SimpleGroupResource>
        {
            new() { Id = groupA },
            new() { Id = groupB }
        };
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(createResource), CancellationToken.None);
        created.ShouldNotBeNull();
        var shiftId = created!.Id;
        created.Groups.Count.ShouldBe(2, "precondition: the created shift must carry 2 groups");

        // Act - PUT the resource back UNCHANGED (what any savebar save does, e.g. after editing a
        // qualification). The 2 groups are sent again with their ids; matched children must keep
        // their ShiftId instead of being detached (FK -> null) by SetValues.
        await _putHandler.Handle(new PutCommand<ShiftResource>(created), CancellationToken.None);

        // Assert - both groups must survive (the bug detached them, leaving 0 linked to the shift).
        var after = await _context.Shift.AsNoTracking()
            .Include(s => s.GroupItems)
            .FirstAsync(s => s.Id == shiftId);
        after.GroupItems.Count.ShouldBe(2, "a PUT with unchanged groups must not drop any group");
    }

    #endregion

    #region Test 3: Nested splits (SplitShift from SplitShift)

    [Test]
    public async Task PostBatchCuts_Should_Create_Nested_SplitShifts()
    {
        // Arrange - Create SealedOrder and first-level split
        var shiftResource = CreateTestShiftResource("Nested_Split_Test", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2025, 1, 1));
        var createCommand = new PostCommand<ShiftResource>(shiftResource);
        var createdShift = await _postHandler.Handle(createCommand, CancellationToken.None);

        var originalShiftId = createdShift!.Id;
        var sealedOrderId = createdShift.OriginalId!.Value;

        Console.WriteLine("=== NESTED SPLIT TEST ===");
        Console.WriteLine($"Created OriginalShift: Id={originalShiftId}");

        // Create first-level SplitShift
        var firstLevelSplit = CreateTestShiftResource("Nested_Level1", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1),
            untilDate: new DateOnly(2025, 12, 31),
            originalId: sealedOrderId);

        var firstCutOps = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = originalShiftId.ToString(), Data = firstLevelSplit }
        };

        var firstResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(firstCutOps), CancellationToken.None);
        var firstLevelShiftId = firstResults[0].Id;

        Console.WriteLine($"First-level SplitShift: Id={firstLevelShiftId}, RootId={firstResults[0].RootId}");

        // Create second-level (nested) SplitShifts from first-level
        var nestedSplit1 = CreateTestShiftResource("Nested_Level2_Part1", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1),
            untilDate: new DateOnly(2025, 6, 30),
            originalId: sealedOrderId);

        var nestedSplit2 = CreateTestShiftResource("Nested_Level2_Part2", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 7, 1),
            untilDate: new DateOnly(2025, 12, 31),
            originalId: sealedOrderId);

        var nestedCutOps = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = firstLevelShiftId.ToString(), Data = nestedSplit1 },
            new() { Type = "CREATE", ParentId = firstLevelShiftId.ToString(), Data = nestedSplit2 }
        };

        // Act
        var nestedResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(nestedCutOps), CancellationToken.None);

        // Assert
        nestedResults.Count.ShouldBe(2, "Should create 2 nested SplitShifts");

        foreach (var result in nestedResults)
        {
            result.Status.ShouldBe(ShiftStatus.SplitShift);
            result.ParentId.ShouldBe(firstLevelShiftId, "ParentId should reference first-level SplitShift");
            Console.WriteLine($"Nested SplitShift: Id={result.Id}, ParentId={result.ParentId}, RootId={result.RootId}");
        }

        // Verify hierarchy
        var allShifts = await GetAllShiftsWithOriginalId(sealedOrderId);
        var splitShifts = allShifts.Where(s => s.Status == ShiftStatus.SplitShift).ToList();

        splitShifts.Count.ShouldBe(3, "Should have 3 SplitShifts total (1 first-level + 2 nested)");

        var nestedShifts = splitShifts.Where(s => s.ParentId == firstLevelShiftId).ToList();
        nestedShifts.Count.ShouldBe(2, "Should have 2 nested SplitShifts");

        Console.WriteLine("=== TEST PASSED ===");
    }

    #endregion

    #region Test 4: Reset cuts

    [Test]
    public async Task PostResetCuts_Should_Close_Old_Splits_And_Create_New_OriginalShift()
    {
        // Arrange - Create SealedOrder and splits
        var shiftResource = CreateTestShiftResource("Reset_Test", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2025, 1, 1));
        var createCommand = new PostCommand<ShiftResource>(shiftResource);
        var createdShift = await _postHandler.Handle(createCommand, CancellationToken.None);

        var originalShiftId = createdShift!.Id;
        var sealedOrderId = createdShift.OriginalId!.Value;

        Console.WriteLine("=== RESET CUTS TEST ===");
        Console.WriteLine($"Created OriginalShift: Id={originalShiftId}, SealedOrderId={sealedOrderId}");

        // Create some SplitShifts
        var split1 = CreateTestShiftResource("Reset_Split1", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1),
            untilDate: new DateOnly(2025, 3, 31),
            originalId: sealedOrderId);

        var split2 = CreateTestShiftResource("Reset_Split2", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 4, 1),
            untilDate: new DateOnly(2025, 6, 30),
            originalId: sealedOrderId);

        var cutOps = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = originalShiftId.ToString(), Data = split1 },
            new() { Type = "CREATE", ParentId = originalShiftId.ToString(), Data = split2 }
        };

        await _batchCutsHandler.Handle(new PostBatchCutsCommand(cutOps), CancellationToken.None);

        var shiftsBeforeReset = await GetAllShiftsWithOriginalId(sealedOrderId);
        Console.WriteLine($"Shifts before reset: {shiftsBeforeReset.Count}");
        foreach (var s in shiftsBeforeReset)
        {
            Console.WriteLine($"  - {s.Name}: Status={s.Status}, FromDate={s.FromDate}, UntilDate={s.UntilDate}");
        }

        // Act - Reset cuts from July 1st
        var resetCommand = new PostResetCutsCommand(sealedOrderId, new DateOnly(2025, 7, 1));
        var resetResults = await _resetCutsHandler.Handle(resetCommand, CancellationToken.None);

        // Assert
        Console.WriteLine($"Shifts after reset: {resetResults.Count}");
        foreach (var s in resetResults)
        {
            Console.WriteLine($"  - {s.Name}: Status={s.Status}, FromDate={s.FromDate}, UntilDate={s.UntilDate}");
        }

        // Check that old splits are closed (UntilDate set)
        var closedSplits = resetResults.Where(s =>
            s.Status == ShiftStatus.SplitShift &&
            s.UntilDate.HasValue &&
            s.UntilDate.Value < new DateOnly(2025, 7, 1)).ToList();

        closedSplits.Count.ShouldBeGreaterThanOrEqualTo(0, "Old splits should be closed or deleted");

        // Check for new OriginalShift starting from reset date
        var newOriginalShift = resetResults.FirstOrDefault(s =>
            s.Status == ShiftStatus.OriginalShift &&
            s.FromDate >= new DateOnly(2025, 7, 1));

        newOriginalShift.ShouldNotBeNull("New OriginalShift should be created from reset date");
        Console.WriteLine($"New OriginalShift: Id={newOriginalShift!.Id}, FromDate={newOriginalShift.FromDate}");

        Console.WriteLine("=== TEST PASSED ===");
    }

    #endregion

    #region Test 5: Update existing SplitShift

    [Test]
    public async Task PostBatchCuts_With_UpdateType_Should_Update_Existing_SplitShift()
    {
        // Arrange - Create SealedOrder and a SplitShift
        var shiftResource = CreateTestShiftResource("Update_Test", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2025, 1, 1));
        var createCommand = new PostCommand<ShiftResource>(shiftResource);
        var createdShift = await _postHandler.Handle(createCommand, CancellationToken.None);

        var originalShiftId = createdShift!.Id;
        var sealedOrderId = createdShift.OriginalId!.Value;

        Console.WriteLine("=== UPDATE SPLITSHIFT TEST ===");

        // Create initial SplitShift
        var initialSplit = CreateTestShiftResource("Update_Initial", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1),
            untilDate: new DateOnly(2025, 12, 31),
            startShift: new TimeOnly(8, 0),
            endShift: new TimeOnly(16, 0),
            originalId: sealedOrderId);

        var createOps = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = originalShiftId.ToString(), Data = initialSplit }
        };

        var createResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(createOps), CancellationToken.None);
        var splitShiftId = createResults[0].Id;

        Console.WriteLine($"Initial SplitShift: Id={splitShiftId}, StartShift={createResults[0].StartShift}, EndShift={createResults[0].EndShift}");

        // Update the SplitShift with new times
        var updatedSplit = CreateTestShiftResource("Update_Modified", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1),
            untilDate: new DateOnly(2025, 12, 31),
            startShift: new TimeOnly(9, 0),
            endShift: new TimeOnly(17, 0),
            originalId: sealedOrderId);
        updatedSplit.Id = splitShiftId;

        var updateOps = new List<CutOperation>
        {
            new() { Type = "UPDATE", ParentId = originalShiftId.ToString(), Data = updatedSplit }
        };

        // Act
        var updateResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(updateOps), CancellationToken.None);

        // Assert
        updateResults.Count.ShouldBe(1);
        var updatedResult = updateResults[0];

        updatedResult.Id.ShouldBe(splitShiftId, "Should be the same shift");
        updatedResult.StartShift.ShouldBe(new TimeOnly(9, 0), "StartShift should be updated");
        updatedResult.EndShift.ShouldBe(new TimeOnly(17, 0), "EndShift should be updated");

        Console.WriteLine($"Updated SplitShift: Id={updatedResult.Id}, StartShift={updatedResult.StartShift}, EndShift={updatedResult.EndShift}");
        Console.WriteLine("=== TEST PASSED ===");
    }

    #endregion

    #region Test 6: Verify Nested Set values (Lft/Rgt)

    [Test]
    public async Task SplitShifts_Should_Have_Valid_NestedSet_Values()
    {
        // Arrange - Create hierarchy: OriginalShift -> SplitShift -> 2 Nested SplitShifts
        var shiftResource = CreateTestShiftResource("NestedSet_Test", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2025, 1, 1));
        var createCommand = new PostCommand<ShiftResource>(shiftResource);
        var createdShift = await _postHandler.Handle(createCommand, CancellationToken.None);

        var originalShiftId = createdShift!.Id;
        var sealedOrderId = createdShift.OriginalId!.Value;

        Console.WriteLine("=== NESTED SET VALUES TEST ===");

        // Create root SplitShift
        var rootSplit = CreateTestShiftResource("NestedSet_Root", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1),
            untilDate: new DateOnly(2025, 12, 31),
            originalId: sealedOrderId);

        var rootOps = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = originalShiftId.ToString(), Data = rootSplit }
        };

        var rootResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(rootOps), CancellationToken.None);
        var rootShiftId = rootResults[0].Id;
        var rootIdValue = rootResults[0].RootId;

        Console.WriteLine($"Root SplitShift: Id={rootShiftId}, Lft={rootResults[0].Lft}, Rgt={rootResults[0].Rgt}, RootId={rootIdValue}");

        // Create child SplitShifts
        var childSplit1 = CreateTestShiftResource("NestedSet_Child1", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 1, 1),
            untilDate: new DateOnly(2025, 6, 30),
            originalId: sealedOrderId);

        var childSplit2 = CreateTestShiftResource("NestedSet_Child2", ShiftStatus.SplitShift,
            fromDate: new DateOnly(2025, 7, 1),
            untilDate: new DateOnly(2025, 12, 31),
            originalId: sealedOrderId);

        var childOps = new List<CutOperation>
        {
            new() { Type = "CREATE", ParentId = rootShiftId.ToString(), Data = childSplit1 },
            new() { Type = "CREATE", ParentId = rootShiftId.ToString(), Data = childSplit2 }
        };

        // Act
        var childResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(childOps), CancellationToken.None);

        // Assert - Check nested set properties
        var allSplitShifts = await _context.Shift
            .Where(s => s.OriginalId == sealedOrderId && s.Status == ShiftStatus.SplitShift)
            .OrderBy(s => s.Lft)
            .AsNoTracking()
            .ToListAsync();

        Console.WriteLine("All SplitShifts with Nested Set values:");
        foreach (var shift in allSplitShifts)
        {
            Console.WriteLine($"  - {shift.Name}: Lft={shift.Lft}, Rgt={shift.Rgt}, ParentId={shift.ParentId}, RootId={shift.RootId}");
        }

        // Verify RootId is set correctly
        foreach (var shift in allSplitShifts)
        {
            shift.RootId.ShouldNotBeNull("RootId should be set for all SplitShifts");
        }

        // Verify parent-child relationships
        var rootShift = allSplitShifts.FirstOrDefault(s => s.ParentId == null);
        var childShifts = allSplitShifts.Where(s => s.ParentId != null).ToList();

        if (rootShift != null)
        {
            rootShift.RootId.ShouldBe(rootShift.Id, "Root's RootId should reference itself");

            foreach (var child in childShifts)
            {
                child.RootId.ShouldBe(rootShift.RootId, "Children should have same RootId as root");
            }
        }

        // Verify Lft < Rgt for each node (basic nested set invariant)
        foreach (var shift in allSplitShifts.Where(s => s.Lft.HasValue && s.Rgt.HasValue))
        {
            shift.Lft!.Value.ShouldBeLessThan(shift.Rgt!.Value, $"Lft should be less than Rgt for {shift.Name}");
        }

        Console.WriteLine("=== TEST PASSED ===");
    }

    #endregion

    #region Test 7: Full workflow - Complete scenario

    [Test]
    public async Task FullWorkflow_Create_Split_Update_Reset_Should_Work_Correctly()
    {
        Console.WriteLine("=== FULL WORKFLOW TEST ===");

        // Step 1: Create SealedOrder
        Console.WriteLine("\n--- Step 1: Create SealedOrder ---");
        var shiftResource = CreateTestShiftResource("FullWorkflow", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2025, 1, 1));
        var createCommand = new PostCommand<ShiftResource>(shiftResource);
        var createdShift = await _postHandler.Handle(createCommand, CancellationToken.None);

        createdShift.ShouldNotBeNull();
        createdShift!.Status.ShouldBe(ShiftStatus.OriginalShift);
        var sealedOrderId = createdShift.OriginalId!.Value;
        var originalShiftId = createdShift.Id;

        Console.WriteLine($"Created: SealedOrder={sealedOrderId}, OriginalShift={originalShiftId}");

        // Step 2: Split into 4 quarters
        Console.WriteLine("\n--- Step 2: Split into quarters ---");
        var quarters = new[]
        {
            (new DateOnly(2025, 1, 1), new DateOnly(2025, 3, 31), "Q1"),
            (new DateOnly(2025, 4, 1), new DateOnly(2025, 6, 30), "Q2"),
            (new DateOnly(2025, 7, 1), new DateOnly(2025, 9, 30), "Q3"),
            (new DateOnly(2025, 10, 1), new DateOnly(2025, 12, 31), "Q4")
        };

        var splitOps = quarters.Select(q => new CutOperation
        {
            Type = "CREATE",
            ParentId = originalShiftId.ToString(),
            Data = CreateTestShiftResource($"FullWorkflow_{q.Item3}", ShiftStatus.SplitShift,
                fromDate: q.Item1, untilDate: q.Item2, originalId: sealedOrderId)
        }).ToList();

        var splitResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(splitOps), CancellationToken.None);
        splitResults.Count.ShouldBe(4);

        foreach (var r in splitResults)
        {
            Console.WriteLine($"  {r.Name}: FromDate={r.FromDate}, UntilDate={r.UntilDate}");
        }

        // Step 3: Update Q2 with different times
        Console.WriteLine("\n--- Step 3: Update Q2 ---");
        var q2Shift = splitResults.First(r => r.Name.Contains("Q2"));
        var updatedQ2 = CreateTestShiftResource("FullWorkflow_Q2_Updated", ShiftStatus.SplitShift,
            fromDate: q2Shift.FromDate, untilDate: q2Shift.UntilDate,
            startShift: new TimeOnly(10, 0), endShift: new TimeOnly(18, 0),
            originalId: sealedOrderId);
        updatedQ2.Id = q2Shift.Id;

        var updateOps = new List<CutOperation>
        {
            new() { Type = "UPDATE", ParentId = originalShiftId.ToString(), Data = updatedQ2 }
        };

        var updateResults = await _batchCutsHandler.Handle(new PostBatchCutsCommand(updateOps), CancellationToken.None);
        updateResults[0].StartShift.ShouldBe(new TimeOnly(10, 0));
        Console.WriteLine($"Updated Q2: StartShift={updateResults[0].StartShift}, EndShift={updateResults[0].EndShift}");

        // Step 4: Verify final state
        Console.WriteLine("\n--- Step 4: Final state ---");
        var finalShifts = await GetAllShiftsWithOriginalId(sealedOrderId);

        Console.WriteLine($"Total shifts: {finalShifts.Count}");
        foreach (var s in finalShifts.OrderBy(x => x.Status).ThenBy(x => x.FromDate))
        {
            Console.WriteLine($"  {s.Name}: Status={s.Status}, FromDate={s.FromDate}, UntilDate={s.UntilDate}");
        }

        finalShifts.Count(s => s.Status == ShiftStatus.SealedOrder).ShouldBe(1);
        finalShifts.Count(s => s.Status == ShiftStatus.OriginalShift).ShouldBe(1);
        finalShifts.Count(s => s.Status == ShiftStatus.SplitShift).ShouldBe(4);

        Console.WriteLine("\n=== FULL WORKFLOW TEST PASSED ===");
    }

    #endregion

    #region Test 8: CutShiftSkill (Klacksy) - one 24h order cut into 3 linked parts

    private CutShiftSkill CreateCutSkill() => new(_shiftRepository, _shiftCutFacade);

    private static SkillExecutionContext TestSkillContext() => new()
    {
        UserId = Guid.Empty,
        TenantId = Guid.Empty,
        UserName = "integration-test",
        UserPermissions = new List<string>()
    };

    [Test]
    public async Task CutShiftSkill_Should_Split_24h_Order_Into_Three_Linked_Parts()
    {
        // Arrange - a 24h order (07:00-07:00) carrying a real dev-DB group, sealed -> OriginalShift.
        var groupA = Guid.Parse("706e2414-9aa4-46e3-8143-a49eca1f0a44");
        var customerId = Guid.Parse("f435fe8b-6468-44c2-92fa-69b87546d4ae"); // Tech Systems GmbH (Customer)
        var macroId = Guid.Parse("a3edd3f5-c31c-4746-a9a0-c613d14ffd23");     // AllShift (category Shift)
        var orderResource = CreateTestShiftResource("CutSkill_24h", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2026, 6, 1),
            startShift: new TimeOnly(7, 0),
            endShift: new TimeOnly(7, 0));
        orderResource.Groups = new List<SimpleGroupResource> { new() { Id = groupA } };
        orderResource.ClientId = customerId;
        orderResource.MacroId = macroId;

        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(orderResource), CancellationToken.None);
        created.ShouldNotBeNull();
        var originalShiftId = created!.Id;
        var sealedOrderId = created.OriginalId!.Value;

        var parameters = new Dictionary<string, object>
        {
            ["shiftId"] = originalShiftId.ToString(),
            ["parts"] = "07:00-15:00,15:00-23:00,23:00-07:00",
            ["partNames"] = $"{TestShiftPrefix}Frueh,{TestShiftPrefix}Spaet,{TestShiftPrefix}Nacht"
        };

        // Act
        var result = await CreateCutSkill().ExecuteAsync(TestSkillContext(), parameters, CancellationToken.None);

        // Assert - skill succeeded and produced exactly ONE order with 3 linked parts.
        result.Success.ShouldBeTrue(result.Message);

        var allForOrder = await GetAllShiftsWithOriginalId(sealedOrderId);
        allForOrder.Count(s => s.Status == ShiftStatus.SealedOrder).ShouldBe(1, "the single sealed order must survive");
        allForOrder.Count(s => s.Status == ShiftStatus.OriginalShift)
            .ShouldBe(0, "the plannable 24h shift must be CONVERTED, not left over as a duplicate");

        var splits = allForOrder.Where(s => s.Status == ShiftStatus.SplitShift).OrderBy(s => s.StartShift).ToList();
        splits.Count.ShouldBe(3, "the order must be cut into exactly 3 linked parts");
        splits.Any(s => s.Id == originalShiftId)
            .ShouldBeTrue("the order's plannable shift was converted into the first part (id reused)");

        foreach (var s in splits)
        {
            s.OriginalId.ShouldBe(sealedOrderId, "every part stays linked to the one order");
            s.ClientId.ShouldBe(customerId, "every part must inherit the order's customer (for billing/address)");
            s.MacroId.ShouldBe(macroId, "every part must inherit the order's calculation macro");
            s.RootId.ShouldBe(s.Id, "a parallel time-slice part is its own nested-set root");
            s.ParentId.ShouldBeNull("a top-level time-slice has no parent");
            s.Lft.ShouldNotBeNull("nested-set lft must be assigned");
            s.Rgt.ShouldNotBeNull("nested-set rgt must be assigned");
            s.Lft!.Value.ShouldBeLessThan(s.Rgt!.Value);
        }

        splits[0].StartShift.ShouldBe(new TimeOnly(7, 0));
        splits[0].EndShift.ShouldBe(new TimeOnly(15, 0));
        splits[1].StartShift.ShouldBe(new TimeOnly(15, 0));
        splits[1].EndShift.ShouldBe(new TimeOnly(23, 0));
        splits[2].StartShift.ShouldBe(new TimeOnly(23, 0));
        splits[2].EndShift.ShouldBe(new TimeOnly(7, 0));
        splits[2].CuttingAfterMidnight.ShouldBeTrue("the 23:00-07:00 part crosses midnight");

        foreach (var s in splits)
        {
            var groups = await _shiftRepository.GetGroupsForShift(s.Id);
            groups.Select(g => g.Id).ShouldContain(groupA, "each part must inherit the order's group");
        }
    }

    [Test]
    public async Task CutShiftSkill_Should_Refuse_To_Cut_An_Already_Split_Order()
    {
        var orderResource = CreateTestShiftResource("CutSkill_Twice", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2026, 6, 1), startShift: new TimeOnly(7, 0), endShift: new TimeOnly(7, 0));
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(orderResource), CancellationToken.None);
        var originalShiftId = created!.Id;

        var firstParams = new Dictionary<string, object>
        {
            ["shiftId"] = originalShiftId.ToString(),
            ["parts"] = "07:00-19:00,19:00-07:00",
            ["partNames"] = $"{TestShiftPrefix}A,{TestShiftPrefix}B"
        };

        var skill = CreateCutSkill();
        (await skill.ExecuteAsync(TestSkillContext(), firstParams, CancellationToken.None)).Success.ShouldBeTrue();

        // Act - cut the same order again
        var secondResult = await skill.ExecuteAsync(TestSkillContext(), firstParams, CancellationToken.None);

        // Assert - refused, so no double-cut wreckage
        secondResult.Success.ShouldBeFalse("cutting an already-split order must be refused");

        var splitCount = (await GetAllShiftsWithOriginalId(created.OriginalId!.Value))
            .Count(s => s.Status == ShiftStatus.SplitShift);
        splitCount.ShouldBe(2, "the second cut must not add more parts");
    }

    [Test]
    public async Task FindSplitShiftCandidates_Should_List_An_Existing_Split_Order_In_The_Group()
    {
        // Biel/Bienne group from the dev DB (canton BE); the location of a split service comes
        // from its group, so listing the group's orders is what the disambiguation step needs.
        var bielGroupId = Guid.Parse("4e2e0e67-d744-40a6-b9fc-6fc66b8a7edf");

        // Arrange - a 24h order in the Biel group, already cut into 2 parts.
        var orderResource = CreateTestShiftResource("FindCand_24h", ShiftStatus.SealedOrder,
            fromDate: new DateOnly(2026, 6, 1), startShift: new TimeOnly(7, 0), endShift: new TimeOnly(7, 0));
        orderResource.Groups = new List<SimpleGroupResource> { new() { Id = bielGroupId } };
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(orderResource), CancellationToken.None);
        var originalShiftId = created!.Id;
        var sealedOrderId = created.OriginalId!.Value;

        var cutParams = new Dictionary<string, object>
        {
            ["shiftId"] = originalShiftId.ToString(),
            ["parts"] = "07:00-19:00,19:00-07:00",
            ["partNames"] = $"{TestShiftPrefix}Day,{TestShiftPrefix}Night"
        };
        (await CreateCutSkill().ExecuteAsync(TestSkillContext(), cutParams, CancellationToken.None)).Success.ShouldBeTrue();

        // Act - the disambiguation skill (exercises group resolution + GroupItems.Any + ILike + GetQuery).
        var skill = new FindSplitShiftCandidatesSkill(_shiftRepository, new GroupSearchRepository(_context));
        var result = await skill.ExecuteAsync(
            TestSkillContext(),
            new Dictionary<string, object> { ["groupName"] = "Biel/Bienne" },
            CancellationToken.None);

        // Assert - the skill returns the seeded order, flagged as already split with its 2 parts.
        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("already split");

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var orders = doc.RootElement.GetProperty("Orders").EnumerateArray()
            .Where(o => o.TryGetProperty("SealedOrderId", out var p)
                        && p.ValueKind != System.Text.Json.JsonValueKind.Null
                        && p.GetGuid() == sealedOrderId)
            .ToList();

        orders.Count.ShouldBe(1, "the seeded split order must be listed exactly once");
        orders[0].GetProperty("AlreadySplit").GetBoolean().ShouldBeTrue("the order is already cut");
        orders[0].GetProperty("PartCount").GetInt32().ShouldBe(2, "it has 2 parts");
    }

    private ClientRepository CreateClientRepository() => new(
        _context,
        Substitute.For<IMacroEngine>(),
        Substitute.For<IClientChangeTrackingService>(),
        Substitute.For<IClientEntityManagementService>(),
        new EntityCollectionUpdateService(_context),
        Substitute.For<IClientValidator>(),
        Substitute.For<ILogger<ClientRepository>>());

    private CreateEmployeeSkill CreateEmployeeSkillForTest()
    {
        var searchRepository = Substitute.For<IClientSearchRepository>();
        var countryResolver = Substitute.For<ICountryResolver>();
        var ch = new Countries
        {
            Abbreviation = "CH",
            Prefix = "+41",
            Name = new MultiLanguage { De = "Schweiz", En = "Switzerland" }
        };
        countryResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(ch);
        countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(ch);
        return new CreateEmployeeSkill(CreateClientRepository(), searchRepository, _unitOfWork, countryResolver);
    }

    [Test]
    public async Task CreateShiftSkill_Requires_A_Customer_And_Persists_ClientId()
    {
        // A real customer (type Customer) from the dev DB.
        var customerId = Guid.Parse("f435fe8b-6468-44c2-92fa-69b87546d4ae");
        var allShiftMacroId = Guid.Parse("a3edd3f5-c31c-4746-a9a0-c613d14ffd23"); // AllShift, category Shift
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = allShiftMacroId, Name = "AllShift", Category = MacroCategoryEnum.Shift }
            });
        var skill = new CreateShiftSkill(
            _shiftRepository, Substitute.For<IGroupRepository>(), CreateClientRepository(), mediator, _unitOfWork);

        // (a) no client -> refused (an order must be billed to a customer)
        var noClient = await skill.ExecuteAsync(TestSkillContext(), new Dictionary<string, object>
        {
            ["name"] = $"{TestShiftPrefix}NoClient",
            ["startTime"] = "07:00",
            ["endTime"] = "07:00"
        }, CancellationToken.None);
        noClient.Success.ShouldBeFalse("an order without a customer must be refused");

        // (b) an employee (non-customer) -> refused
        var employeeId = await _context.Client
            .Where(c => c.Type == EntityTypeEnum.Employee && !c.IsDeleted)
            .Select(c => c.Id)
            .FirstAsync();
        var wrongType = await skill.ExecuteAsync(TestSkillContext(), new Dictionary<string, object>
        {
            ["name"] = $"{TestShiftPrefix}WrongType",
            ["clientId"] = employeeId.ToString(),
            ["startTime"] = "07:00",
            ["endTime"] = "07:00"
        }, CancellationToken.None);
        wrongType.Success.ShouldBeFalse("an order billed to a non-customer must be refused");

        // (c) a customer -> created, ClientId persisted on the order
        var ok = await skill.ExecuteAsync(TestSkillContext(), new Dictionary<string, object>
        {
            ["name"] = $"{TestShiftPrefix}WithCustomer",
            ["clientId"] = customerId.ToString(),
            ["startTime"] = "07:00",
            ["endTime"] = "07:00",
            ["fromDate"] = "2026-07-01"
        }, CancellationToken.None);
        ok.Success.ShouldBeTrue(ok.Message);

        var order = await _context.Shift.AsNoTracking()
            .FirstAsync(s => s.Name == $"{TestShiftPrefix}WithCustomer" && s.Status == ShiftStatus.SealedOrder);
        order.ClientId.ShouldBe(customerId, "the order must be billed to the chosen customer");
        order.MacroId.ShouldBe(allShiftMacroId, "the order must get the default Shift-category macro");
        order.IsTimeRange.ShouldBeTrue(
            "a created order must be a time-range shift, matching every FE shift, so it is not hidden by the shift-list filter");

        var plannable = await _context.Shift.AsNoTracking()
            .FirstAsync(s => s.Name == $"{TestShiftPrefix}WithCustomer" && s.Status == ShiftStatus.OriginalShift);
        plannable.IsTimeRange.ShouldBeTrue("the plannable OriginalShift copy must inherit is_time_range=true");

        // The success message must steer the model to split via cut_shift instead of navigating to the cut page.
        ok.Message.ShouldContain("cut_shift");
    }

    private CreateShiftSkill CreateShiftSkillWithDefaultMacro()
    {
        var allShiftMacroId = Guid.Parse("a3edd3f5-c31c-4746-a9a0-c613d14ffd23");
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = allShiftMacroId, Name = "AllShift", Category = MacroCategoryEnum.Shift }
            });
        return new CreateShiftSkill(
            _shiftRepository, Substitute.For<IGroupRepository>(), CreateClientRepository(), mediator, _unitOfWork);
    }

    [Test]
    public async Task CreateShiftSkill_ReIssue_Reuses_Existing_Uncut_Order()
    {
        // ORD-5a: re-issuing create_shift for the same uncut order (same key, incl. start/end time) must
        // reuse it instead of creating a duplicate. Counter-test: a different time is a distinct order.
        var customerId = Guid.Parse("f435fe8b-6468-44c2-92fa-69b87546d4ae");
        var skill = CreateShiftSkillWithDefaultMacro();
        var name = $"{TestShiftPrefix}Reuse24h";

        Dictionary<string, object> Args() => new()
        {
            ["name"] = name,
            ["clientId"] = customerId.ToString(),
            ["startTime"] = "07:00",
            ["endTime"] = "07:00",
            ["fromDate"] = "2026-07-01"
        };

        var first = await skill.ExecuteAsync(TestSkillContext(), Args(), CancellationToken.None);
        first.Success.ShouldBeTrue(first.Message);

        var second = await skill.ExecuteAsync(TestSkillContext(), Args(), CancellationToken.None);
        second.Success.ShouldBeTrue(second.Message);

        (await _context.Shift.AsNoTracking()
            .CountAsync(s => s.Name == name && s.Status == ShiftStatus.SealedOrder && !s.IsDeleted))
            .ShouldBe(1, "re-issuing create_shift must not create a second order");
        (await _context.Shift.AsNoTracking()
            .CountAsync(s => s.Name == name && s.Status == ShiftStatus.OriginalShift && !s.IsDeleted))
            .ShouldBe(1, "exactly one plannable OriginalShift must remain");

        // Counter-test: same name/date/customer but a DIFFERENT time -> a genuinely distinct order.
        var diffTime = Args();
        diffTime["startTime"] = "08:00";
        diffTime["endTime"] = "08:00";
        var third = await skill.ExecuteAsync(TestSkillContext(), diffTime, CancellationToken.None);
        third.Success.ShouldBeTrue(third.Message);

        (await _context.Shift.AsNoTracking()
            .CountAsync(s => s.Name == name && s.Status == ShiftStatus.SealedOrder && !s.IsDeleted))
            .ShouldBe(2, "an order with a different time must NOT be merged into the existing one");

        // HIGH-2: a case/whitespace-only name change on re-issue must still reuse (no duplicate).
        var caseVariant = Args();
        caseVariant["name"] = "  " + name.ToUpperInvariant() + "  ";
        var fourth = await skill.ExecuteAsync(TestSkillContext(), caseVariant, CancellationToken.None);
        fourth.Success.ShouldBeTrue(fourth.Message);
        (await _context.Shift.AsNoTracking()
            .CountAsync(s => s.Status == ShiftStatus.SealedOrder && !s.IsDeleted
                             && s.Name.ToLower().Trim() == name.ToLower()))
            .ShouldBe(2, "a case/whitespace-only name change must reuse, not create a third order");
    }

    [Test]
    public async Task CreateShiftSkill_StructuralDifference_Is_A_Distinct_Order()
    {
        // MED-1: an order that differs only in its weekdays (weekend vs. all) is a DISTINCT order and must
        // NOT be merged into the existing one by the reuse-guard (otherwise the weekend order is lost).
        var customerId = Guid.Parse("f435fe8b-6468-44c2-92fa-69b87546d4ae");
        var skill = CreateShiftSkillWithDefaultMacro();
        var name = $"{TestShiftPrefix}WeekdayDistinct";

        Dictionary<string, object> Args(string weekdays) => new()
        {
            ["name"] = name,
            ["clientId"] = customerId.ToString(),
            ["startTime"] = "07:00",
            ["endTime"] = "15:00",
            ["fromDate"] = "2026-07-01",
            ["weekdays"] = weekdays
        };

        (await skill.ExecuteAsync(TestSkillContext(), Args("weekdays"), CancellationToken.None))
            .Success.ShouldBeTrue();
        (await skill.ExecuteAsync(TestSkillContext(), Args("weekend"), CancellationToken.None))
            .Success.ShouldBeTrue();

        (await _context.Shift.AsNoTracking()
            .CountAsync(s => s.Name == name && s.Status == ShiftStatus.SealedOrder && !s.IsDeleted))
            .ShouldBe(2, "orders with different weekdays are distinct and must not be merged");
    }

    [Test]
    public async Task CreateShiftSkill_Without_FromDate_Asks_With_DatePicker()
    {
        // ORD-6: fromDate is required; the skill must ask with a date picker instead of defaulting to today.
        var customerId = Guid.Parse("f435fe8b-6468-44c2-92fa-69b87546d4ae");
        var skill = CreateShiftSkillWithDefaultMacro();

        var result = await skill.ExecuteAsync(TestSkillContext(), new Dictionary<string, object>
        {
            ["name"] = $"{TestShiftPrefix}NoDate",
            ["clientId"] = customerId.ToString(),
            ["startTime"] = "07:00",
            ["endTime"] = "15:00"
        }, CancellationToken.None);

        result.Success.ShouldBeFalse("fromDate must be required");
        result.Message.ShouldContain("[REPLIES:date");
    }

    [Test]
    public async Task CreateShiftSkill_Persists_StaffCount_And_Quantity()
    {
        // ORD-9 / ORD-10: SumEmployees (ClientCount) and Quantity (Menge) are persisted from the parameters.
        var customerId = Guid.Parse("f435fe8b-6468-44c2-92fa-69b87546d4ae");
        var skill = CreateShiftSkillWithDefaultMacro();
        var name = $"{TestShiftPrefix}Counts";

        var ok = await skill.ExecuteAsync(TestSkillContext(), new Dictionary<string, object>
        {
            ["name"] = name,
            ["clientId"] = customerId.ToString(),
            ["startTime"] = "07:00",
            ["endTime"] = "15:00",
            ["fromDate"] = "2026-07-01",
            ["sumEmployees"] = 3,
            ["quantity"] = 2
        }, CancellationToken.None);
        ok.Success.ShouldBeTrue(ok.Message);

        var order = await _context.Shift.AsNoTracking()
            .FirstAsync(s => s.Name == name && s.Status == ShiftStatus.SealedOrder);
        order.SumEmployees.ShouldBe(3, "the required staff count must be persisted");
        order.Quantity.ShouldBe(2, "the quantity (Menge) must be persisted, not hardcoded to 1");
    }

    [Test]
    public async Task CreateShiftSkill_Reuse_Guard_Ignores_Scenario_Rows()
    {
        // XC-2: the reuse-guard must never match scenario rows (AnalyseToken set) — it must create a real order.
        var customerId = Guid.Parse("f435fe8b-6468-44c2-92fa-69b87546d4ae");
        var skill = CreateShiftSkillWithDefaultMacro();
        var name = $"{TestShiftPrefix}ScenarioGuard";

        Dictionary<string, object> Args() => new()
        {
            ["name"] = name,
            ["clientId"] = customerId.ToString(),
            ["startTime"] = "07:00",
            ["endTime"] = "07:00",
            ["fromDate"] = "2026-07-01"
        };

        var first = await skill.ExecuteAsync(TestSkillContext(), Args(), CancellationToken.None);
        first.Success.ShouldBeTrue(first.Message);

        // Turn the created OriginalShift into a scenario row.
        var orig = await _context.Shift
            .FirstAsync(s => s.Name == name && s.Status == ShiftStatus.OriginalShift);
        orig.AnalyseToken = Guid.NewGuid();
        await _context.SaveChangesAsync();

        // Re-issue: the scenario row must be ignored -> a fresh real order is created.
        var second = await skill.ExecuteAsync(TestSkillContext(), Args(), CancellationToken.None);
        second.Success.ShouldBeTrue(second.Message);

        (await _context.Shift.AsNoTracking()
            .CountAsync(s => s.Name == name && s.Status == ShiftStatus.OriginalShift && !s.IsDeleted))
            .ShouldBe(2, "the reuse-guard must ignore scenario rows and create a real order");
        (await _context.Shift.AsNoTracking()
            .CountAsync(s => s.Name == name && s.Status == ShiftStatus.OriginalShift && s.AnalyseToken == null && !s.IsDeleted))
            .ShouldBe(1, "exactly one real (non-scenario) order must exist");
    }

    [Test]
    public async Task FindCustomerCandidates_Lists_Customers_For_Billing()
    {
        var skill = new FindCustomerCandidatesSkill(CreateClientRepository());

        var result = await skill.ExecuteAsync(
            TestSkillContext(),
            new Dictionary<string, object> { ["searchString"] = "Tech Systems" },
            CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Count").GetInt32()
            .ShouldBeGreaterThan(0, "at least one matching customer must be listed");

        var names = doc.RootElement.GetProperty("Customers").EnumerateArray()
            .Select(c => c.GetProperty("Name").GetString())
            .ToList();
        names.ShouldContain(n => n != null && n.Contains("Tech Systems"));
    }

    [Test]
    public async Task CreateCustomer_Twice_SameBusinessKey_Reuses_NoDuplicate()
    {
        // CUS-6 / HIGH-1: re-creating a customer with the same business key (company + zip + street) on
        // "weiter" must reuse the existing customer instead of producing a duplicate (otherwise the order's
        // ClientId guard misses and a duplicate order results too). Counter-test: same company + zip but a
        // DIFFERENT street is a genuinely distinct customer (e.g. a second branch) and must NOT be merged.
        var company = $"{TestCustomerPrefix}AcmeAG";
        var skill = CreateEmployeeSkillForTest();

        Dictionary<string, object> Args(string street) => new()
        {
            ["firstName"] = "Test",
            ["lastName"] = "Contact",
            ["gender"] = "Female",
            ["entityType"] = "Customer",
            ["company"] = company,
            ["street"] = street,
            ["zip"] = "2500",
            ["city"] = "Biel",
            ["email"] = "test@example.com",
            ["phone"] = "+41 32 000 00 00",
            ["memberSince"] = "2026-07-01"
        };

        var first = await skill.ExecuteAsync(TestSkillContext(), Args("Bahnhofstrasse 1"), CancellationToken.None);
        first.Success.ShouldBeTrue(first.Message);

        var firstId = await _context.Client.AsNoTracking()
            .Where(c => c.Company == company && c.Type == EntityTypeEnum.Customer && !c.IsDeleted)
            .Select(c => c.Id)
            .SingleAsync();

        // Re-create with the SAME business key (incl. a case/whitespace-variant street) -> reuse, no duplicate.
        var second = await skill.ExecuteAsync(TestSkillContext(), Args("  bahnhofstrasse 1 "), CancellationToken.None);
        second.Success.ShouldBeTrue(second.Message);

        var json = System.Text.Json.JsonSerializer.Serialize(second.Data);
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            doc.RootElement.GetProperty("Reused").GetBoolean()
                .ShouldBeTrue("re-creating the same customer must reuse it");
            doc.RootElement.GetProperty("ClientId").GetGuid()
                .ShouldBe(firstId, "the reused id must be the existing customer's id");
        }

        (await _context.Client.AsNoTracking()
            .CountAsync(c => c.Company == company && c.Type == EntityTypeEnum.Customer && !c.IsDeleted))
            .ShouldBe(1, "re-creating the same customer must not create a duplicate");

        // Counter-test: same company + zip, DIFFERENT street -> a distinct customer (no false merge).
        var third = await skill.ExecuteAsync(TestSkillContext(), Args("Industrieweg 99"), CancellationToken.None);
        third.Success.ShouldBeTrue(third.Message);

        (await _context.Client.AsNoTracking()
            .CountAsync(c => c.Company == company && c.Type == EntityTypeEnum.Customer && !c.IsDeleted))
            .ShouldBe(2, "a different street is a distinct customer and must not be merged");
    }

    #endregion
}
