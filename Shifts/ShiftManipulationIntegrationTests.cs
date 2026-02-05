using FluentAssertions;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Application.DTOs.Schedules;
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
            dateRangeFilterService,
            shiftSearchService,
            shiftSortingService,
            shiftStatusFilterService,
            shiftPaginationService,
            shiftGroupManagementService,
            entityCollectionUpdateService,
            shiftValidator,
            _scheduleMapper);

        // Create ShiftResetService
        var shiftResetServiceLogger = Substitute.For<ILogger<ShiftResetService>>();
        var shiftResetService = new ShiftResetService(_shiftRepository, _scheduleMapper, shiftResetServiceLogger);

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
        result.Should().NotBeNull("Handler should return a result");
        result!.Status.Should().Be(ShiftStatus.OriginalOrder, "Should remain OriginalOrder (not plannable)");

        Console.WriteLine("=== CREATE ORIGINAL ORDER TEST ===");
        Console.WriteLine($"Created Shift: Id={result.Id}, Status={result.Status}, Name={result.Name}");

        // Verify database state - should be only 1 shift (no automatic copy for OriginalOrder)
        var shiftsInDb = await _context.Shift
            .Where(s => s.Id == result.Id)
            .AsNoTracking()
            .ToListAsync();

        shiftsInDb.Should().HaveCount(1, "Should have only 1 shift (OriginalOrder)");
        shiftsInDb[0].Status.Should().Be(ShiftStatus.OriginalOrder);

        Console.WriteLine("=== TEST PASSED: OriginalOrder created (editable, not plannable) ===");
    }

    [Test]
    public async Task SealOriginalOrder_Should_Create_SealedOrder_And_OriginalShift()
    {
        // Arrange - First create an OriginalOrder
        var originalOrderResource = CreateTestShiftResource("Seal_Test", ShiftStatus.OriginalOrder);
        var createCommand = new PostCommand<ShiftResource>(originalOrderResource);
        var originalOrder = await _postHandler.Handle(createCommand, CancellationToken.None);

        originalOrder.Should().NotBeNull();
        originalOrder!.Status.Should().Be(ShiftStatus.OriginalOrder);

        Console.WriteLine("=== SEAL ORIGINAL ORDER TEST ===");
        Console.WriteLine($"Step 1: Created OriginalOrder: Id={originalOrder.Id}, Status={originalOrder.Status}");

        // Act - Simulate "Lock" button: change status to SealedOrder and save (PUT = update)
        originalOrder.Status = ShiftStatus.SealedOrder;
        var sealCommand = new PutCommand<ShiftResource>(originalOrder);
        var result = await _putHandler.Handle(sealCommand, CancellationToken.None);

        // Assert
        result.Should().NotBeNull("Handler should return a result");
        result!.Status.Should().Be(ShiftStatus.OriginalShift, "Returned shift should be OriginalShift (the plannable copy)");

        Console.WriteLine($"Step 2: After sealing - Returned: Id={result.Id}, Status={result.Status}, OriginalId={result.OriginalId}");

        // Verify database state
        var allShifts = await GetAllShiftsWithOriginalId(result.OriginalId!.Value);

        allShifts.Should().HaveCount(2, "Should have SealedOrder + OriginalShift");

        var sealedOrder = allShifts.FirstOrDefault(s => s.Status == ShiftStatus.SealedOrder);
        var originalShift = allShifts.FirstOrDefault(s => s.Status == ShiftStatus.OriginalShift);

        sealedOrder.Should().NotBeNull("SealedOrder should exist (permanently sealed)");
        originalShift.Should().NotBeNull("OriginalShift should exist (plannable copy)");

        Console.WriteLine($"SealedOrder: Id={sealedOrder!.Id}, Status={sealedOrder.Status} (permanently sealed)");
        Console.WriteLine($"OriginalShift: Id={originalShift!.Id}, Status={originalShift.Status}, OriginalId={originalShift.OriginalId} (plannable)");

        // Verify relationship
        originalShift.OriginalId.Should().Be(sealedOrder.Id, "OriginalShift.OriginalId should reference SealedOrder");
        sealedOrder.Name.Should().Be(originalShift.Name, "Names should match");
        sealedOrder.FromDate.Should().Be(originalShift.FromDate, "FromDate should match");

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

        createdShift.Should().NotBeNull();
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
        results.Should().HaveCount(2, "Should return 2 SplitShifts");

        foreach (var result in results)
        {
            result.Status.Should().Be(ShiftStatus.SplitShift, "All results should be SplitShifts");
            result.OriginalId.Should().Be(sealedOrderId, "OriginalId should reference SealedOrder");
            Console.WriteLine($"SplitShift: Id={result.Id}, FromDate={result.FromDate}, UntilDate={result.UntilDate}, " +
                            $"StartShift={result.StartShift}, EndShift={result.EndShift}");
        }

        // Verify database state
        var allShifts = await GetAllShiftsWithOriginalId(sealedOrderId);
        allShifts.Should().HaveCount(4, "Should have SealedOrder + OriginalShift + 2 SplitShifts");

        var splitShifts = allShifts.Where(s => s.Status == ShiftStatus.SplitShift).ToList();
        splitShifts.Should().HaveCount(2, "Should have exactly 2 SplitShifts");

        Console.WriteLine("=== TEST PASSED ===");
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
        nestedResults.Should().HaveCount(2, "Should create 2 nested SplitShifts");

        foreach (var result in nestedResults)
        {
            result.Status.Should().Be(ShiftStatus.SplitShift);
            result.ParentId.Should().Be(firstLevelShiftId, "ParentId should reference first-level SplitShift");
            Console.WriteLine($"Nested SplitShift: Id={result.Id}, ParentId={result.ParentId}, RootId={result.RootId}");
        }

        // Verify hierarchy
        var allShifts = await GetAllShiftsWithOriginalId(sealedOrderId);
        var splitShifts = allShifts.Where(s => s.Status == ShiftStatus.SplitShift).ToList();

        splitShifts.Should().HaveCount(3, "Should have 3 SplitShifts total (1 first-level + 2 nested)");

        var nestedShifts = splitShifts.Where(s => s.ParentId == firstLevelShiftId).ToList();
        nestedShifts.Should().HaveCount(2, "Should have 2 nested SplitShifts");

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

        closedSplits.Should().HaveCountGreaterThanOrEqualTo(0, "Old splits should be closed or deleted");

        // Check for new OriginalShift starting from reset date
        var newOriginalShift = resetResults.FirstOrDefault(s =>
            s.Status == ShiftStatus.OriginalShift &&
            s.FromDate >= new DateOnly(2025, 7, 1));

        newOriginalShift.Should().NotBeNull("New OriginalShift should be created from reset date");
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
        updateResults.Should().HaveCount(1);
        var updatedResult = updateResults[0];

        updatedResult.Id.Should().Be(splitShiftId, "Should be the same shift");
        updatedResult.StartShift.Should().Be(new TimeOnly(9, 0), "StartShift should be updated");
        updatedResult.EndShift.Should().Be(new TimeOnly(17, 0), "EndShift should be updated");

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
            shift.RootId.Should().NotBeNull("RootId should be set for all SplitShifts");
        }

        // Verify parent-child relationships
        var rootShift = allSplitShifts.FirstOrDefault(s => s.ParentId == null);
        var childShifts = allSplitShifts.Where(s => s.ParentId != null).ToList();

        if (rootShift != null)
        {
            rootShift.RootId.Should().Be(rootShift.Id, "Root's RootId should reference itself");

            foreach (var child in childShifts)
            {
                child.RootId.Should().Be(rootShift.RootId, "Children should have same RootId as root");
            }
        }

        // Verify Lft < Rgt for each node (basic nested set invariant)
        foreach (var shift in allSplitShifts.Where(s => s.Lft.HasValue && s.Rgt.HasValue))
        {
            shift.Lft.Should().BeLessThan(shift.Rgt!.Value, $"Lft should be less than Rgt for {shift.Name}");
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

        createdShift.Should().NotBeNull();
        createdShift!.Status.Should().Be(ShiftStatus.OriginalShift);
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
        splitResults.Should().HaveCount(4);

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
        updateResults[0].StartShift.Should().Be(new TimeOnly(10, 0));
        Console.WriteLine($"Updated Q2: StartShift={updateResults[0].StartShift}, EndShift={updateResults[0].EndShift}");

        // Step 4: Verify final state
        Console.WriteLine("\n--- Step 4: Final state ---");
        var finalShifts = await GetAllShiftsWithOriginalId(sealedOrderId);

        Console.WriteLine($"Total shifts: {finalShifts.Count}");
        foreach (var s in finalShifts.OrderBy(x => x.Status).ThenBy(x => x.FromDate))
        {
            Console.WriteLine($"  {s.Name}: Status={s.Status}, FromDate={s.FromDate}, UntilDate={s.UntilDate}");
        }

        finalShifts.Count(s => s.Status == ShiftStatus.SealedOrder).Should().Be(1);
        finalShifts.Count(s => s.Status == ShiftStatus.OriginalShift).Should().Be(1);
        finalShifts.Count(s => s.Status == ShiftStatus.SplitShift).Should().Be(4);

        Console.WriteLine("\n=== FULL WORKFLOW TEST PASSED ===");
    }

    #endregion
}
