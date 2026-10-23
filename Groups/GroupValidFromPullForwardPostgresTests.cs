// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

// Verifies the entry-date pull-forward writes against a real PostgreSQL database, without a host and without any background
// service: GroupRepository.LowerValidFromAsync (an ExecuteUpdate that EF InMemory cannot run) lowers a group and all its
// ancestors along the Parent chain, never raises a start, leaves siblings alone and stamps UpdateTime and
// CurrentUserUpdated; GroupItemRepository.PullForwardValidFromAsync moves only later starts; a rollback of the transaction
// that holds both restores the memberships and the group starts. Seeds its own INTEGRATION_TEST_ prefixed rows and
// removes only those.

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Klacks.Api.Infrastructure.Services.Groups;

namespace Klacks.IntegrationTest.Groups;

[TestFixture]
[Category("RealDatabase")]
public class GroupValidFromPullForwardPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PULLFWD_";

    private static readonly DateTime Late = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Entry = new(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _rootId = Guid.NewGuid();
    private readonly Guid _midId = Guid.NewGuid();
    private readonly Guid _leafId = Guid.NewGuid();
    private readonly Guid _siblingId = Guid.NewGuid();
    private readonly Guid _earlyRootId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _leafItemId = Guid.NewGuid();
    private readonly Guid _endedItemId = Guid.NewGuid();
    private readonly Guid _openItemId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private GroupRepository _groups = null!;
    private GroupItemRepository _items = null!;

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [SetUp]
    public async Task Setup()
    {
        _context = NewContext();
        _groups = new GroupRepository(
            _context, Substitute.For<IGroupServiceFacade>(), Substitute.For<IGroupCacheService>(),
            Substitute.For<ILogger<Group>>(), Substitute.For<ICompanyClock>(),
            Substitute.For<IGroupVisibilityPreservationService>());
        _items = new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>());

        await CleanupAsync();
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        _context.Dispose();
    }

    [Test]
    public async Task LowerValidFromAsync_LowersTheGroupAndEveryAncestor_AndStampsTheAudit()
    {
        var changed = await _groups.LowerValidFromAsync(new Dictionary<Guid, DateTime> { [_leafId] = Entry });

        changed.ShouldBe(3);
        var starts = await StartsAsync();
        starts[_leafId].ShouldBe(Entry);
        starts[_midId].ShouldBe(Entry);
        starts[_rootId].ShouldBe(Entry);
        starts[_siblingId].ShouldBe(Late);
        starts[_earlyRootId].ShouldBe(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await using var check = NewContext();
        var stamped = await check.Group.AsNoTracking().Where(g => g.Id == _leafId || g.Id == _rootId).ToListAsync();
        stamped.ShouldAllBe(g => g.UpdateTime != null && g.CurrentUserUpdated == _context.CurrentAuditActorName());
        (await check.Group.AsNoTracking().SingleAsync(g => g.Id == _siblingId)).UpdateTime.ShouldBeNull();
    }

    [Test]
    public async Task LowerValidFromAsync_NeverRaisesAStart_AndANoOpChangesNothing()
    {
        (await _groups.LowerValidFromAsync(new Dictionary<Guid, DateTime> { [_leafId] = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) }))
            .ShouldBe(0);
        (await _groups.LowerValidFromAsync(new Dictionary<Guid, DateTime> { [_earlyRootId] = Entry })).ShouldBe(0);

        var starts = await StartsAsync();
        starts[_leafId].ShouldBe(Late);
        starts[_earlyRootId].ShouldBe(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task APullForwardAndAGroupLowering_AreBothRolledBackWithTheTransaction()
    {
        await using (var transaction = await _context.Database.BeginTransactionAsync())
        {
            var pulled = await _items.PullForwardValidFromAsync(new Dictionary<Guid, DateTime> { [_leafItemId] = Entry });
            await _context.SaveChangesAsync();
            var lowered = await _groups.LowerValidFromAsync(new Dictionary<Guid, DateTime> { [_leafId] = Entry });

            pulled.ShouldBe(1);
            lowered.ShouldBe(3);
            (await _items.GetValidFromByIdsAsync([_leafItemId]))[_leafItemId].ShouldBe(Entry);
            (await _context.Group.AsNoTracking().SingleAsync(g => g.Id == _rootId)).ValidFrom.ShouldBe(Entry);

            await transaction.RollbackAsync();
        }

        _context.ChangeTracker.Clear();
        (await _items.GetValidFromByIdsAsync([_leafItemId]))[_leafItemId].ShouldBe(Late);
        var starts = await StartsAsync();
        starts[_leafId].ShouldBe(Late);
        starts[_midId].ShouldBe(Late);
        starts[_rootId].ShouldBe(Late);
    }

    [Test]
    public async Task APullForwardInACommittedTransaction_PersistsBoth()
    {
        await using (var transaction = await _context.Database.BeginTransactionAsync())
        {
            await _items.PullForwardValidFromAsync(new Dictionary<Guid, DateTime> { [_leafItemId] = Entry });
            await _context.SaveChangesAsync();
            await _groups.LowerValidFromAsync(new Dictionary<Guid, DateTime> { [_leafId] = Entry });
            await transaction.CommitAsync();
        }

        await using var check = NewContext();
        var items = new GroupItemRepository(check, Substitute.For<ILogger<GroupItem>>());
        (await items.GetValidFromByIdsAsync([_leafItemId]))[_leafItemId].ShouldBe(Entry);
    }

    [Test]
    public async Task PullForwardValidFromAsync_LeavesOpenAndEarlierStartsAlone_OnPostgres()
    {
        var changed = await _items.PullForwardValidFromAsync(new Dictionary<Guid, DateTime>
        {
            [_leafItemId] = Entry,
            [_openItemId] = Entry,
            [_endedItemId] = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await _context.SaveChangesAsync();

        changed.ShouldBe(1);
        var stored = await _items.GetValidFromByIdsAsync([_leafItemId, _openItemId, _endedItemId]);
        stored[_leafItemId].ShouldBe(Entry);
        stored[_openItemId].ShouldBeNull();
        stored[_endedItemId].ShouldBe(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task GetEndedMembershipsAsync_ReturnsTheEndedMembershipOfTheClient_OnPostgres()
    {
        var ended = await _items.GetEndedMembershipsAsync([_clientId]);

        ended.Count.ShouldBe(1);
        ended[0].ClientId.ShouldBe(_clientId);
        ended[0].GroupId.ShouldBe(_siblingId);
        ended[0].ValidUntil.ShouldBe(new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc));
    }

    private async Task<Dictionary<Guid, DateTime>> StartsAsync()
    {
        await using var check = NewContext();
        return await check.Group.AsNoTracking()
            .Where(g => g.Name.StartsWith(TestPrefix))
            .ToDictionaryAsync(g => g.Id, g => g.ValidFrom);
    }

    private async Task SeedAsync()
    {
        _context.Group.AddRange(
            NewGroup(_rootId, "Root", null, Late),
            NewGroup(_midId, "Mid", _rootId, Late),
            NewGroup(_leafId, "Leaf", _midId, Late),
            NewGroup(_siblingId, "Sibling", _rootId, Late),
            NewGroup(_earlyRootId, "EarlyRoot", null, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        _context.Client.Add(new Client
        {
            Id = _clientId,
            Name = TestPrefix + "Client",
            FirstName = "PullForward",
            Type = EntityTypeEnum.Employee,
            Gender = GenderEnum.Female,
            LegalEntity = false
        });
        await _context.SaveChangesAsync();

        _context.GroupItem.AddRange(
            new GroupItem { Id = _leafItemId, ClientId = _clientId, GroupId = _leafId, ValidFrom = Late },
            new GroupItem { Id = _openItemId, ClientId = _clientId, GroupId = _midId },
            new GroupItem
            {
                Id = _endedItemId,
                ClientId = _clientId,
                GroupId = _siblingId,
                ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidUntil = new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc)
            });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Group NewGroup(Guid id, string suffix, Guid? parent, DateTime validFrom) => new()
    {
        Id = id,
        Name = TestPrefix + suffix,
        Description = "Integration test group",
        Parent = parent,
        Root = parent is null ? id : _rootId,
        ValidFrom = validFrom
    };

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE name LIKE {0}) " +
            "OR group_id IN (SELECT id FROM \"group\" WHERE name LIKE {0})",
            TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client WHERE name LIKE {0}", TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM \"group\" WHERE name LIKE {0}", TestPrefix + "%");
    }
}
