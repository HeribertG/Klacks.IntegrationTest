// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins, against real PostgreSQL, the group readers that used to hand out rows of a key or a scenario: the groups with
/// members count real rows only, the shift ids of a group hold real links only, the group list loads no tokened group item
/// on the exact and on the fuzzy path, and the group search applies its root scope before the cap and in the fuzzy
/// fallback. Every row carries the INTEGRATION_TEST_GroupTokened_ prefix and the cleanup deletes exactly those.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.ReadModels;

[TestFixture]
[Category("RealDatabase")]
public class GroupTokenedRowsReadTests
{
    private const string Prefix = "INTEGRATION_TEST_GroupTokened_";
    private const string FuzzyGroupName = "IntegrationTestGroupTokenedQuarzsand";
    private const string MisheardFuzzyName = "IntegrationTestGroupTokenedQuarzsant";
    private const int SearchCap = 3;
    private const int HiddenRootCount = 5;
    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private DataBaseContext _context = null!;
    private GroupRepository _groupRepository = null!;
    private GroupItemRepository _groupItemRepository = null!;
    private GroupSearchRepository _groupSearchRepository = null!;

    private Group _mixed = null!;
    private Group _keyOnly = null!;
    private Group _scenarioOnly = null!;
    private Group _fuzzy = null!;
    private Client _member = null!;
    private Shift _real = null!;
    private Shift _key = null!;
    private Shift _clone = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();

