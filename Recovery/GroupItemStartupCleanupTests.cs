// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The startup cleanup of group_item (cleanup_group_item_duplicates.sql, executed by StoredProcedureInitializer on every
/// backend start) against PostgreSQL: it reduces a doubled membership of the real plan to its newer row and leaves the
/// memberships scenarios hold alone. Two scenarios that each borrowed the same person into the same group keep their
/// rows, and a real membership keeps standing next to a newer scenario membership of the same person and group; before
/// the script compared real-plan rows only, each backend start hard-deleted the older row of such a pair. The tests do
/// not run the initializer: the script works on the whole shared database and the initializer also recreates the
/// schedule functions. They read the script from the embedded resource of the Api assembly (GroupItemCleanupScript),
/// run its statements inside a transaction of their own and roll it back, so neither the seeded rows nor anything the
/// script touched elsewhere persists. One test pins that the resource exists and that both client statements compare
/// real-plan rows only, so a renamed resource or a changed statement cannot turn the others into no-ops.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.IntegrationTest.TestHelpers;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class GroupItemStartupCleanupTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_GSC_";
    private const int ClientStatementCount = 2;
    private static readonly DateTime Day = new(2093, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    private IDbContextTransaction? _transaction;
    private Guid _groupId;
    private Guid _clientId;

    [SetUp]
    public async Task SeedSetUp()
    {
        await PurgeLeftoversAsync();
        _transaction = await Context.Database.BeginTransactionAsync();
        _groupId = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = Day });
        Context.Client.Add(new Client { Id = _clientId, FirstName = Prefix, Name = Prefix + "Person", Type = EntityTypeEnum.Employee });
        await Context.SaveChangesAsync();
    }

    [TearDown]
    public async Task SeedTearDown()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    [Test]
    public void TheScript_IsEmbedded_AndBothClientStatementsCompareRealPlanRowsOnly()
    {
        // Act
        var clientStatements = GroupItemCleanupScript.ClientStatements();

        // Assert
        clientStatements.Count.ShouldBe(ClientStatementCount);
        foreach (var statement in clientStatements)
        {
            statement.ShouldContain("DELETE FROM group_item gi");
            statement.ShouldContain("gi.analyse_token IS NULL");
            statement.ShouldContain("gi2.analyse_token IS NULL");
        }
    }

    [Test]
    public async Task TheMembershipsOfTwoScenarios_ForTheSamePersonAndGroup_BothSurviveTheCleanup()
    {
        // Arrange
        var first = await AddMembershipAsync(Guid.NewGuid());
        var second = await AddMembershipAsync(Guid.NewGuid());

        // Act
        await GroupItemCleanupScript.ExecuteInOpenTransactionAsync(Context);

        // Assert
        (await StoredIdsAsync()).ShouldBe(new[] { first, second }, ignoreOrder: true);
    }

    [Test]
    public async Task ARealMembership_NextToANewerScenarioMembership_SurvivesTheCleanup()
    {
        // Arrange
        var real = await AddMembershipAsync(token: null);
        var scenario = await AddMembershipAsync(Guid.NewGuid());

        // Act
        await GroupItemCleanupScript.ExecuteInOpenTransactionAsync(Context);

        // Assert
        (await StoredIdsAsync()).ShouldBe(new[] { real, scenario }, ignoreOrder: true);
    }

    [Test]
    public async Task ADoubledRealMembership_IsStillReducedToItsNewerRow()
    {
        // Arrange: the unique index only guards live rows, so the older row of the pair is a deleted one.
        var older = await AddMembershipAsync(token: null, isDeleted: true);
        var newer = await AddMembershipAsync(token: null);

        // Act
        await GroupItemCleanupScript.ExecuteInOpenTransactionAsync(Context);

        // Assert: proves the script ran at all, the two tests above would also pass against a script that does nothing.
        var stored = await StoredIdsAsync();
        stored.ShouldBe(new[] { newer });
        stored.ShouldNotContain(older);
    }

    private async Task<Guid> AddMembershipAsync(Guid? token, bool isDeleted = false)
    {
        var membership = new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            GroupId = _groupId,
            ValidFrom = Day,
            ValidUntil = Day,
            AnalyseToken = token,
            IsDeleted = isDeleted,
        };
        Context.GroupItem.Add(membership);
        await Context.SaveChangesAsync();
        return membership.Id;
    }

    private Task<List<Guid>> StoredIdsAsync()
        => Context.GroupItem.IgnoreQueryFilters().AsNoTracking()
            .Where(gi => gi.ClientId == _clientId && gi.GroupId == _groupId)
            .Select(gi => gi.Id)
            .ToListAsync();

    /// <summary>
    /// Removes rows an earlier, aborted run committed under the INTEGRATION_TEST_GSC_ marker; the tests themselves
    /// commit nothing.
    /// </summary>
    private async Task PurgeLeftoversAsync()
    {
        var sql = $@"
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%';
            DELETE FROM client WHERE first_name LIKE '{Prefix}%';";

        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
