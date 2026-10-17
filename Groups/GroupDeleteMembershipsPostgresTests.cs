// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

// Verifies against a real PostgreSQL database (IT database klacks) that deleting a group through the production
// nested-set adapter (raw SQL) also soft-deletes the group_item rows of the deleted subtree - client memberships and
// shift assignments - stamping deleted_time and current_user_deleted, while items of other groups stay untouched. A
// person whose only group was deleted then counts as group-less and is therefore visible to a group-restricted
// Supervisor; before the fix the dangling membership hid the person from every non-admin. Uses the
// SupervisorVisibilityScenario fixture (real UserService, GroupVisibilityService, ClientGroupFilterService and
// ClientVisibilityGuard). Every row carries the INTEGRATION_TEST_GROUPDEL_ prefix and the cleanup removes only those.

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Persistence.Adapters;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.Groups;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class GroupDeleteMembershipsPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_GROUPDEL_";

    private DataBaseContext _context = null!;
    private SupervisorVisibilityScenario _scenario = null!;
    private GroupTreeService _treeService = null!;
    private Group _keptHiddenGroup = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        _scenario = await SupervisorVisibilityScenario.CreateAsync(TestPrefix, _context);

        _keptHiddenGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "KEPT_HIDDEN",
            Description = "Group delete membership integration test",
            ValidFrom = DateTime.UtcNow.AddYears(-1)
        };
        _context.Group.Add(_keptHiddenGroup);
        await _context.SaveChangesAsync();

        _treeService = new GroupTreeService(
            _context, Substitute.For<ILogger<GroupTreeService>>(), new GroupTreeProductionAdapter(_context));
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task DeletingTheOnlyGroup_SoftDeletesItsItems_AndTheMemberBecomesVisibleToARestrictedUser()
    {
        var keptHiddenClient = await _scenario.CreateClientAsync("KEPT_HIDDEN_CLIENT", _keptHiddenGroup.Id);
        var shiftItemId = await AddShiftItemAsync(_scenario.HiddenGroup.Id);
        var supervisorId = Guid.Parse(_scenario.Supervisor.Id);

        using (ExecutionPrincipal.Begin(supervisorId))
        {
            (await _scenario.Guard.IsVisibleAsync(_scenario.HiddenClient.Id)).ShouldBeFalse();
        }

        await _treeService.DeleteNodeAsync(_scenario.HiddenGroup.Id);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var hiddenGroupItems = await _context.GroupItem.IgnoreQueryFilters().AsNoTracking()
            .Where(gi => gi.GroupId == _scenario.HiddenGroup.Id)
            .ToListAsync();
        hiddenGroupItems.Count.ShouldBe(2);
        hiddenGroupItems.Select(gi => gi.Id).ShouldContain(shiftItemId);
        hiddenGroupItems.ShouldAllBe(gi => gi.IsDeleted && gi.DeletedTime != null && gi.CurrentUserDeleted != null);

        (await _context.GroupItem.AsNoTracking().CountAsync(gi => gi.GroupId == _scenario.VisibleGroup.Id)).ShouldBe(1);
        (await _context.GroupItem.AsNoTracking().CountAsync(gi => gi.GroupId == _keptHiddenGroup.Id)).ShouldBe(1);

        using (ExecutionPrincipal.Begin(supervisorId))
        {
            (await _scenario.Guard.IsVisibleAsync(_scenario.HiddenClient.Id)).ShouldBeTrue();
            (await _scenario.Guard.IsVisibleAsync(_scenario.VisibleClient.Id)).ShouldBeTrue();
            (await _scenario.Guard.IsVisibleAsync(keptHiddenClient.Id)).ShouldBeFalse();
        }
    }

    private async Task<Guid> AddShiftItemAsync(Guid groupId)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TST",
            Description = "group delete membership",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        };
        var item = new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = shift.Id };
        await _context.Shift.AddAsync(shift);
        await _context.GroupItem.AddAsync(item);
        await _context.SaveChangesAsync();
        return item.Id;
    }

    private async Task CleanupAsync()
    {
        await SupervisorVisibilityScenario.CleanupAsync(_context, TestPrefix);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, {0}))", TestPrefix);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE starts_with(name, {0})", TestPrefix);
    }
}
