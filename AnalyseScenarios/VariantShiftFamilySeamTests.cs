// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of the pair of rows behind a shift in a variant (key), through the real PUT handler, guard and
/// repositories: a PUT addressed at the sealed order or at its plannable copy leaves both rows equal in every mirrored
/// field, in their group items (each stamped with the token), expenses, and a removed group; with cuts present a change of
/// time is refused with a conflict and writes nothing, while a rename mirrors and leaves the cut piece alone. The lookup
/// that the group-link endpoints use finds shifts with a token and ignores real shifts. Only INTEGRATION_TEST_ rows are
/// written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Domain.Enums;
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
public class VariantShiftFamilySeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARIANTFAMILY_";
    private const int MirroredGroupCount = 2;
    private const int MirroredQuantity = 3;
    private const decimal MirroredWorkTime = 7.5m;
    private static readonly DateOnly PeriodFrom = new(2099, 8, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 31);
    private static readonly TimeOnly MirroredStart = new(9, 30);
    private static readonly TimeOnly MirroredEnd = new(17, 30);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private ShiftRepository _shiftRepository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private VariantShiftWriteGuard _writeGuard = null!;
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
        var scenarioService = (AnalyseScenarioService)_provider.GetRequiredService<IAnalyseScenarioService>();
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
            scenarioService,
            groupGuard);

        _group = await CreateGroupAsync("GROUP", null);
        _subGroup = await CreateGroupAsync("SUBGROUP", _group.Id);
        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
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
    public async Task Put_AtTheSealedOrder_LeavesBothRowsEqualInEveryMirroredFieldGroupItemAndExpense()
    {
        var variant = await CreateVariantAsync("SEALED");
        await PostKeyShiftAsync("SEALED", variant.Token);
        var sealedOrder = await LoadRowAsync(variant.Token, ShiftStatus.SealedOrder);
        var resource = await EditableResourceAsync(sealedOrder.Id);
        ApplyEdit(resource);

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        await ShouldHoldParityAsync(variant.Token);
    }

    [Test]
    public async Task Put_AtThePlannableCopy_LeavesBothRowsEqualInEveryMirroredFieldGroupItemAndExpense()
    {
        var variant = await CreateVariantAsync("COPY");
        await PostKeyShiftAsync("COPY", variant.Token);
        var copy = await LoadRowAsync(variant.Token, ShiftStatus.OriginalShift);
        var resource = await EditableResourceAsync(copy.Id);
        ApplyEdit(resource);

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        await ShouldHoldParityAsync(variant.Token);
    }

    [Test]
    public async Task Put_RemovingAGroup_RemovesItFromBothRows()
    {
        var variant = await CreateVariantAsync("REMOVE");
        await PostKeyShiftAsync("REMOVE", variant.Token);
        var sealedOrder = await LoadRowAsync(variant.Token, ShiftStatus.SealedOrder);
        var added = await EditableResourceAsync(sealedOrder.Id);
        added.Groups = [new SimpleGroupResource { Id = _group.Id }, new SimpleGroupResource { Id = _subGroup.Id }];
        await PutHandler().Handle(new PutCommand<ShiftResource>(added), CancellationToken.None);
        var removed = await EditableResourceAsync(sealedOrder.Id);
        removed.Groups = [new SimpleGroupResource { Id = _group.Id }];

        await PutHandler().Handle(new PutCommand<ShiftResource>(removed), CancellationToken.None);

        _context.ChangeTracker.Clear();
        foreach (var row in await LoadFamilyAsync(variant.Token))
        {
            var items = await ActiveGroupItemsAsync(row.Id);
            items.Select(item => item.GroupId).ShouldBe(new[] { _group.Id });
            items.ShouldAllBe(item => item.AnalyseToken == variant.Token);
        }
    }

    [Test]
    public async Task Put_ChangingTheTimeOfAnOrderWithCuts_IsAConflict_AndChangesNeitherRow()
    {
        var variant = await CreateVariantAsync("CUTS");
        await PostKeyShiftAsync("CUTS", variant.Token);
        var sealedOrder = await LoadRowAsync(variant.Token, ShiftStatus.SealedOrder);
        await CreateShiftAsync("PIECE", variant.Token, ShiftStatus.SplitShift, sealedOrder.Id);
        var resource = await EditableResourceAsync(sealedOrder.Id);
        resource.StartShift = MirroredStart;

        var exception = await Should.ThrowAsync<ConflictException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        exception.Message.ShouldBe(VariantShiftWriteGuard.CutsExistMessage);
        _context.ChangeTracker.Clear();
        var rows = await LoadFamilyAsync(variant.Token);
        rows.Where(row => row.Status != ShiftStatus.SplitShift).ShouldAllBe(row => row.StartShift == new TimeOnly(8, 0));
    }

    [Test]
    public async Task Put_ChangingTheTimeOfThePlannableCopyOfAnOrderWithCuts_IsAConflictToo()
    {
        var variant = await CreateVariantAsync("CUTSCOPY");
        await PostKeyShiftAsync("CUTSCOPY", variant.Token);
        var sealedOrder = await LoadRowAsync(variant.Token, ShiftStatus.SealedOrder);
        var copy = await LoadRowAsync(variant.Token, ShiftStatus.OriginalShift);
        await CreateShiftAsync("PIECE", variant.Token, ShiftStatus.SplitShift, sealedOrder.Id);
        var resource = await EditableResourceAsync(copy.Id);
        resource.IsTuesday = true;

        await Should.ThrowAsync<ConflictException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));
    }

    [Test]
    public async Task Put_RenamingAnOrderWithCuts_MirrorsTheNameAndLeavesTheCutPieceAlone()
    {
        var variant = await CreateVariantAsync("RENAME");
        await PostKeyShiftAsync("RENAME", variant.Token);
        var sealedOrder = await LoadRowAsync(variant.Token, ShiftStatus.SealedOrder);
        var piece = await CreateShiftAsync("PIECE", variant.Token, ShiftStatus.SplitShift, sealedOrder.Id);
        var resource = await EditableResourceAsync(sealedOrder.Id);
        resource.Name = TestPrefix + "RENAMED";
        resource.Description = "Renamed description";

        await PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        _context.ChangeTracker.Clear();
        var rows = await LoadFamilyAsync(variant.Token);
        rows.Where(row => row.Status != ShiftStatus.SplitShift).ShouldAllBe(
            row => row.Name == TestPrefix + "RENAMED" && row.Description == "Renamed description");
        rows.Single(row => row.Id == piece.Id).Name.ShouldBe(TestPrefix + "PIECE");
    }

    [Test]
    public async Task FindFirstTokenedShiftId_FindsKeyShiftsAndTokenRows_ForUnrestrictedCallersToo_AndIgnoresRealShifts()
    {
        var variant = await CreateVariantAsync("LOOKUP");
        var real = await CreateShiftAsync("REAL", null, ShiftStatus.OriginalShift);
        var keyShift = await CreateShiftAsync("KEY", variant.Token, ShiftStatus.SealedOrder);
        var reader = new ShiftVisibilityReadRepository(_context);

        (await reader.FindFirstTokenedShiftIdAsync([real.Id], CancellationToken.None)).ShouldBeNull();
        (await reader.FindFirstTokenedShiftIdAsync([], CancellationToken.None)).ShouldBeNull();
        (await reader.FindFirstTokenedShiftIdAsync([real.Id, keyShift.Id], CancellationToken.None)).ShouldBe(keyShift.Id);
        (await reader.FindFirstTokenedShiftIdAsync([Guid.NewGuid()], CancellationToken.None)).ShouldBeNull();
    }

    private void ApplyEdit(ShiftResource resource)
    {
        resource.Name = TestPrefix + "EDITED";
        resource.Abbreviation = "EDT";
        resource.Description = "Edited description";
        resource.StartShift = MirroredStart;
        resource.EndShift = MirroredEnd;
        resource.Quantity = MirroredQuantity;
        resource.IsTuesday = true;
        resource.WorkTime = MirroredWorkTime;
        resource.UntilDate = PeriodUntil.AddDays(-1);
        resource.Groups = [new SimpleGroupResource { Id = _group.Id }, new SimpleGroupResource { Id = _subGroup.Id }];
        resource.DefaultExpenses = [new ShiftExpensesResource { Amount = 2m, Description = TestPrefix + "EXPENSE" }];
    }

    private async Task ShouldHoldParityAsync(Guid token)
    {
        _context.ChangeTracker.Clear();
        var sealedOrder = await LoadRowAsync(token, ShiftStatus.SealedOrder);
        var copy = await LoadRowAsync(token, ShiftStatus.OriginalShift);

        sealedOrder.Name.ShouldBe(TestPrefix + "EDITED");
        sealedOrder.StartShift.ShouldBe(MirroredStart);
        sealedOrder.Quantity.ShouldBe(MirroredQuantity);
        foreach (var name in VariantShiftMirror.MirroredFieldNames)
        {
            var property = typeof(Shift).GetProperty(name)!;
            property.GetValue(copy).ShouldBe(property.GetValue(sealedOrder), name);
        }

        copy.AnalyseToken.ShouldBe(token);
        sealedOrder.AnalyseToken.ShouldBe(token);
        copy.OriginalId.ShouldBe(sealedOrder.Id);
        foreach (var row in new[] { sealedOrder, copy })
        {
            var items = await ActiveGroupItemsAsync(row.Id);
            items.Count.ShouldBe(MirroredGroupCount);
            items.Select(item => item.GroupId).ShouldBe(new[] { _group.Id, _subGroup.Id }, ignoreOrder: true);
            items.ShouldAllBe(item => item.AnalyseToken == token);

            var expenses = await _context.Set<ShiftExpenses>().AsNoTracking()
                .Where(expense => expense.ShiftId == row.Id && !expense.IsDeleted)
                .ToListAsync();
            expenses.Select(expense => expense.Description).ShouldBe(new[] { TestPrefix + "EXPENSE" });
        }
    }

    private async Task<List<GroupItem>> ActiveGroupItemsAsync(Guid shiftId)
    {
        return await _context.Set<GroupItem>().AsNoTracking()
            .Where(item => item.ShiftId == shiftId && !item.IsDeleted)
            .ToListAsync();
    }

    private async Task<ShiftResource> EditableResourceAsync(Guid shiftId)
    {
        var resource = _mapper.ToShiftResource((await _shiftRepository.Get(shiftId))!);
        resource.AnalyseToken = null;
        resource.Client = null;
        _context.ChangeTracker.Clear();
        return resource;
    }

    private async Task<Shift> LoadRowAsync(Guid token, ShiftStatus status)
    {
        return (await LoadFamilyAsync(token)).Single(row => row.Status == status);
    }

    private async Task<List<Shift>> LoadFamilyAsync(Guid token)
    {
        return await _context.Shift.AsNoTracking().Where(s => s.AnalyseToken == token && !s.IsDeleted).ToListAsync();
    }

    private async Task PostKeyShiftAsync(string suffix, Guid token)
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
            Groups = [new SimpleGroupResource { Id = _group.Id }]
        };

        var sealing = Substitute.For<IOrderSealingService>();
        sealing.CollectMissingRequirements(Arg.Any<Shift>()).Returns(Array.Empty<string>());
        var handler = new PostCommandHandler(
            _shiftRepository,
            _mapper,
            _unitOfWork,
            Substitute.For<IDefaultShiftMacroResolver>(),
            sealing,
            Substitute.For<IShiftVisibilityGuard>(),
            Substitute.For<IClientReferenceGuard>(),
            _writeGuard,
            Substitute.For<ILogger<PostCommandHandler>>());

        await handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);
        _context.ChangeTracker.Clear();
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

    private async Task<Shift> CreateShiftAsync(string suffix, Guid? analyseToken, ShiftStatus status, Guid? originalId = null)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Variant family seam",
            Status = status,
            FromDate = PeriodFrom,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            OriginalId = originalId,
            AnalyseToken = analyseToken
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
            DELETE FROM client_shift_preference WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL, original_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
