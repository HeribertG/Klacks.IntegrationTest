// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Klacks.Api.Infrastructure.Services.Schedules;

namespace Klacks.IntegrationTest.Shifts;

/// <summary>
/// Pins, against real PostgreSQL, every shift read shape the Application layer builds on the shift table: the
/// open-order summary scan (unsealed orders not ended before the company day, one summary), the uncut full-day scan, the
/// empty-container scan (anti-join against the template ids), Shifts/ByIds (real plan, repository order with
/// PostgreSQL's NULLS LAST on the original id, client with addresses), find_split_shift_candidates (live group link,
/// case-insensitive ILike name filter) and the order-lineage lookups of the cut skills (first plannable shift in
/// repository order, split-part count and existence). The company day is pinned far in the future so the scans'
/// date predicates select seeded rows only; assertions on scans over the whole table stay on seeded ids. Every
/// shift, client and group carries the INTEGRATION_TEST_ShiftReadShapes_ prefix and the cleanup deletes exactly those.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class ShiftReadShapesTests
{
    private const string Prefix = "INTEGRATION_TEST_ShiftReadShapes_";
    private static readonly DateOnly Today = new(2031, 1, 10);

    private DataBaseContext _context = null!;
    private ShiftRepository _shiftRepository = null!;
    private ShiftReader _shiftReader = null!;
    private ContainerTemplateRepository _containerTemplateRepository = null!;
    private ICompanyClock _clock = null!;
    private string _token = null!;

    private Guid _groupId;
    private Guid _hiddenGroupId;
    private Guid _clientId;

    private Shift _order = null!;
    private Shift _planEarly = null!;
    private Shift _planLate = null!;
    private Shift _partDay = null!;
    private Shift _partNight = null!;
    private Shift _unlinkedOrder = null!;

    private Shift _openToday = null!;
    private Shift _openLaterLowId = null!;
    private Shift _openLaterHighId = null!;
    private Shift _openPast = null!;
    private Shift _openTokenCopy = null!;
    private Shift _openSourceLinked = null!;
    private Shift _openDeleted = null!;
    private Shift _openEnded = null!;

    private Shift _fullDayOpen = null!;
    private Shift _fullDayEndsToday = null!;
    private Shift _fullDayEnded = null!;
    private Shift _fullDayUnequalTimes = null!;
    private Shift _fullDaySplit = null!;
    private Shift _fullDayContainer = null!;
    private Shift _fullDayTokenCopy = null!;

    private Shift _containerEmpty = null!;
    private Shift _containerWithTemplate = null!;
    private Shift _containerDeletedTemplate = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();

        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _token = "shrs" + Guid.NewGuid().ToString("N")[..10];

        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        _shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            Substitute.For<IShiftQueryPipelineService>(),
            Substitute.For<IShiftGroupManagementService>(),
            collectionUpdateService,
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper(),
            _clock);
        _shiftReader = new ShiftReader(_context);
        _containerTemplateRepository = new ContainerTemplateRepository(
            _context,
            Substitute.For<ILogger<ContainerTemplate>>(),
            collectionUpdateService,
            new ContainerTemplateService(Substitute.For<IUnitOfWork>(), NullLogger<ContainerTemplateService>.Instance));

        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task OpenOrderSummaryScan_CountsUnsealedOrdersNotEndedBeforeToday_WithoutEndedScenarioOrDeletedOrders()
    {
        var detector = new OpenOrderDetector(
            _shiftReader, new ShiftGroupScopeReadRepository(_context), _clock, NullLogger<OpenOrderDetector>.Instance);

        var summary = (await detector.DetectAsync()).Cast<OpenOrdersSummaryTriggerEvent>().ShouldHaveSingleItem();
        var fingerprints = await detector.GetActiveFingerprintsAsync();

        summary.CompanyDay.ShouldBe(Today);
        summary.Orders.Select(order => order.ShiftId).Where(SeededIds().Contains)
            .ShouldBe([_openToday.Id, _openLaterLowId.Id, _openLaterHighId.Id, _openPast.Id], ignoreOrder: true);
        summary.Orders.Single(order => order.ShiftId == _openToday.Id).GroupIds.ShouldBeEmpty();
        fingerprints.ShouldBe([AgentConditionLedgerPolicy.FingerprintFor(summary)]);
        fingerprints.Single().ShouldBe(AgentConditionLedgerPolicy.FingerprintFor(
            detector.Kind, OpenOrdersSummaryTriggerEvent.DedupKeyFor(Today)));
    }

    [Test]
    public async Task UncutFullDayScan_KeepsRunningEqualTimeTasks_WithoutEndedSplitContainerOrScenarioRows()
    {
        var agentConditions = Substitute.For<IAgentConditionRepository>();
        agentConditions.GetOpenByKindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new List<AgentCondition>());
        var detector = new UncutFullDayShiftDetector(
            _shiftReader, new ShiftGroupScopeReadRepository(_context), agentConditions, _clock,
            NullLogger<UncutFullDayShiftDetector>.Instance);

        var fingerprints = await detector.GetActiveFingerprintsAsync();

        fingerprints.ShouldContain(UncutFingerprint(detector.Kind, _fullDayOpen));
        fingerprints.ShouldContain(UncutFingerprint(detector.Kind, _fullDayEndsToday));
        foreach (var shift in new[] { _fullDayEnded, _fullDayUnequalTimes, _fullDaySplit, _fullDayContainer, _fullDayTokenCopy })
        {
            fingerprints.ShouldNotContain(UncutFingerprint(detector.Kind, shift));
        }
    }

    [Test]
    public async Task EmptyContainerScan_SkipsContainersWithALiveTemplate_KeepsThoseWithOnlyADeletedOne()
    {
        var agentConditions = Substitute.For<IAgentConditionRepository>();
        agentConditions.GetOpenByKindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new List<AgentCondition>());
        var detector = new EmptyContainerDetector(
            _shiftReader, _containerTemplateRepository, new ShiftGroupScopeReadRepository(_context), agentConditions,
            _clock, NullLogger<EmptyContainerDetector>.Instance);

        var fingerprints = await detector.GetActiveFingerprintsAsync();

        fingerprints.ShouldContain(ContainerFingerprint(detector.Kind, _containerEmpty));
        fingerprints.ShouldContain(ContainerFingerprint(detector.Kind, _containerDeletedTemplate));
        fingerprints.ShouldNotContain(ContainerFingerprint(detector.Kind, _containerWithTemplate));
    }

    [Test]
    public async Task ByIds_Unrestricted_ReturnsRealPlanRows_InRepositoryOrder_WithClientAndAddresses()
    {
        var result = await ByIdsAsync(
            null,
            _order.Id, _planLate.Id, _partNight.Id, _planEarly.Id, _partDay.Id, _openTokenCopy.Id, _openSourceLinked.Id);

        result.Select(r => r.Id).ShouldBe([_partDay.Id, _partNight.Id, _planEarly.Id, _planLate.Id, _order.Id]);
        result.ShouldAllBe(r => r.Client != null && r.Client.Addresses.Count == 2);
    }

    [Test]
    public async Task ByIds_RestrictedToAnotherGroup_KeepsOnlyGroupLessRows()
    {
        var result = await ByIdsAsync([_hiddenGroupId], _order.Id, _partDay.Id, _openToday.Id);

        result.Select(r => r.Id).ShouldBe([_openToday.Id]);
    }

    [Test]
    public async Task FindSplitCandidates_NameFilterIsCaseInsensitive_AndSkipsDeletedGroupLinks()
    {
        var orders = await FindSplitCandidatesAsync("bIEL ORDER " + _token.ToUpperInvariant());

        var order = orders.Single();
        order.GetProperty("SealedOrderId").GetGuid().ShouldBe(_order.Id);
        order.GetProperty("OrderShiftId").GetGuid().ShouldBe(_planEarly.Id);
        order.GetProperty("PartCount").GetInt32().ShouldBe(2);
        order.GetProperty("Parts").EnumerateArray().Select(p => p.GetProperty("Id").GetGuid()).ShouldBe([_partDay.Id, _partNight.Id]);
        (await FindSplitCandidatesAsync("no such order " + _token)).ShouldBeEmpty();
        (await FindSplitCandidatesAsync(string.Empty)).Select(o => o.GetProperty("SealedOrderId").GetGuid())
            .ShouldBe([_order.Id]);
    }

    [Test]
    public async Task OrderLineage_FormerGetQueryComposition_FirstPlannableShiftInRepositoryOrder_AndSplitParts()
    {
        var firstPlan = await _shiftRepository.GetQuery()
            .FirstOrDefaultAsync(s => s.OriginalId == _order.Id && s.Status == ShiftStatus.OriginalShift);
        var partCount = await _shiftRepository.GetQuery()
            .CountAsync(s => s.OriginalId == _order.Id && s.Status == ShiftStatus.SplitShift);
        var isSplit = await _shiftRepository.GetQuery()
            .AnyAsync(s => s.OriginalId == _order.Id && s.Status == ShiftStatus.SplitShift);
        var unlinkedIsSplit = await _shiftRepository.GetQuery()
            .AnyAsync(s => s.OriginalId == _unlinkedOrder.Id && s.Status == ShiftStatus.SplitShift);

        firstPlan.ShouldNotBeNull();
        firstPlan.Id.ShouldBe(_planEarly.Id);
        _context.Entry(firstPlan).State.ShouldBe(EntityState.Detached);
        partCount.ShouldBe(2);
        isSplit.ShouldBeTrue();
        unlinkedIsSplit.ShouldBeFalse();
    }

    [Test]
    public async Task OrderLineage_RepositoryReads_EqualTheFormerComposition()
    {
        var firstPlan = await _shiftReader.FindFirstByOriginalIdAsync(_order.Id, ShiftStatus.OriginalShift);
        var firstPart = await _shiftReader.FindFirstByOriginalIdAsync(_order.Id, ShiftStatus.SplitShift);
        var legacyFirstPart = await _shiftRepository.GetQuery()
            .FirstOrDefaultAsync(s => s.OriginalId == _order.Id && s.Status == ShiftStatus.SplitShift);

        firstPlan!.Id.ShouldBe(_planEarly.Id);
        _context.Entry(firstPlan).State.ShouldBe(EntityState.Detached);
        firstPart!.Id.ShouldBe(legacyFirstPart!.Id);
        firstPart.Id.ShouldBe(_partDay.Id);
        (await _shiftReader.CountByOriginalIdAsync(_order.Id, ShiftStatus.SplitShift)).ShouldBe(2);
        (await _shiftReader.CountByOriginalIdAsync(_order.Id, ShiftStatus.OriginalShift)).ShouldBe(2);
        (await _shiftReader.AnyByOriginalIdAsync(_order.Id, ShiftStatus.SplitShift)).ShouldBeTrue();
        (await _shiftReader.AnyByOriginalIdAsync(_unlinkedOrder.Id, ShiftStatus.SplitShift)).ShouldBeFalse();
        (await _shiftRepository.HasOriginalShiftCopyAsync(_order.Id)).ShouldBeTrue();
    }

    [Test]
    public async Task DetectorScans_EqualTheDetectorsFormerGetQueryComposition_OnTheSeededRows()
    {
        var seeded = SeededIds();
        var openScan = new RealPlanShiftScan(ShiftStatus.OriginalOrder) { NotEndedBefore = Today };
        await ShouldEqualAsSetAsync(openScan, LegacyOpenOrders());

        var uncutScan = new RealPlanShiftScan(ShiftStatus.OriginalShift)
        {
            ShiftType = ShiftType.IsTask, FullDayOnly = true, NotEndedBefore = Today,
        };
        await ShouldEqualAsSetAsync(uncutScan, LegacyUncutFullDay());
        var seededFullDay = new List<Guid> { _fullDayOpen.Id, _fullDayEnded.Id, _fullDayEndsToday.Id };
        await ShouldEqualAsSetAsync(
            uncutScan with { OnlyIds = seededFullDay },
            LegacyUncutFullDay().Where(s => seededFullDay.Contains(s.Id)));

        var templateIds = await _containerTemplateRepository.GetContainerIdsWithTemplateAsync();
        var legacyTemplateIds = await _context.ContainerTemplate.AsNoTracking()
            .Where(t => !t.IsDeleted).Select(t => t.ContainerId).Distinct().ToListAsync();
        templateIds.Where(seeded.Contains).ShouldBe(legacyTemplateIds.Where(seeded.Contains), ignoreOrder: true);
        templateIds.ShouldContain(_containerWithTemplate.Id);
        templateIds.ShouldNotContain(_containerDeletedTemplate.Id);

        var containerScan = new RealPlanShiftScan(ShiftStatus.OriginalShift)
        {
            ShiftType = ShiftType.IsContainer, ExcludedIds = templateIds,
        };
        await ShouldEqualAsSetAsync(containerScan, LegacyEmptyContainers(templateIds));
        var seededContainers = new List<Guid> { _containerEmpty.Id, _containerWithTemplate.Id };
        var containerRows = await _shiftReader.ScanRealPlanAsync(containerScan with { OnlyIds = seededContainers });
        containerRows.Select(s => s.Id).ShouldBe([_containerEmpty.Id]);
    }

    [Test]
    public async Task OrderFamilyInGroup_EqualsTheFormerComposition_InRepositoryOrderWithNullsLast()
    {
        var fragment = "bIEL ORDER " + _token.ToUpperInvariant();

        var rows = await _shiftReader.GetOrderFamilyShiftsInGroupAsync(_groupId, fragment);
        var legacy = await _shiftRepository.GetQuery()
            .Where(s => s.GroupItems.Any(gi => gi.GroupId == _groupId && !gi.IsDeleted))
            .Where(s => s.Status == ShiftStatus.SealedOrder
                        || s.Status == ShiftStatus.OriginalShift
                        || s.Status == ShiftStatus.SplitShift)
            .Where(s => EF.Functions.ILike(s.Name, $"%{fragment}%"))
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Abbreviation,
                s.Status,
                s.StartShift,
                s.EndShift,
                s.FromDate,
                s.UntilDate,
                s.OriginalId
            })
            .ToListAsync();

        rows.ShouldBe(legacy
            .Select(s => new OrderFamilyShiftRow(
                s.Id, s.Name, s.Abbreviation, s.Status, s.StartShift, s.EndShift, s.FromDate, s.UntilDate, s.OriginalId))
            .ToList());
        rows.Single(s => s.Id == _partDay.Id).ShouldBe(new OrderFamilyShiftRow(
            _partDay.Id, _partDay.Name, _partDay.Abbreviation, ShiftStatus.SplitShift, _partDay.StartShift, _partDay.EndShift,
            _partDay.FromDate, _partDay.UntilDate, _order.Id));
        rows.Select(s => s.Id).ShouldBe([_partDay.Id, _partNight.Id, _planEarly.Id, _planLate.Id, _order.Id]);
        (await _shiftReader.GetOrderFamilyShiftsInGroupAsync(_groupId, null)).Count.ShouldBe(5);
    }

    [Test]
    public async Task VisibleRealPlanWithClientByIds_EqualsTheFormerComposition()
    {
        var ids = new List<Guid> { _order.Id, _planLate.Id, _partNight.Id, _planEarly.Id, _partDay.Id, _openToday.Id, _openTokenCopy.Id };

        foreach (var visibleGroupIds in new List<Guid>?[] { null, [], [_hiddenGroupId], [_groupId] })
        {
            var rows = await _shiftReader.GetVisibleRealPlanWithClientByIdsAsync(ids, visibleGroupIds);
            var legacyQuery = _context.Shift
                .Include(x => x.Client!)
                .ThenInclude(x => x.Addresses!.Where(a => a != null))
                .OrderBy(x => x.OriginalId)
                .ThenBy(x => x.FromDate)
                .ThenBy(x => x.StartShift)
                .AsNoTracking()
                .Where(s => ids.Contains(s.Id) && s.AnalyseToken == null && s.ScenarioSourceShiftId == null);
            var legacy = await Klacks.Api.Domain.Services.Shifts.ShiftGroupVisibilityFilter.Apply(legacyQuery, visibleGroupIds)
                .ToListAsync();

            rows.Select(s => s.Id).ShouldBe(legacy.Select(s => s.Id));
            rows.Select(s => s.Client?.Addresses.Count ?? 0).ShouldBe(legacy.Select(s => s.Client?.Addresses.Count ?? 0));
        }
    }

    private async Task ShouldEqualAsSetAsync(RealPlanShiftScan scan, IQueryable<Shift> legacy)
    {
        var seeded = SeededIds();
        var keys = (await _shiftReader.ScanRealPlanKeysAsync(scan)).Where(k => seeded.Contains(k.Id)).ToList();
        var rows = (await _shiftReader.ScanRealPlanAsync(scan)).Where(s => seeded.Contains(s.Id)).ToList();
        var legacyKeys = (await legacy.Select(s => new { s.Id, s.FromDate }).ToListAsync())
            .Where(k => seeded.Contains(k.Id)).ToList();
        keys.ShouldNotBeEmpty();

        keys.Select(k => (k.Id, k.FromDate)).ShouldBe(legacyKeys.Select(k => (k.Id, k.FromDate)), ignoreOrder: true);
        rows.Select(s => s.Id).ShouldBe(keys.Select(k => k.Id), ignoreOrder: true);
    }

    private IQueryable<Shift> LegacyOpenOrders() =>
        _shiftRepository.GetQuery()
            .Where(s => s.Status == ShiftStatus.OriginalOrder
                && (s.UntilDate == null || s.UntilDate >= Today)
                && s.AnalyseToken == null
                && s.ScenarioSourceShiftId == null
                && !s.IsDeleted);

    private IQueryable<Shift> LegacyUncutFullDay() =>
        _shiftRepository.GetQuery()
            .Where(s => s.Status == ShiftStatus.OriginalShift
                && s.ShiftType == ShiftType.IsTask
                && s.StartShift == s.EndShift
                && s.AnalyseToken == null
                && s.ScenarioSourceShiftId == null
                && !s.IsDeleted
                && (s.UntilDate == null || s.UntilDate >= Today));

    private IQueryable<Shift> LegacyEmptyContainers(List<Guid> containerIdsWithTemplate) =>
        _shiftRepository.GetQuery()
            .Where(s => s.ShiftType == ShiftType.IsContainer)
            .Where(s => s.Status == ShiftStatus.OriginalShift)
            .Where(s => s.AnalyseToken == null && s.ScenarioSourceShiftId == null)
            .Where(s => !s.IsDeleted)
            .Where(s => !containerIdsWithTemplate.Contains(s.Id));

    private async Task<List<ShiftResource>> ByIdsAsync(List<Guid>? visibleGroupIds, params Guid[] ids)
    {
        var groupFilter = Substitute.For<IShiftGroupFilterService>();
        groupFilter.ResolveShiftListGroupIdsAsync(null).Returns(visibleGroupIds);
        var handler = new GetShiftsByIdsQueryHandler(
            _shiftReader, groupFilter, new ScheduleMapper(), NullLogger<GetShiftsByIdsQueryHandler>.Instance);

        return await handler.Handle(new GetShiftsByIdsQuery(ids.ToList()), CancellationToken.None);
    }

    private async Task<List<JsonElement>> FindSplitCandidatesAsync(string searchString)
    {
        var groupSearch = Substitute.For<IGroupSearchRepository>();
        groupSearch.SearchAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new GroupSearchResult { Items = [new GroupSearchItem { Id = _groupId, Name = Prefix + "Group" }], TotalCount = 1 });
        var skill = new FindSplitShiftCandidatesSkill(_shiftReader, groupSearch);

        var result = await skill.ExecuteAsync(
            new SkillExecutionContext { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), UserName = "planner", UserPermissions = [] },
            new Dictionary<string, object> { ["groupName"] = Prefix + "Group", ["searchString"] = searchString },
            CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        return document.RootElement.GetProperty("Orders").EnumerateArray().Select(o => o.Clone()).ToList();
    }

    private HashSet<Guid> SeededIds() => _context.Shift.IgnoreQueryFilters().AsNoTracking()
        .Where(s => s.Name.StartsWith(Prefix)).Select(s => s.Id).ToHashSet();

    private static string UncutFingerprint(string kind, Shift shift) =>
        AgentConditionLedgerPolicy.FingerprintFor(kind, UncutFullDayShiftTriggerEvent.DedupKeyFor(shift.Id));

    private static string ContainerFingerprint(string kind, Shift shift) =>
        AgentConditionLedgerPolicy.FingerprintFor(kind, EmptyContainerTriggerEvent.DedupKeyFor(shift.Id));

    private async Task SeedAsync()
    {
        var group = new Group { Id = Guid.NewGuid(), Name = Prefix + "Group", Description = Prefix, ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var hiddenGroup = new Group { Id = Guid.NewGuid(), Name = Prefix + "Hidden", Description = Prefix, ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        _groupId = group.Id;
        _hiddenGroupId = hiddenGroup.Id;
        _clientId = Guid.NewGuid();
        var client = new Client
        {
            Id = _clientId,
            Name = Prefix + "Customer",
            FirstName = "Integration",
            Addresses =
            [
                NewAddress("Hauptstrasse 1"),
                NewAddress("Bahnhofplatz 2"),
            ],
        };
        _context.Group.AddRange(group, hiddenGroup);
        _context.Client.Add(client);
        await _context.SaveChangesAsync();

        var family = new DateOnly(2031, 2, 1);
        _order = NewShift($"Biel Order {_token}", ShiftStatus.SealedOrder, family, new TimeOnly(6, 0), _groupId);
        _planLate = NewShift($"Biel Order {_token} PlanLate", ShiftStatus.OriginalShift, family.AddDays(8), new TimeOnly(6, 0), _groupId, _order.Id);
        _planEarly = NewShift($"Biel Order {_token} PlanEarly", ShiftStatus.OriginalShift, family.AddDays(1), new TimeOnly(6, 0), _groupId, _order.Id);
        _partNight = NewShift($"Biel Order {_token} PartNight", ShiftStatus.SplitShift, family, new TimeOnly(19, 0), _groupId, _order.Id);
        _partDay = NewShift($"Biel Order {_token} PartDay", ShiftStatus.SplitShift, family, new TimeOnly(7, 0), _groupId, _order.Id);
        foreach (var shift in new[] { _order, _planLate, _planEarly, _partNight, _partDay })
        {
            shift.ClientId = _clientId;
            shift.EndShift = shift.StartShift.AddHours(12);
        }

        _unlinkedOrder = NewShift($"Biel Order {_token} unlinked", ShiftStatus.SealedOrder, family, new TimeOnly(6, 0), _groupId);
        _unlinkedOrder.GroupItems.Single().IsDeleted = true;
        _unlinkedOrder.EndShift = new TimeOnly(18, 0);

        var tieBytes = Guid.NewGuid().ToByteArray();
        _openToday = NewShift("OpenToday", ShiftStatus.OriginalOrder, Today, new TimeOnly(8, 0));
        _openLaterHighId = NewShift("OpenLaterHigh", ShiftStatus.OriginalOrder, Today.AddDays(2), new TimeOnly(8, 0), id: WithLastByte(tieBytes, 0xF0));
        _openLaterLowId = NewShift("OpenLaterLow", ShiftStatus.OriginalOrder, Today.AddDays(2), new TimeOnly(8, 0), id: WithLastByte(tieBytes, 0x10));
        _openPast = NewShift("OpenPast", ShiftStatus.OriginalOrder, Today.AddDays(-1), new TimeOnly(8, 0));
        _openTokenCopy = NewShift("OpenTokenCopy", ShiftStatus.OriginalOrder, Today.AddDays(3), new TimeOnly(8, 0));
        _openTokenCopy.AnalyseToken = Guid.NewGuid();
        _openSourceLinked = NewShift("OpenSourceLinked", ShiftStatus.OriginalOrder, Today.AddDays(3), new TimeOnly(8, 0));
        _openSourceLinked.ScenarioSourceShiftId = _openToday.Id;
        _openDeleted = NewShift("OpenDeleted", ShiftStatus.OriginalOrder, Today.AddDays(3), new TimeOnly(8, 0));
        _openDeleted.IsDeleted = true;
        _openEnded = NewShift("OpenEnded", ShiftStatus.OriginalOrder, Today.AddDays(-5), new TimeOnly(8, 0));
        _openEnded.UntilDate = Today.AddDays(-1);

        _fullDayOpen = NewShift("FullDayOpen", ShiftStatus.OriginalShift, Today.AddDays(10), new TimeOnly(7, 0));
        _fullDayEndsToday = NewShift("FullDayEndsToday", ShiftStatus.OriginalShift, Today.AddDays(-30), new TimeOnly(7, 0));
        _fullDayEndsToday.UntilDate = Today;
        _fullDayEnded = NewShift("FullDayEnded", ShiftStatus.OriginalShift, Today.AddDays(-30), new TimeOnly(7, 0));
        _fullDayEnded.UntilDate = Today.AddDays(-1);
        _fullDayUnequalTimes = NewShift("FullDayUnequal", ShiftStatus.OriginalShift, Today.AddDays(10), new TimeOnly(7, 0));
        _fullDayUnequalTimes.EndShift = new TimeOnly(19, 0);
        _fullDaySplit = NewShift("FullDaySplit", ShiftStatus.SplitShift, Today.AddDays(10), new TimeOnly(7, 0));
        _fullDayContainer = NewShift("FullDayContainer", ShiftStatus.OriginalShift, Today.AddDays(10), new TimeOnly(7, 0));
        _fullDayContainer.ShiftType = ShiftType.IsContainer;
        _fullDayTokenCopy = NewShift("FullDayTokenCopy", ShiftStatus.OriginalShift, Today.AddDays(10), new TimeOnly(7, 0));
        _fullDayTokenCopy.AnalyseToken = Guid.NewGuid();

        _containerEmpty = NewContainer("ContainerEmpty");
        _containerWithTemplate = NewContainer("ContainerWithTemplate");
        _containerDeletedTemplate = NewContainer("ContainerDeletedTemplate");

        _context.Shift.AddRange(
            _order, _planLate, _planEarly, _partNight, _partDay, _unlinkedOrder,
            _openToday, _openLaterHighId, _openLaterLowId, _openPast, _openTokenCopy, _openSourceLinked, _openDeleted, _openEnded,
            _fullDayOpen, _fullDayEndsToday, _fullDayEnded, _fullDayUnequalTimes, _fullDaySplit, _fullDayContainer, _fullDayTokenCopy,
            _containerEmpty, _containerWithTemplate, _containerDeletedTemplate);
        await _context.SaveChangesAsync();

        _context.ContainerTemplate.AddRange(
            NewTemplate(_containerWithTemplate.Id, isDeleted: false),
            NewTemplate(_containerDeletedTemplate.Id, isDeleted: true));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Shift NewContainer(string suffix)
    {
        var container = NewShift(suffix, ShiftStatus.OriginalShift, Today.AddYears(-6), new TimeOnly(8, 0));
        container.ShiftType = ShiftType.IsContainer;
        container.EndShift = new TimeOnly(16, 0);
        return container;
    }

    private static ContainerTemplate NewTemplate(Guid containerId, bool isDeleted) => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = containerId,
        FromTime = new TimeOnly(8, 0),
        UntilTime = new TimeOnly(16, 0),
        Weekday = 1,
        IsDeleted = isDeleted,
    };

    private Address NewAddress(string street) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _clientId,
        Street = street,
        Zip = "2502",
        City = "Biel",
        Country = "CH",
        ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Shift NewShift(
        string suffix, ShiftStatus status, DateOnly fromDate, TimeOnly start, Guid? groupId = null, Guid? originalId = null, Guid? id = null)
    {
        var shiftId = id ?? Guid.NewGuid();
        return new Shift
        {
            Id = shiftId,
            Name = Prefix + suffix,
            Abbreviation = "SRS",
            Description = Prefix,
            Status = status,
            ShiftType = ShiftType.IsTask,
            OriginalId = originalId,
            FromDate = fromDate,
            StartShift = start,
            EndShift = start,
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            Quantity = 1,
            SumEmployees = 1,
            GroupItems = groupId is { } g
                ? [new GroupItem { Id = Guid.NewGuid(), GroupId = g, ShiftId = shiftId }]
                : [],
        };
    }

    private static Guid WithLastByte(byte[] template, byte last)
    {
        var bytes = (byte[])template.Clone();
        bytes[15] = last;
        return new Guid(bytes);
    }

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM container_template WHERE container_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM client WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
