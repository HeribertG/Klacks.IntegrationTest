// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Runs the grouping plan applier and the feasibility data source against the real integration
/// database: a new group is created through the nested-set repository, a shift and a client are added,
/// a dead membership is soft-deleted, all in one transaction; the data source then sees the result.
/// Every row carries the INTEGRATION_TEST_GROUPING_ prefix and only those rows are cleaned up.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Grouping;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class GroupingPlanApplierIntegrationTests
{
    private const string Prefix = "INTEGRATION_TEST_GROUPING_";
    private const string CleanupSql = @"
        DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE 'INTEGRATION\_TEST\_GROUPING\_%')
            OR client_id IN (SELECT id FROM client WHERE name LIKE 'INTEGRATION\_TEST\_GROUPING\_%')
            OR shift_id IN (SELECT id FROM shift WHERE name LIKE 'INTEGRATION\_TEST\_GROUPING\_%');
        DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE name LIKE 'INTEGRATION\_TEST\_GROUPING\_%');
        DELETE FROM shift WHERE name LIKE 'INTEGRATION\_TEST\_GROUPING\_%';
        DELETE FROM client WHERE name LIKE 'INTEGRATION\_TEST\_GROUPING\_%';
        DELETE FROM group_visibility WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE 'INTEGRATION\_TEST\_GROUPING\_%');
        DELETE FROM ""group"" WHERE name LIKE 'INTEGRATION\_TEST\_GROUPING\_%';";

    private static readonly DateTime ValidFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private SignalRTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();
        _ = _factory.Services;
        await CleanupAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await CleanupAsync();
        _factory?.Dispose();
    }

    private async Task CleanupAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        await context.Database.ExecuteSqlRawAsync(CleanupSql);
    }

    [Test]
    public async Task Apply_CreatesGroupAddsBeforeRemoving_AndDataSourceSeesTheResult()
    {
        Guid oldGroupId;
        Guid homeGroupId;
        Guid clientId;
        Guid shiftId;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<DataBaseContext>();
            var oldGroup = new Group { Id = Guid.NewGuid(), Name = Prefix + "OLD", Description = string.Empty, ValidFrom = ValidFrom };
            var homeGroup = new Group { Id = Guid.NewGuid(), Name = Prefix + "HOME", Description = string.Empty, ValidFrom = ValidFrom };
            var client = new Client
            {
                Id = Guid.NewGuid(), Name = Prefix + "CLIENT", FirstName = "Anna", Company = string.Empty,
                LegalEntity = false, Type = EntityTypeEnum.Employee
            };
            var shift = new Shift
            {
                Id = Guid.NewGuid(), Name = Prefix + "SHIFT", Abbreviation = "IT", Description = string.Empty,
                Status = ShiftStatus.OriginalShift, ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 8m,
                FromDate = new DateOnly(2026, 1, 1), StartShift = new TimeOnly(6, 0), EndShift = new TimeOnly(14, 0),
                IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true
            };
            context.Group.AddRange(oldGroup, homeGroup);
            context.Client.Add(client);
            context.Membership.Add(new Membership { Id = Guid.NewGuid(), ClientId = client.Id, ValidFrom = ValidFrom });
            context.Shift.Add(shift);
            context.GroupItem.AddRange(
                new GroupItem { Id = Guid.NewGuid(), GroupId = oldGroup.Id, ClientId = client.Id, ValidFrom = ValidFrom },
                new GroupItem { Id = Guid.NewGuid(), GroupId = homeGroup.Id, ClientId = client.Id, ValidFrom = ValidFrom });
            await context.SaveChangesAsync();
            (oldGroupId, homeGroupId, clientId, shiftId) = (oldGroup.Id, homeGroup.Id, client.Id, shift.Id);
        }

        GroupingApplyResult result;
        using (var applyScope = _factory.Services.CreateScope())
        {
            var applier = applyScope.ServiceProvider.GetRequiredService<IGroupingPlanApplier>();
            result = await applier.ApplyAsync(new GroupingApplyCommand(
            [
                new GroupingProposal(GroupingProposalKind.CreateGroup, null, GroupingFeasibilityDefaults.NewGroupKey, null, null, GroupingFindingCode.ShiftWithoutGroup),
                new GroupingProposal(GroupingProposalKind.AddShift, null, GroupingFeasibilityDefaults.NewGroupKey, null, shiftId, GroupingFindingCode.ShiftWithoutGroup),
                new GroupingProposal(GroupingProposalKind.AddClient, null, GroupingFeasibilityDefaults.NewGroupKey, clientId, null, GroupingFindingCode.ClientWithoutGroup),
                new GroupingProposal(GroupingProposalKind.RemoveClient, oldGroupId, null, clientId, null, GroupingFindingCode.ClientDeadMembership),
            ], ValidFrom, Prefix + "NEW", "integration-test"), CancellationToken.None);
        }

        result.CreatedGroups.ShouldBe(1);
        result.AddedShifts.ShouldBe(1);
        result.AddedClients.ShouldBe(1);
        result.RemovedClients.ShouldBe(1);

        using var verifyScope = _factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var newGroup = await verify.Group.AsNoTracking().SingleAsync(g => g.Name == Prefix + "NEW");
        newGroup.Rgt.ShouldBeGreaterThan(newGroup.Lft);
        (await verify.GroupItem.AsNoTracking().CountAsync(gi => gi.GroupId == newGroup.Id)).ShouldBe(2);
        (await verify.GroupItem.AsNoTracking().AnyAsync(gi => gi.GroupId == oldGroupId && gi.ClientId == clientId)).ShouldBeFalse();
        (await verify.GroupItem.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(gi => gi.GroupId == oldGroupId && gi.ClientId == clientId && gi.IsDeleted)).ShouldBeTrue();
        (await verify.GroupItem.AsNoTracking().AnyAsync(gi => gi.GroupId == homeGroupId && gi.ClientId == clientId)).ShouldBeTrue();

        var dataSource = verifyScope.ServiceProvider.GetRequiredService<IGroupingFeasibilityDataSource>();
        var snapshot = await dataSource.LoadAsync(new DateOnly(2026, 9, 28), new DateOnly(2026, 11, 23), new DateOnly(2026, 9, 27), CancellationToken.None);
        snapshot.Shifts.ShouldContain(shift => shift.Id == shiftId);
        snapshot.Clients.ShouldContain(client => client.Id == clientId && client.DisplayName == "Anna " + Prefix + "CLIENT");
        snapshot.Memberships.ShouldContain(m => m.GroupId == newGroup.Id && m.ShiftId == shiftId);
        snapshot.Memberships.ShouldNotContain(m => m.GroupId == oldGroupId && m.ClientId == clientId);
    }
}
