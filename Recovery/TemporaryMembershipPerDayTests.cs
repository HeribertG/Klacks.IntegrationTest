// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Temporary membership per day against PostgreSQL: a person borrowed on Monday and on Friday is a member of the
/// receiving group on those two days only, in the scenario and after the scenario is taken over (the schedule's
/// membership windows say so), the unique indexes allow one row per day and refuse a second row for the same day, two
/// scenarios that borrowed the same day are both taken over without an error, a day removed and added again in one
/// unit of work saves, the person stays a borrowed person for the candidate pool, reading and saving the
/// employee's master data neither shows nor changes the temporary rows, and the cleanup every backend start runs
/// (cleanup_group_item_duplicates.sql) leaves the borrowed days next to each other and next to a regular membership;
/// that script is run from the embedded resource inside a transaction that is rolled back, never through the initializer.
/// Outside the plan the person stays with the home group: the mail of the receiving group does not list her, its
/// period closing sees her only in a period with a borrowed day, and its planning runs (planning skills, Wizard 4,
/// the cover of a source scenario in a recovery run) do not take her as one of their own, and the agents a run started
/// from the user interface sends lose her in the receiving group only (IBorrowedAgentFilter).
/// Every seeded row hangs off an
/// INTEGRATION_TEST_TMD_ group or client and is removed by that key, before and after each test.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.IntegrationTest.TestHelpers;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class TemporaryMembershipPerDayTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_TMD_";
    private const int MaxNotes = 50;
    private static readonly DateOnly Monday = new(2092, 6, 2);
    private static readonly DateOnly Wednesday = Monday.AddDays(2);
    private static readonly DateOnly Friday = Monday.AddDays(4);
    private static readonly DateTime GroupsValidFrom = new(2092, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private Guid _rootId;
    private Guid _groupId;
    private Guid _sisterGroupId;
    private Guid _borrowedId;
    private Guid _homeMembershipId;

    [SetUp]
    public async Task SeedSetUp()
    {
        await PurgeAsync();
        await SeedAsync();
    }

    [TearDown]
    public async Task SeedTearDown()
    {
        Context.ChangeTracker.Clear();
        await PurgeAsync();
    }

    [Test]
    public async Task BorrowedOnMondayAndFriday_IsAMemberOnThoseDaysOnly_AlsoAfterTheScenarioIsTakenOver()
    {
        // Arrange
        var token = Guid.NewGuid();
        await BorrowAsync(token, Monday, Friday);

        // Act
        await PromoteAsync(token);

        // Assert
        var real = await RealRowsInGroupAsync();
        real.Select(row => row.ValidFrom).ShouldBe(new DateTime?[] { Utc(Monday), Utc(Friday) });
        real.ShouldAllBe(row => row.IsTemporary && row.ValidFrom == row.ValidUntil);

        var windows = await GroupMembershipWindowLoader.LoadAsync(
            Context, [_groupId], [_borrowedId], Monday, Friday, CancellationToken.None);
        for (var day = Monday; day <= Friday; day = day.AddDays(1))
        {
            var isMember = windows.Any(entry => entry.GroupId == _groupId && entry.Window.IsActiveOn(day));
            isMember.ShouldBe(day == Monday || day == Friday, $"membership on {day}");
        }
    }

    [Test]
    public async Task ASecondRowForTheSameDay_IsRefused_InTheScenarioAndInTheRealPlan()
    {
        // Arrange
        var token = Guid.NewGuid();
        Context.GroupItem.Add(DayRow(Monday, token));
        Context.GroupItem.Add(DayRow(Monday, token: null));
        await Context.SaveChangesAsync();

        // Act and assert: the scenario index carries the day
        Context.GroupItem.Add(DayRow(Monday, token));
        await Should.ThrowAsync<DbUpdateException>(() => Context.SaveChangesAsync());
        Context.ChangeTracker.Clear();

        // Act and assert: so does the index of the temporary real memberships
        Context.GroupItem.Add(DayRow(Monday, token: null));
        await Should.ThrowAsync<DbUpdateException>(() => Context.SaveChangesAsync());
        Context.ChangeTracker.Clear();
    }

    [Test]
    public async Task OtherDays_AndARegularMembershipNextToTheBorrowedDays_AreAllowed()
    {
        // Arrange
        var token = Guid.NewGuid();
        Context.GroupItem.Add(DayRow(Monday, token));
        Context.GroupItem.Add(DayRow(Friday, token));
        Context.GroupItem.Add(DayRow(Monday, token: null));
        Context.GroupItem.Add(DayRow(Friday, token: null));
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = _borrowedId, GroupId = _groupId, ValidFrom = Utc(Monday) });

        // Act
        await Should.NotThrowAsync(() => Context.SaveChangesAsync());

        // Assert: a second regular membership of the same person in the same group is still refused
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = _borrowedId, GroupId = _groupId, ValidFrom = Utc(Friday) });
        await Should.ThrowAsync<DbUpdateException>(() => Context.SaveChangesAsync());
        Context.ChangeTracker.Clear();
    }

    [Test]
    public async Task TwoScenarios_ThatBorrowedTheSameDay_AreBothTakenOver_AndTheDayIsThereOnce()
    {
        // Arrange
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await BorrowAsync(first, Monday);
        await BorrowAsync(second, Monday, Friday);

        // Act
        await PromoteAsync(first);
        await Should.NotThrowAsync(() => PromoteAsync(second));

        // Assert
        var real = await RealRowsInGroupAsync();
        real.Select(row => row.ValidFrom).ShouldBe(new DateTime?[] { Utc(Monday), Utc(Friday) });
        var leftOfSecond = await Context.GroupItem.IgnoreQueryFilters().AsNoTracking()
            .Where(gi => gi.AnalyseToken == second)
            .ToListAsync();
        leftOfSecond.ShouldHaveSingleItem().IsDeleted.ShouldBeTrue("the day the real plan already holds is discarded");
    }

    [Test]
    public async Task ADayRemovedAndAddedAgain_InOneUnitOfWork_SavesAsOneRow()
    {
        // Arrange
        var token = Guid.NewGuid();
        await BorrowAsync(token, Monday);

        // Act
        using (var scope = CreateScope())
        {
            var scenarioService = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
            await scenarioService.RemoveScenarioMembershipsAsync(token, _borrowedId, Monday, CancellationToken.None);
            await scenarioService.AddScenarioMembershipAsync(token, _borrowedId, _groupId, Monday, Monday, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CompleteAsync();
        }

        // Assert
        var live = await Context.GroupItem.AsNoTracking().Where(gi => gi.AnalyseToken == token).ToListAsync();
        live.ShouldHaveSingleItem().ValidFrom.ShouldBe(Utc(Monday));
    }

    [Test]
    public async Task RemovingMonday_LeavesFriday_InTheScenario()
    {
        // Arrange
        var token = Guid.NewGuid();
        await BorrowAsync(token, Monday, Friday);

        // Act
        using (var scope = CreateScope())
        {
            var scenarioService = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
            await scenarioService.RemoveScenarioMembershipsAsync(token, _borrowedId, Monday, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CompleteAsync();
        }

        // Assert
        var live = await Context.GroupItem.AsNoTracking().Where(gi => gi.AnalyseToken == token).ToListAsync();
        live.ShouldHaveSingleItem().ValidFrom.ShouldBe(Utc(Friday));
    }

    [Test]
    public async Task APersonWithOnlyBorrowedDaysInTheGroup_StaysABorrowedPerson_AfterTheScenarioIsTakenOver()
    {
        // Arrange
        var token = Guid.NewGuid();
        await BorrowAsync(token, Monday, Friday);
        await PromoteAsync(token);

        // Act
        using var scope = CreateScope();
        var memberIds = await scope.ServiceProvider.GetRequiredService<IGetAllClientIdsFromGroupAndSubgroups>()
            .GetRealClientIdsFromGroupAndSubgroups(_groupId, CancellationToken.None);
        var sisterMemberIds = await scope.ServiceProvider.GetRequiredService<IGetAllClientIdsFromGroupAndSubgroups>()
            .GetRealClientIdsFromGroupAndSubgroups(_sisterGroupId, CancellationToken.None);
        var pool = await scope.ServiceProvider.GetRequiredService<IReplacementCandidatePoolResolver>()
            .ResolveAsync(_groupId, CancellationToken.None);

        // Assert
        memberIds.ShouldNotContain(_borrowedId);
        sisterMemberIds.ShouldContain(_borrowedId);
        pool.ShouldNotBeNull();
        pool.InGroup.Select(person => person.Id).ShouldNotContain(_borrowedId);
        pool.CrossGroup.Select(person => person.Id).ShouldContain(_borrowedId);
    }

    [Test]
    public async Task ReadingAndSavingTheEmployee_ShowsTheRegularMembershipOnly_AndLeavesTheTemporaryRowsAsTheyAre()
    {
        // Arrange: borrowed days of the real plan and a day a still open scenario holds.
        var takenOver = Guid.NewGuid();
        var open = Guid.NewGuid();
        await BorrowAsync(takenOver, Monday, Friday);
        await PromoteAsync(takenOver);
        await BorrowAsync(open, Wednesday);
        var before = await TemporaryRowsAsync();

        // Act
        using (var scope = CreateScope())
        {
            var clientRepository = scope.ServiceProvider.GetRequiredService<IClientRepository>();
            var read = await clientRepository.Get(_borrowedId);
            read.ShouldNotBeNull();
            read.GroupItems.ShouldHaveSingleItem().Id.ShouldBe(_homeMembershipId);

            await clientRepository.Put(read);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CompleteAsync();
        }

        // Assert
        var after = await TemporaryRowsAsync();
        after.Count.ShouldBe(3);
        after.Select(Describe).ShouldBe(before.Select(Describe));
        var home = await Context.GroupItem.AsNoTracking().SingleAsync(gi => gi.Id == _homeMembershipId);
        home.IsTemporary.ShouldBeFalse();
        home.AnalyseToken.ShouldBeNull();
    }

    [Test]
    public async Task ThePlanningRunsOfTheReceivingGroup_PlanItsRegularMembers_NotABorrowedPerson()
    {
        // Arrange: borrowed days of the real plan inside the planning period and a day an open scenario holds.
        var takenOver = Guid.NewGuid();
        await BorrowAsync(takenOver, Monday, Friday);
        await PromoteAsync(takenOver);
        await BorrowAsync(Guid.NewGuid(), Wednesday);

        // Act
        using var scope = CreateScope();
        var planningAgents = scope.ServiceProvider.GetRequiredService<IGroupPlanningAgentRepository>();
        var wizard4Agents = scope.ServiceProvider.GetRequiredService<IWizard4AgentResolver>();
        var groupClients = scope.ServiceProvider.GetRequiredService<IGetAllClientIdsFromGroupAndSubgroups>();
        var agentsOfTheReceivingGroup = await planningAgents.GetAgentIdsAsync(_groupId, Monday, Friday, CancellationToken.None);
        var agentsOfTheHomeGroup = await planningAgents.GetAgentIdsAsync(_sisterGroupId, Monday, Friday, CancellationToken.None);
        var wizard4OfTheReceivingGroup = await wizard4Agents.ResolveAsync(_groupId, Monday, Friday, CancellationToken.None);
        var wizard4OfTheHomeGroup = await wizard4Agents.ResolveAsync(_sisterGroupId, Monday, Friday, CancellationToken.None);
        var coveredByASourceScenario = await groupClients.GetRealClientIdsFromGroupAndSubgroups(_groupId, CancellationToken.None);
        var everyGroupRow = await groupClients.GetAllClientIdsFromGroupAndSubgroups(_groupId);

        // Assert
        agentsOfTheReceivingGroup.ShouldNotContain(_borrowedId);
        agentsOfTheHomeGroup.ShouldContain(_borrowedId);
        wizard4OfTheReceivingGroup.ShouldNotContain(_borrowedId);
        wizard4OfTheHomeGroup.ShouldContain(_borrowedId);
        coveredByASourceScenario.ShouldNotContain(_borrowedId);
        everyGroupRow.ShouldContain(_borrowedId, "the reader of every group row, which the recovery run used before, lists her");
    }

    [Test]
    public async Task TheAgentsAUserInterfaceRunSends_LoseTheBorrowedPerson_OnlyInTheReceivingGroup()
    {
        // Arrange: the schedule grid of the receiving group lists her, and somebody without a membership in the tree.
        var takenOver = Guid.NewGuid();
        await BorrowAsync(takenOver, Monday, Friday);
        await PromoteAsync(takenOver);
        await BorrowAsync(Guid.NewGuid(), Wednesday);
        var listedForAnotherReason = Guid.NewGuid();
        IReadOnlyList<Guid> sentByTheClient = [listedForAnotherReason, _borrowedId];

        // Act
        using var scope = CreateScope();
        var filter = scope.ServiceProvider.GetRequiredService<IBorrowedAgentFilter>();
        var receivingGroup = await filter.RemoveBorrowedAsync(sentByTheClient, _groupId, null, CancellationToken.None);
        var homeGroup = await filter.RemoveBorrowedAsync(sentByTheClient, _sisterGroupId, null, CancellationToken.None);
        var wholeTree = await filter.RemoveBorrowedAsync(sentByTheClient, _rootId, null, CancellationToken.None);

        // Assert: in the root's subtree her regular membership of the sister group keeps her.
        receivingGroup.ShouldBe([listedForAnotherReason]);
        homeGroup.ShouldBe(sentByTheClient);
        wholeTree.ShouldBe(sentByTheClient);
    }

    [Test]
    public async Task ABorrowedPerson_BelongsToTheReceivingGroupOnlyInThePlan_NotToItsMail()
    {
        // Arrange
        var token = Guid.NewGuid();
        await BorrowAsync(token, Monday, Friday);
        await PromoteAsync(token);

        // Act
        using var scope = CreateScope();
        var emailQueries = scope.ServiceProvider.GetRequiredService<IEmailQueryRepository>();
        var ofTheReceivingGroup = await emailQueries.GetClientIdsByGroupIdsAsync([_groupId], CancellationToken.None);
        var ofTheHomeGroup = await emailQueries.GetClientIdsByGroupIdsAsync([_sisterGroupId], CancellationToken.None);

        // Assert
        ofTheReceivingGroup.ShouldNotContain(_borrowedId);
        ofTheHomeGroup.ShouldContain(_borrowedId);
    }

    [Test]
    public async Task ThePeriodClosingOfTheReceivingGroup_SeesTheBorrowedPerson_OnlyInAPeriodWithABorrowedDay()
    {
        // Arrange: a note in the week she was borrowed and one in the week after.
        var token = Guid.NewGuid();
        await BorrowAsync(token, Monday, Friday);
        await PromoteAsync(token);
        var nextMonday = Monday.AddDays(7);
        Context.ScheduleNotes.Add(new ScheduleNote { Id = Guid.NewGuid(), ClientId = _borrowedId, CurrentDate = Monday, Content = Prefix + "borrowed week" });
        Context.ScheduleNotes.Add(new ScheduleNote { Id = Guid.NewGuid(), ClientId = _borrowedId, CurrentDate = nextMonday, Content = Prefix + "home week" });
        await Context.SaveChangesAsync();

        // Act
        using var scope = CreateScope();
        var periodClosing = scope.ServiceProvider.GetRequiredService<IPeriodClosingReadRepository>();
        var borrowedWeek = await periodClosing.GetScheduleNoteIssues(Monday, Friday, _groupId, MaxNotes, CancellationToken.None);
        var weekAfter = await periodClosing.GetScheduleNoteIssues(nextMonday, nextMonday.AddDays(4), _groupId, MaxNotes, CancellationToken.None);
        var weekAfterAtHome = await periodClosing.GetScheduleNoteIssues(nextMonday, nextMonday.AddDays(4), _sisterGroupId, MaxNotes, CancellationToken.None);

        // Assert
        borrowedWeek.Select(note => note.ClientId).ShouldContain(_borrowedId);
        weekAfter.Select(note => note.ClientId).ShouldNotContain(_borrowedId);
        weekAfterAtHome.Select(note => note.ClientId).ShouldContain(_borrowedId);
    }

    [Test]
    public async Task BorrowedDays_NextToARegularMembershipOfTheSameGroup_SurviveTheStartupCleanup()
    {
        // The script works on the whole database, so it runs in a transaction that is rolled back.
        await using var transaction = await Context.Database.BeginTransactionAsync();
        try
        {
            // Arrange: the regular membership is the oldest row, so a cleanup that compared every real row would delete it.
            await AddRowAsync(new GroupItem { Id = Guid.NewGuid(), ClientId = _borrowedId, GroupId = _groupId, ValidFrom = Utc(Monday.AddDays(7)) });
            await AddRowAsync(DayRow(Monday, token: null));
            await AddRowAsync(DayRow(Friday, token: null));
            await AddRowAsync(DayRow(Wednesday, Guid.NewGuid()));
            var before = await RowsInGroupAsync();

            // Act
            await GroupItemCleanupScript.ExecuteInOpenTransactionAsync(Context);

            // Assert
            before.Count.ShouldBe(4);
            (await RowsInGroupAsync()).ShouldBe(before);
        }
        finally
        {
            await transaction.RollbackAsync();
            Context.ChangeTracker.Clear();
        }
    }

    [Test]
    public void TheStartupCleanup_ComparesOnlyRegularRealRows_InBothClientStatements()
    {
        // Act
        var clientStatements = GroupItemCleanupScript.ClientStatements();

        // Assert
        clientStatements.Count.ShouldBe(2);
        foreach (var statement in clientStatements)
        {
            statement.ShouldContain("gi.analyse_token IS NULL");
            statement.ShouldContain("gi2.analyse_token IS NULL");
            statement.ShouldContain("gi.is_temporary = false");
            statement.ShouldContain("gi2.is_temporary = false");
        }
    }

    private async Task AddRowAsync(GroupItem row)
    {
        Context.GroupItem.Add(row);
        await Context.SaveChangesAsync();
    }

    private Task<List<Guid>> RowsInGroupAsync()
        => Context.GroupItem.IgnoreQueryFilters().AsNoTracking()
            .Where(gi => gi.ClientId == _borrowedId && gi.GroupId == _groupId)
            .OrderBy(gi => gi.Id)
            .Select(gi => gi.Id)
            .ToListAsync();

    private async Task BorrowAsync(Guid token, params DateOnly[] days)
    {
        using var scope = CreateScope();
        var scenarioService = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
        foreach (var day in days)
        {
            await scenarioService.AddScenarioMembershipAsync(token, _borrowedId, _groupId, day, day, CancellationToken.None);
        }

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CompleteAsync();
    }

    private async Task PromoteAsync(Guid token)
    {
        using var scope = CreateScope();
        var scenarioService = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
        await scenarioService.PromoteScenarioWorksAsync(token, Monday, Friday, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CompleteAsync();
    }

    private Task<List<GroupItem>> RealRowsInGroupAsync()
        => Context.GroupItem.AsNoTracking()
            .Where(gi => gi.AnalyseToken == null && gi.ClientId == _borrowedId && gi.GroupId == _groupId)
            .OrderBy(gi => gi.ValidFrom)
            .ToListAsync();

    private Task<List<GroupItem>> TemporaryRowsAsync()
        => Context.GroupItem.IgnoreQueryFilters().AsNoTracking()
            .Where(gi => gi.ClientId == _borrowedId && gi.IsTemporary)
            .OrderBy(gi => gi.Id)
            .ToListAsync();

    private static string Describe(GroupItem row)
        => $"{row.Id}|{row.ClientId}|{row.GroupId}|{row.AnalyseToken}|{row.ValidFrom:O}|{row.ValidUntil:O}|{row.IsTemporary}|{row.IsDeleted}";

    private GroupItem DayRow(DateOnly day, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _borrowedId,
        GroupId = _groupId,
        ValidFrom = Utc(day),
        ValidUntil = Utc(day),
        AnalyseToken = token,
        IsTemporary = true,
    };

    private static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private async Task SeedAsync()
    {
        _rootId = Guid.NewGuid();
        _groupId = Guid.NewGuid();
        _sisterGroupId = Guid.NewGuid();
        _borrowedId = Guid.NewGuid();
        _homeMembershipId = Guid.NewGuid();

        Context.Group.Add(new Group { Id = _rootId, Name = Prefix + "ROOT", ValidFrom = GroupsValidFrom });
        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = GroupsValidFrom, Parent = _rootId, Root = _rootId });
        Context.Group.Add(new Group { Id = _sisterGroupId, Name = Prefix + "SISTER", ValidFrom = GroupsValidFrom, Parent = _rootId, Root = _rootId });
        Context.Client.Add(new Client { Id = _borrowedId, FirstName = Prefix, Name = Prefix + "Borrowed", Type = EntityTypeEnum.Employee });
        Context.Membership.Add(new Membership { Id = Guid.NewGuid(), ClientId = _borrowedId, ValidFrom = GroupsValidFrom });
        Context.GroupItem.Add(new GroupItem { Id = _homeMembershipId, ClientId = _borrowedId, GroupId = _sisterGroupId, ValidFrom = GroupsValidFrom });

        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// Prefix-scoped hard cleanup, FK children first. Only rows of the groups and the client carrying the
    /// INTEGRATION_TEST_TMD_ marker are removed.
    /// </summary>
    private async Task PurgeAsync()
    {
        var sql = $@"
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM schedule_notes WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%' AND parent IS NOT NULL;
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%';
            DELETE FROM client WHERE first_name LIKE '{Prefix}%';";

        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