        var facade = Substitute.For<IGroupServiceFacade>();
        facade.SearchService.Returns(new GroupSearchService(
            Substitute.For<ILogger<GroupSearchService>>(),
            new GroupValidityService(_context, Substitute.For<ILogger<GroupValidityService>>())));
        _groupRepository = new GroupRepository(
            _context, facade, Substitute.For<IGroupCacheService>(), Substitute.For<ILogger<Group>>(), TestCompanyClock.Utc(),
            Substitute.For<IGroupVisibilityPreservationService>());
        _groupItemRepository = new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>());
        _groupSearchRepository = new GroupSearchRepository(_context);

        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task GroupsWithMembers_CountRealRowsOnly()
    {
        var ids = await _groupRepository.GetGroupIdsWithMembersAsync();

        ids.ShouldContain(_mixed.Id);
        ids.ShouldNotContain(_keyOnly.Id);
        ids.ShouldNotContain(_scenarioOnly.Id);
    }

    [Test]
    public async Task ShiftIdsOfAGroup_AreItsRealLinksOnly()
    {
        var ids = await _groupItemRepository.GetShiftIdsByGroupIds([_mixed.Id, _keyOnly.Id]);

        ids.ShouldBe(new[] { _real.Id });
    }

    [Test]
    public async Task GroupList_LoadsNoTokenedGroupItem()
    {
        var result = await _groupRepository.Truncated(NewFilter(_mixed.Name));

        var group = result.Groups!.Single(g => g.Id == _mixed.Id);
        group.GroupItems.Select(item => item.ShiftId).ShouldBe(new Guid?[] { _real.Id, null }, ignoreOrder: true);
    }

    [Test]
    public async Task GroupList_LoadsNoTokenedGroupItem_OnTheFuzzyPathEither()
    {
        var result = await _groupRepository.Truncated(NewFilter(MisheardFuzzyName));

        var group = result.Groups!.Single(g => g.Id == _fuzzy.Id);
        group.GroupItems.Select(item => item.ShiftId).ShouldBe(new Guid?[] { _real.Id });
    }

    [Test]
    public async Task GroupSearch_AppliesTheRootScopeBeforeTheCap()
    {
        var hiddenRoots = new List<Group>();
        for (var i = 0; i < HiddenRootCount; i++)
        {
            hiddenRoots.Add(NewGroup("AHidden" + i));
        }

        var visibleRoot = NewGroup("ZVisible");
        var visibleChild = NewGroup("ZVisibleChild");
        visibleChild.Root = visibleRoot.Id;
        visibleChild.Parent = visibleRoot.Id;
        _context.Group.AddRange(hiddenRoots);
        _context.Group.AddRange(visibleRoot, visibleChild);
        await _context.SaveChangesAsync();

        var scoped = await _groupSearchRepository.SearchWithinRootsAsync(Prefix, SearchCap, [visibleRoot.Id]);

        scoped.TotalCount.ShouldBe(2);
        scoped.Items.Select(item => item.Id).ShouldBe(new[] { visibleRoot.Id, visibleChild.Id });

        var empty = await _groupSearchRepository.SearchWithinRootsAsync(Prefix, SearchCap, []);
        empty.TotalCount.ShouldBe(0);
        empty.Items.ShouldBeEmpty();

        var open = await _groupSearchRepository.SearchWithinRootsAsync(Prefix, SearchCap, null);
        open.TotalCount.ShouldBeGreaterThan(SearchCap);
        open.Items.Count.ShouldBe(SearchCap);
    }

    [Test]
    public async Task GroupSearch_AppliesTheRootScopeInTheFuzzyFallback()
    {
        var hidden = await _groupSearchRepository.SearchWithinRootsAsync(MisheardFuzzyName, SearchCap, [_mixed.Id]);
        var visible = await _groupSearchRepository.SearchWithinRootsAsync(MisheardFuzzyName, SearchCap, [_fuzzy.Id]);

        hidden.Items.ShouldBeEmpty();
        visible.Items.Select(item => item.Id).ShouldBe(new[] { _fuzzy.Id });
    }

    private static GroupFilter NewFilter(string searchString) => new()
    {
        SearchString = searchString,
        ActiveDateRange = true,
        FormerDateRange = true,
        FutureDateRange = true,
        NumberOfItemsPerPage = 50,
        OrderBy = "name",
        SortOrder = "asc"
    };

    private async Task SeedAsync()
    {
        _mixed = NewGroup("Mixed");
        _keyOnly = NewGroup("KeyOnly");
        _scenarioOnly = NewGroup("ScenarioOnly");
        _fuzzy = NewGroup(string.Empty);
        _fuzzy.Name = FuzzyGroupName;
        _context.Group.AddRange(_mixed, _keyOnly, _scenarioOnly, _fuzzy);

        _member = new Client { Id = Guid.NewGuid(), Name = Prefix + "Member", FirstName = "Integration", Type = EntityTypeEnum.Employee };
        _context.Client.Add(_member);

        _real = NewShift("real", null, null);
        _key = NewShift("key", Guid.NewGuid(), null);
        _context.Shift.AddRange(_real, _key);
        await _context.SaveChangesAsync();
        _clone = NewShift("clone", Guid.NewGuid(), _real.Id);
        _context.Shift.Add(_clone);

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _member.Id, GroupId = _mixed.Id },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _real.Id, GroupId = _mixed.Id },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _key.Id, GroupId = _mixed.Id, AnalyseToken = _key.AnalyseToken },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _clone.Id, GroupId = _mixed.Id, AnalyseToken = _clone.AnalyseToken },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _key.Id, GroupId = _keyOnly.Id, AnalyseToken = _key.AnalyseToken },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _member.Id, GroupId = _scenarioOnly.Id, AnalyseToken = Guid.NewGuid() },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _real.Id, GroupId = _fuzzy.Id },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _key.Id, GroupId = _fuzzy.Id, AnalyseToken = _key.AnalyseToken });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Group NewGroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Description = Prefix,
        ValidFrom = Past
    };

    private static Shift NewShift(string suffix, Guid? token, Guid? sourceId) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Abbreviation = "T",
        Description = Prefix,
        Status = ShiftStatus.OriginalShift,
        ShiftType = ShiftType.IsTask,
        FromDate = new DateOnly(2031, 1, 1),
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        AfterShift = new TimeOnly(0, 0),
        BeforeShift = new TimeOnly(0, 0),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 1,
        AnalyseToken = token,
        ScenarioSourceShiftId = sourceId
    };

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}') OR name = '{FuzzyGroupName}');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE starts_with(name, '{Prefix}');
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM client WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}') OR name = '{FuzzyGroupName}';
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
