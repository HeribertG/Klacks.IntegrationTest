// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of ScenarioVariantCoverageDto.CopiedShiftCount on the real path: a real order is copied into a key by
/// ScenarioVariantShiftCopier, the scenario is created by the real handler and cloned by the real clone service, and the
/// summary is built on top. The count is per order: pieces cut inside the key and the rows of the family do not count. An
/// order written directly into the key, an original that was soft-deleted, one that no longer overlaps the key period and a
/// key the scenario does not carry count nothing. A chained scenario sees the same count, and a key that plans nothing in
/// the scenario period still shows its count, because the accept promotes it anyway. Only INTEGRATION_TEST_ rows are
/// written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.Summary;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
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
public class VariantCopiedShiftCountSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARCOPYCOUNT_";
    private const int TwoOrders = 2;
    private static readonly DateOnly PeriodFrom = new(2099, 9, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 9, 30);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private CreateAnalyseScenarioCommandHandler _handler = null!;
    private ScenarioVariantShiftCopier _copier = null!;
    private Group _group = null!;
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
        _scenarioService = _provider.GetRequiredService<IAnalyseScenarioService>();

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(_ => _context.SaveChangesAsync());
        var groupGuard = Substitute.For<IGroupVisibilityGuard>();
        groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);
        groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _handler = new CreateAnalyseScenarioCommandHandler(
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            new ScenarioVariantRepository(_context),
            _scenarioService,
            unitOfWork,
            groupGuard,
            Substitute.For<ILogger<CreateAnalyseScenarioCommandHandler>>());

        var visibility = Substitute.For<IShiftVisibilityGuard>();
        _copier = new ScenarioVariantShiftCopier(_context, _provider.GetRequiredService<ScenarioGroupScopeReader>(), visibility);

        _group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "GROUP",
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Group>().AddAsync(_group);
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
    public async Task CopiedOrders_AreCountedOncePerOrder_OnTheRealPath()
    {
        var variant = await CreateVariantAsync("KEY");
        await CopyAsync(variant, await CreateRealOrderAsync("REAL1"));
        await CopyAsync(variant, await CreateRealOrderAsync("REAL2"));
        await CreateKeyOrderAsync(variant, "WRITTENINTOKEY");

        var scenario = await CreateScenarioAsync(PeriodFrom, PeriodUntil, variant.Id);

        (await EntryOfAsync(scenario.Token, variant)).CopiedShiftCount.ShouldBe(TwoOrders);
    }

    [Test]
    public async Task ACutInsideTheKey_DoesNotChangeTheCount()
    {
        var variant = await CreateVariantAsync("KEY");
        await CopyAsync(variant, await CreateRealOrderAsync("REAL"));
        var keyOrder = await _context.Shift.AsNoTracking()
            .SingleAsync(shift => shift.AnalyseToken == variant.Token && shift.Status == ShiftStatus.SealedOrder);
        var plannable = await _context.Shift
            .SingleAsync(shift => shift.AnalyseToken == variant.Token && shift.Status == ShiftStatus.OriginalShift);
        plannable.Status = ShiftStatus.SplitShift;
        plannable.RootId = plannable.Id;
        var second = NewShift("PIECE", ShiftStatus.SplitShift, PeriodFrom, PeriodUntil, variant.Token);
        second.OriginalId = keyOrder.Id;
        second.RootId = second.Id;
        await AddAsync(second);
        _context.ChangeTracker.Clear();

        var scenario = await CreateScenarioAsync(PeriodFrom, PeriodUntil, variant.Id);

        (await EntryOfAsync(scenario.Token, variant)).CopiedShiftCount.ShouldBe(1);
    }

    [Test]
    public async Task ASoftDeletedOriginal_IsNotCounted()
    {
        var variant = await CreateVariantAsync("KEY");
        var real = await CreateRealOrderAsync("REAL");
        await CopyAsync(variant, real);
        await CopyAsync(variant, await CreateRealOrderAsync("REAL2"));
        await ChangeRealOrderAsync(real.Id, order => order.IsDeleted = true);

        var scenario = await CreateScenarioAsync(PeriodFrom, PeriodUntil, variant.Id);

        (await EntryOfAsync(scenario.Token, variant)).CopiedShiftCount.ShouldBe(1);
    }

    [Test]
    public async Task AnOriginalThatNoLongerOverlapsTheKey_IsNotCounted_AndTheBoundaryDayStillIs()
    {
        var variant = await CreateVariantAsync("KEY");
        var ended = await CreateRealOrderAsync("ENDED");
        var endsOnFirstDay = await CreateRealOrderAsync("ENDSONFROM");
        await CopyAsync(variant, ended);
        await CopyAsync(variant, endsOnFirstDay);
        await ChangeRealOrderAsync(ended.Id, order => order.UntilDate = PeriodFrom.AddDays(-1));
        await ChangeRealOrderAsync(endsOnFirstDay.Id, order => order.UntilDate = PeriodFrom);

        var scenario = await CreateScenarioAsync(PeriodFrom, PeriodUntil, variant.Id);

        (await EntryOfAsync(scenario.Token, variant)).CopiedShiftCount.ShouldBe(1);
    }

    [Test]
    public async Task AnOpenEndedOriginal_AndOneStartingOnTheLastDay_AreCounted_OneStartingLaterIsNot()
    {
        var variant = await CreateVariantAsync("KEY");
        var openEnded = await CreateRealOrderAsync("OPENENDED");
        var startsOnLastDay = await CreateRealOrderAsync("STARTSONUNTIL");
        var startsLater = await CreateRealOrderAsync("STARTSLATER");
        await CopyAsync(variant, openEnded);
        await CopyAsync(variant, startsOnLastDay);
        await CopyAsync(variant, startsLater);
        await ChangeRealOrderAsync(startsOnLastDay.Id, order => order.FromDate = PeriodUntil);
        await ChangeRealOrderAsync(startsLater.Id, order => order.FromDate = PeriodUntil.AddDays(1));

        var scenario = await CreateScenarioAsync(PeriodFrom, PeriodUntil, variant.Id);

        (await EntryOfAsync(scenario.Token, variant)).CopiedShiftCount.ShouldBe(TwoOrders);
    }

    [Test]
    public async Task EachKey_CountsItsOwnOrders_AndAKeyTheScenarioDoesNotCarryCountsNothing()
    {
        var carried = await CreateVariantAsync("CARRIED");
        var alsoCarried = await CreateVariantAsync("ALSOCARRIED");
        var notCarried = await CreateVariantAsync("NOTCARRIED");
        var real = await CreateRealOrderAsync("REAL");
        await CopyAsync(carried, real);
        await CopyAsync(alsoCarried, real);
        await CopyAsync(alsoCarried, await CreateRealOrderAsync("REAL2"));
        await CopyAsync(notCarried, real);

        var scenario = await CreateScenarioAsync(PeriodFrom, PeriodUntil, carried.Id, alsoCarried.Id);

        var summary = await BuildSummaryAsync(scenario.Token);
        summary.Variants!.Count.ShouldBe(TwoOrders);
        summary.Variants.Single(entry => entry.VariantId == carried.Id).CopiedShiftCount.ShouldBe(1);
        summary.Variants.Single(entry => entry.VariantId == alsoCarried.Id).CopiedShiftCount.ShouldBe(TwoOrders);
    }

    [Test]
    public async Task AChainedScenario_SeesTheSameCount()
    {
        var variant = await CreateVariantAsync("KEY");
        await CopyAsync(variant, await CreateRealOrderAsync("REAL"));
        var source = await CreateScenarioAsync(PeriodFrom, PeriodUntil, variant.Id);
        var child = await AddScenarioRowAsync("CHILD");

        await _scenarioService.CloneScenarioDataAsync(_group.Id, PeriodFrom, PeriodUntil, child.Token, null, source.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await EntryOfAsync(child.Token, variant)).CopiedShiftCount.ShouldBe(1);
    }

    [Test]
    public async Task AKeyThatPlansNothingInTheScenarioPeriod_StillShowsItsCount()
    {
        var variant = await CreateVariantAsync("KEY");
        await CopyAsync(variant, await CreateRealOrderAsync("REAL"));

        var scenario = await CreateScenarioAsync(PeriodUntil.AddDays(30), PeriodUntil.AddDays(40), variant.Id);

        var entry = await EntryOfAsync(scenario.Token, variant);
        entry.CopiedShiftCount.ShouldBe(1);
        entry.DemandedSlots.ShouldBe(0);
    }

    [Test]
    public async Task ACarriedKeyWithoutCopies_KeepsCountZero()
    {
        var variant = await CreateVariantAsync("KEY");
        await CreateKeyOrderAsync(variant, "WRITTENINTOKEY");

        var scenario = await CreateScenarioAsync(PeriodFrom, PeriodUntil, variant.Id);

        (await EntryOfAsync(scenario.Token, variant)).CopiedShiftCount.ShouldBe(0);
    }

    private async Task<ScenarioVariantCoverageDto> EntryOfAsync(Guid token, AnalyseScenario variant)
    {
        var summary = await BuildSummaryAsync(token);
        summary.Variants.ShouldNotBeNull();
        return summary.Variants.Single(entry => entry.VariantId == variant.Id);
    }

    private async Task<ScenarioSummaryDto> BuildSummaryAsync(Guid token)
    {
        var contractProvider = Substitute.For<IClientContractDataProvider>();
        contractProvider
            .GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>());
        var builder = new ScenarioSummaryBuilder(
            _context,
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            _scenarioService,
            contractProvider);

        var summary = await builder.BuildAsync(token, CancellationToken.None);

        summary.ShouldNotBeNull();
        return summary;
    }

    private async Task CopyAsync(AnalyseScenario variant, Shift realOrder)
    {
        await _copier.StageCopyOfShiftsAsync(variant, [_group.Id], [realOrder.Id]);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<AnalyseScenarioResource> CreateScenarioAsync(DateOnly from, DateOnly until, params Guid[] variantIds)
    {
        var scenario = await _handler.Handle(
            new CreateAnalyseScenarioCommand(new CreateAnalyseScenarioRequest
            {
                Name = TestPrefix + "SCENARIO",
                GroupId = _group.Id,
                FromDate = from,
                UntilDate = until,
                VariantIds = variantIds
            }),
            CancellationToken.None);
        _context.ChangeTracker.Clear();
        return scenario;
    }

    private async Task<AnalyseScenario> AddScenarioRowAsync(string suffix)
    {
        var row = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            GroupId = _group.Id,
            Token = Guid.NewGuid(),
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active,
            Kind = AnalyseScenarioKind.Scenario,
            CreatedByUser = TestPrefix + "USER"
        };
        await _context.Set<AnalyseScenario>().AddAsync(row);
        await _context.SaveChangesAsync();
        return row;
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

    private async Task<Shift> CreateRealOrderAsync(string suffix)
    {
        var order = NewShift(suffix + "_ORDER", ShiftStatus.SealedOrder, PeriodFrom.AddDays(-60), null, null);
        await AddAsync(order);
        var plannable = NewShift(suffix + "_PLAN", ShiftStatus.OriginalShift, PeriodFrom.AddDays(-60), null, null);
        plannable.OriginalId = order.Id;
        await AddAsync(plannable);
        _context.ChangeTracker.Clear();
        return order;
    }

    private async Task CreateKeyOrderAsync(AnalyseScenario variant, string suffix)
    {
        var order = NewShift(suffix + "_ORDER", ShiftStatus.SealedOrder, PeriodFrom, PeriodUntil, variant.Token);
        await AddAsync(order);
        var plannable = NewShift(suffix + "_PLAN", ShiftStatus.OriginalShift, PeriodFrom, PeriodUntil, variant.Token);
        plannable.OriginalId = order.Id;
        await AddAsync(plannable);
        _context.ChangeTracker.Clear();
    }

    private async Task ChangeRealOrderAsync(Guid id, Action<Shift> change)
    {
        var order = await _context.Shift.IgnoreQueryFilters().SingleAsync(shift => shift.Id == id);
        change(order);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task AddAsync(Shift shift)
    {
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
    }

    private Shift NewShift(string suffix, ShiftStatus status, DateOnly from, DateOnly? until, Guid? token)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Variant copied count seam",
            Status = status,
            FromDate = from,
            UntilDate = until,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            SumEmployees = 1,
            WorkTime = 8m,
            ClientId = _client.Id,
            AnalyseToken = token,
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = _group.Id, ShiftId = id, AnalyseToken = token }]
        };
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
}
