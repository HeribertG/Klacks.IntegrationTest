// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Handlers.ClientShiftPreferences;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.ClientShiftPreferences;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.ReadModels;

/// <summary>
/// Pins, against real PostgreSQL, the group-item reads of the Application layer: the shifts a client may prefer
/// (links of the client's group tree; each shift once, by name; deleted links and deleted shifts dropped, scenario
/// links kept), the member count per group of analyze_group_semantics (real client rows; scenario, shift, deleted
/// rows dropped; ended memberships counted) and the link remove_shift_from_group deletes (the live link of the
/// asked shift and group), and compares each repository read with the former GetQuery() composition. Every shift, client and group carries the INTEGRATION_TEST_GroupItemReads_ prefix and
/// the cleanup deletes exactly those and their group items.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class GroupItemReadShapesTests
{
    private const string Prefix = "INTEGRATION_TEST_GroupItemReads_";
    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private DataBaseContext _context = null!;
    private GroupItemRepository _groupItemRepository = null!;

    private Group _root = null!;
    private Group _child = null!;
    private Group _foreign = null!;
    private Group _empty = null!;
    private Client _member = null!;
    private readonly List<Client> _counted = [];

    private Shift _alpha = null!;
    private Shift _bravo = null!;
    private Shift _charlieScenarioLink = null!;
    private Shift _deletedShift = null!;
    private Shift _foreignShift = null!;
    private Shift _deletedLinkShift = null!;
    private Guid _bravoChildLinkId;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        _groupItemRepository = new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>());
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task AvailableShifts_AreTheTreeLinks_EachShiftOnce_ByName_WithoutDeletedLinksOrShifts()
    {
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = new GetAvailableShiftsQueryHandler(
            _groupItemRepository, guard, NullLogger<GetAvailableShiftsQueryHandler>.Instance);

        var result = await handler.Handle(new GetAvailableShiftsQuery(_member.Id), CancellationToken.None);

        result.Select(s => s.Id).ShouldBe([_alpha.Id, _bravo.Id, _charlieScenarioLink.Id]);
        result.Select(s => s.Name).ShouldBe([_alpha.Name, _bravo.Name, _charlieScenarioLink.Name]);
        result.Select(s => s.Abbreviation).ShouldBe(["A", "B", "C"]);
    }

    [Test]
    public async Task MemberCounts_CountRealClientRows_IncludingEndedOnes()
    {
        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(new List<Group> { _root, _child, _empty });
        var clientRepository = Substitute.For<IClientRepository>();
        clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());
        clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>()).Returns(new List<Client>());
        var qualificationRepository = Substitute.For<IQualificationRepository>();
        qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Qualification>());
        var contractRepository = Substitute.For<IContractRepository>();
        contractRepository.List().Returns(new List<Contract>());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StateResource>().AsEnumerable());
        var scopeGuard = Substitute.For<IGroupScopeGuard>();
        scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());

        var skill = new AnalyzeGroupSemanticsSkill(
            groupRepository, scopeGuard, _groupItemRepository, clientRepository, contractRepository, qualificationRepository, mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        var counts = ((AnalyzeGroupSemanticsResult)result.Data!).GroupMemberCounts
            .ToDictionary(c => c.GroupId, c => c.ActiveMemberCount);
        counts[_root.Id].ShouldBe(2);
        counts[_child.Id].ShouldBe(1);
        counts[_empty.Id].ShouldBe(0);
    }

    [Test]
    public async Task RemoveShiftFromGroup_DeletesTheLiveLinkOfTheAskedShiftAndGroup()
    {
        var (skill, routes) = RemoveSkill(_bravo.Id, _child);

        var result = await skill.ExecuteAsync(Ctx(), RemoveParams(_bravo.Id, _child.Id));

        result.Success.ShouldBeTrue(result.Message);
        routes.ShouldBe([$"api/backend/GroupItems/{_bravoChildLinkId}"]);
    }

    [Test]
    public async Task RemoveShiftFromGroup_WithOnlyADeletedLink_IsRejected()
    {
        var (skill, routes) = RemoveSkill(_deletedLinkShift.Id, _root);

        var result = await skill.ExecuteAsync(Ctx(), RemoveParams(_deletedLinkShift.Id, _root.Id));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not linked");
        routes.ShouldBeEmpty();
    }

    [Test]
    public async Task RepositoryReads_EqualTheFormerGetQueryComposition()
    {
        var treeIds = new List<Guid> { _root.Id, _child.Id };
        var linked = await _groupItemRepository.GetLinkedShiftNamesAsync(treeIds);
        var legacyLinked = await _context.GroupItem
            .Include(gi => gi.Shift)
            .Where(gi => treeIds.Contains(gi.GroupId) && gi.ShiftId != null)
            .Where(gi => gi.Shift != null)
            .Select(gi => new { gi.Shift!.Id, gi.Shift.Name, gi.Shift.Abbreviation })
            .Distinct()
            .OrderBy(s => s.Name)
            .ToListAsync();
        linked.ShouldBe(legacyLinked.Select(s => new LinkedShiftName(s.Id, s.Name, s.Abbreviation)).ToList());

        var groupIds = new List<Guid> { _root.Id, _child.Id, _foreign.Id, _empty.Id };
        var counts = await _groupItemRepository.CountRealClientRowsPerGroupAsync(groupIds);
        var legacyCounts = (await _context.GroupItem
                .Where(gi => groupIds.Contains(gi.GroupId) && gi.ClientId != null && gi.AnalyseToken == null)
                .Select(gi => gi.GroupId)
                .ToListAsync())
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
        counts.ShouldBe(legacyCounts, ignoreOrder: true);
        counts.ContainsKey(_empty.Id).ShouldBeFalse();

        var linkId = await _groupItemRepository.FindShiftLinkIdAsync(_bravo.Id, _child.Id);
        var legacyLink = await _context.GroupItem
            .FirstOrDefaultAsync(gi => gi.ShiftId == _bravo.Id && gi.GroupId == _child.Id);
        linkId.ShouldBe(legacyLink!.Id);
        (await _groupItemRepository.FindShiftLinkIdAsync(_deletedLinkShift.Id, _root.Id)).ShouldBeNull();
    }

    private (RemoveShiftFromGroupSkill Skill, List<string> Routes) RemoveSkill(Guid shiftId, Group group)
    {
        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Exists(shiftId).Returns(true);
        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.Get(group.Id).Returns(group);
        var scopeGuard = Substitute.For<IGroupScopeGuard>();
        scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());

        var routes = new List<string>();
        var selfApi = Substitute.For<IKlacksSelfApiClient>();
        selfApi.DeleteAsync<GroupItemResource>(
                Arg.Do<string>(routes.Add), Arg.Any<SkillExecutionContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SelfApiResult<GroupItemResource>(true, new GroupItemResource(), 200, null));

        var skill = new RemoveShiftFromGroupSkill(
            shiftRepository, groupRepository, scopeGuard, _groupItemRepository, selfApi, new SelfApiRouteResolver());
        return (skill, routes);
    }

    private static Dictionary<string, object> RemoveParams(Guid shiftId, Guid groupId) => new()
    {
        ["shiftId"] = shiftId.ToString(),
        ["groupId"] = groupId.ToString(),
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = [],
    };

    private async Task SeedAsync()
    {
        _root = NewGroup("Root");
        _child = NewGroup("Child");
        _child.Root = _root.Id;
        _child.Parent = _root.Id;
        _foreign = NewGroup("Foreign");
        _empty = NewGroup("Empty");
        _context.Group.AddRange(_root, _child, _foreign, _empty);

        _member = NewClient("Member");
        _counted.Clear();
        for (var i = 0; i < 6; i++)
        {
            _counted.Add(NewClient("Counted" + i));
        }

        _context.Client.Add(_member);
        _context.Client.AddRange(_counted);

        _alpha = NewShift("alpha", "A");
        _bravo = NewShift("bravo", "B");
        _charlieScenarioLink = NewShift("charlie", "C");
        _deletedShift = NewShift("delta", "D");
        _deletedShift.IsDeleted = true;
        _foreignShift = NewShift("echo", "E");
        _deletedLinkShift = NewShift("foxtrot", "F");
        _context.Shift.AddRange(_alpha, _bravo, _charlieScenarioLink, _deletedShift, _foreignShift, _deletedLinkShift);
        await _context.SaveChangesAsync();

        _bravoChildLinkId = Guid.NewGuid();
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _member.Id, GroupId = _child.Id },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _bravo.Id, GroupId = _root.Id },
            new GroupItem { Id = _bravoChildLinkId, ShiftId = _bravo.Id, GroupId = _child.Id },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _alpha.Id, GroupId = _child.Id },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _charlieScenarioLink.Id, GroupId = _root.Id, AnalyseToken = Guid.NewGuid() },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _deletedShift.Id, GroupId = _root.Id },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _foreignShift.Id, GroupId = _foreign.Id },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = _deletedLinkShift.Id, GroupId = _root.Id, IsDeleted = true },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _counted[0].Id, GroupId = _root.Id },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _counted[1].Id, GroupId = _root.Id, ValidFrom = Past, ValidUntil = Past.AddYears(1) },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _counted[2].Id, GroupId = _root.Id, AnalyseToken = Guid.NewGuid() },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _counted[3].Id, GroupId = _root.Id, IsDeleted = true },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _counted[4].Id, GroupId = _foreign.Id });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Group NewGroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Description = Prefix,
        ValidFrom = Past,
    };

    private static Client NewClient(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        FirstName = "Integration",
        Type = EntityTypeEnum.Employee,
    };

    private static Shift NewShift(string suffix, string abbreviation) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Abbreviation = abbreviation,
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
    };

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM client WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
