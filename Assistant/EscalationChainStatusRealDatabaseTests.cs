// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proof for the escalation parts behind get_replacement_status and the opened intervention list:
/// the repository query that returns AbsenceCoverage chains of one absent employee for a UTC window whatever their
/// status (translated and executed by Npgsql, which the in-memory provider would not prove), the Running list with
/// the cancel flag, and a cancel that really ends the chain, cancels its stages and closes the woken planner inbox
/// question. Cleanup deletes ONLY rows whose absent client name carries this fixture prefix.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class EscalationChainStatusRealDatabaseTests
{
    private const string TestPrefix = "INTEGRATION_TEST_ESC_STATUS_";
    private const string PlannerId = "integration-test-planner";

    private static readonly DateTime ShiftStartUtc = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);

    private readonly List<string> _userIds = [];
    private readonly List<Guid> _groupIds = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var migrationOptions = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var context = new DataBaseContext(migrationOptions, Substitute.For<IHttpContextAccessor>()))
        {
            await context.Database.MigrateAsync();
        }

        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task AbsenceCoverageChainsByAbsentClient_ReturnsEveryStatusInTheWindow_AndNothingElse()
    {
        var absent = Guid.NewGuid();
        var running = await AddChainAsync(absent, ShiftStartUtc, EscalationChainStatus.Running);
        var acknowledged = await AddChainAsync(absent, ShiftStartUtc.AddDays(1), EscalationChainStatus.Acknowledged);
        await AddChainAsync(absent, ShiftStartUtc.AddDays(30), EscalationChainStatus.Running);
        await AddChainAsync(Guid.NewGuid(), ShiftStartUtc, EscalationChainStatus.Running);
        await AddChainAsync(absent, ShiftStartUtc, EscalationChainStatus.Running, EscalationChainPurpose.ProactiveApproval);

        await using var context = NewContext();
        var found = await new EscalationChainRepository(context).GetAbsenceCoverageChainsByAbsentClientAsync(
            absent, ShiftStartUtc.AddHours(-1), ShiftStartUtc.AddDays(2));

        found.Select(c => c.Id).ToList().ShouldBe(new List<Guid> { running, acknowledged });
        found.ShouldAllBe(c => c.Stages.Count == 2);
        found.ShouldAllBe(c => c.Purpose == EscalationChainPurpose.AbsenceCoverage);
    }

    [Test]
    public async Task RunningList_CarriesTheCancelFlagPerCaller()
    {
        var chainId = await AddChainAsync(Guid.NewGuid(), ShiftStartUtc, EscalationChainStatus.Running);

        var onlyFixtureRows = Substitute.For<IEscalationChainVisibilityFilter>();
        onlyFixtureRows.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<EscalationChain>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(
                call.Arg<IReadOnlyCollection<EscalationChain>>().Where(c => c.AbsentClientName.StartsWith(TestPrefix)).ToList()));
        var userService = Substitute.For<IUserService>();
        userService.IsAdmin().Returns(false);
        userService.GetRights().Returns(new List<string>());

        await using var context = NewContext();
        var handler = new GetRunningEscalationChainsQueryHandler(new EscalationChainRepository(context), onlyFixtureRows, userService);

        var holder = await handler.Handle(new GetRunningEscalationChainsQuery(PlannerId), CancellationToken.None);
        var stranger = await handler.Handle(new GetRunningEscalationChainsQuery("someone-else"), CancellationToken.None);

        holder.Single(c => c.Id == chainId).CanCancel.ShouldBeTrue();
        stranger.Single(c => c.Id == chainId).CanCancel.ShouldBeFalse();
    }

    [Test]
    public async Task CancelThroughTheHandler_OnOneTrackingContext_StillClosesTheWokenPlannersInboxQuestion()
    {
        var chainId = await AddChainAsync(Guid.NewGuid(), ShiftStartUtc, EscalationChainStatus.Running);

        var notifier = Substitute.For<IEscalationNotifier>();
        var visibility = Substitute.For<IEscalationChainVisibilityFilter>();
        visibility.IsVisibleAsync(Arg.Any<EscalationChain>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var userService = Substitute.For<IUserService>();
        userService.IsAdmin().Returns(true);

        await using (var context = NewContext())
        {
            var repository = new EscalationChainRepository(context);
            var service = new EscalationChainService(
                repository,
                Substitute.For<IEscalationRosterService>(),
                notifier,
                Substitute.For<ISettingsReader>(),
                Substitute.For<IAgentConditionLedgerService>(),
                TimeProvider.System,
                NullLogger<EscalationChainService>.Instance);
            var handler = new CancelEscalationChainCommandHandler(service, repository, visibility, userService);

            var result = await handler.Handle(
                new CancelEscalationChainCommand(chainId, PlannerId, "Planner", "covered elsewhere"), CancellationToken.None);

            result.Outcome.ShouldBe(EscalationCancelOutcome.Cancelled);
        }

        await notifier.Received(1).CloseOpenQuestionsAsync(
            Arg.Is<IReadOnlyList<EscalationStage>>(stages => stages.Count == 1 && stages[0].UserId == PlannerId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cancel_EndsTheChain_CancelsStages_AndClosesTheWokenPlannersInboxQuestion()
    {
        var chainId = await AddChainAsync(Guid.NewGuid(), ShiftStartUtc, EscalationChainStatus.Running);

        var notifier = Substitute.For<IEscalationNotifier>();
        await using (var context = NewContext())
        {
            var service = new EscalationChainService(
                new EscalationChainRepository(context),
                Substitute.For<IEscalationRosterService>(),
                notifier,
                Substitute.For<ISettingsReader>(),
                Substitute.For<IAgentConditionLedgerService>(),
                TimeProvider.System,
                NullLogger<EscalationChainService>.Instance);

            (await service.CancelAsync(chainId, PlannerId, "Planner", "covered elsewhere")).ShouldBeTrue();
        }

        await using var verify = NewContext();
        var chain = await verify.EscalationChains.AsNoTracking().Include(c => c.Stages).SingleAsync(c => c.Id == chainId);
        chain.Status.ShouldBe(EscalationChainStatus.Cancelled);
        chain.CancelReason.ShouldBe("covered elsewhere");
        chain.Stages.ShouldAllBe(s => s.Status == EscalationStageStatus.Cancelled);
        await notifier.Received(1).CloseOpenQuestionsAsync(
            Arg.Is<IReadOnlyList<EscalationStage>>(stages => stages.Count == 1 && stages[0].UserId == PlannerId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Filter_AgainstRealVisibilityRows_HidesFromASupervisorWithoutRows_AndShowsToOneWhoSeesTheGroup()
    {
        var group = await SeedGroupAsync();
        var withoutRows = await SeedUserAsync();
        var withRow = await SeedUserAsync(group);
        var chain = NewChain(group);
        var groupless = NewChain(null);

        await using var context = NewContext();
        var filter = NewFilter(context);

        (await filter.FilterVisibleAsync(new[] { chain, groupless }, withoutRows)).ShouldBeEmpty();

        var seen = await filter.FilterVisibleAsync(new[] { chain, groupless }, withRow);
        seen.Select(c => c.Id).ShouldBe(new[] { chain.Id });
        (await filter.IsVisibleAsync(groupless, withRow)).ShouldBeFalse();
    }

    [Test]
    public async Task Filter_AgainstRealVisibilityRows_StageHolderSeesTheirOwnChainWithoutAnyRow()
    {
        var holder = await SeedUserAsync();
        var groupless = NewChain(null, holder);

        await using var context = NewContext();
        var filter = NewFilter(context);

        (await filter.IsVisibleAsync(groupless, holder)).ShouldBeTrue();
        (await filter.FilterVisibleAsync(new[] { groupless }, holder)).Count.ShouldBe(1);
    }

    private static EscalationChain NewChain(Guid? groupId, string? stageHolder = null) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = groupId,
        Stages = stageHolder is null
            ? new List<EscalationStage>()
            : new List<EscalationStage> { new() { UserId = stageHolder, Status = EscalationStageStatus.Notified } }
    };

    private static EscalationChainVisibilityFilter NewFilter(DataBaseContext context)
    {
        var userManager = Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(), null, null, null, null, null, null, null, null);
        userManager.GetUsersInRoleAsync(Arg.Any<string>()).Returns(new List<AppUser>());

        var visibilityRepository = Substitute.For<IGroupVisibilityRepository>();
        visibilityRepository.GroupVisibilityList(Arg.Any<string>())
            .Returns(call => ReadVisibilityRowsAsync(context, (string)call[0]));

        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.GetNoTracking(Arg.Any<Guid>())
            .Returns(call => context.Group.AsNoTracking().FirstOrDefaultAsync(g => g.Id == (Guid)call[0]));

        var resolver = new PlanningAudienceResolver(
            userManager, new MemoryCache(new MemoryCacheOptions()), visibilityRepository, groupRepository,
            Substitute.For<IGroupItemRepository>());
        return new EscalationChainVisibilityFilter(resolver);
    }

    private static async Task<IEnumerable<GroupVisibility>> ReadVisibilityRowsAsync(DataBaseContext context, string userId)
    {
        return await context.GroupVisibility.AsNoTracking().Where(g => g.AppUserId == userId).ToListAsync();
    }

    private async Task<Guid> SeedGroupAsync()
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "group",
            Description = "Escalation chain visibility integration test",
            ValidFrom = DateTime.UtcNow.AddYears(-1)
        };
        await using var context = NewContext();
        context.Group.Add(group);
        await context.SaveChangesAsync();
        _groupIds.Add(group.Id);
        return group.Id;
    }

    private async Task<string> SeedUserAsync(params Guid[] visibleGroupIds)
    {
        var email = $"{TestPrefix}user-{Guid.NewGuid():N}@example.invalid";
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = TestPrefix,
            LastName = "Planner",
            SecurityStamp = Guid.NewGuid().ToString()
        };
        await using var context = NewContext();
        context.AppUser.Add(user);
        await context.SaveChangesAsync();
        _userIds.Add(user.Id);

        foreach (var groupId in visibleGroupIds)
        {
            context.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = user.Id, GroupId = groupId });
        }

        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> AddChainAsync(
        Guid absentClientId,
        DateTime shiftStartUtc,
        EscalationChainStatus status,
        EscalationChainPurpose purpose = EscalationChainPurpose.AbsenceCoverage)
    {
        var chain = new EscalationChain
        {
            Id = Guid.NewGuid(),
            Status = status,
            Purpose = purpose,
            WorkId = purpose == EscalationChainPurpose.AbsenceCoverage ? Guid.NewGuid() : null,
            ConditionId = purpose == EscalationChainPurpose.ProactiveApproval ? Guid.NewGuid() : null,
            GroupId = Guid.NewGuid(),
            AbsentClientId = absentClientId,
            AbsentClientName = TestPrefix + "absent",
            ShiftStartUtc = shiftStartUtc,
            DeadlineUtc = shiftStartUtc.AddHours(-1),
            Stages = new List<EscalationStage>
            {
                new()
                {
                    Rank = 1,
                    UserId = PlannerId,
                    UserDisplayName = TestPrefix + "first",
                    Status = status == EscalationChainStatus.Running ? EscalationStageStatus.Notified : EscalationStageStatus.Expired,
                    NotifiedAtUtc = shiftStartUtc.AddHours(-3),
                    DueAtUtc = shiftStartUtc.AddHours(-2)
                },
                new() { Rank = 2, UserId = "integration-test-second", UserDisplayName = TestPrefix + "second" }
            }
        };

        await using var context = NewContext();
        context.EscalationChains.Add(chain);
        await context.SaveChangesAsync();
        return chain.Id;
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM escalation_stages WHERE escalation_chain_id IN "
            + "(SELECT id FROM escalation_chains WHERE starts_with(absent_client_name, {0}))",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM escalation_chains WHERE starts_with(absent_client_name, {0})",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_visibility WHERE app_user_id IN (SELECT id FROM \"AspNetUsers\" WHERE first_name = {0})",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"AspNetUsers\" WHERE first_name = {0}",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE starts_with(name, {0})",
            TestPrefix);
    }
}
