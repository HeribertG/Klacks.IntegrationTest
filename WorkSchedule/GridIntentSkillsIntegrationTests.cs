// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The grid-intent skills restore_work, reassign_work and bulk_delete_works against real Postgres through the real
/// mediator, handlers and DI scope. A Mock-only test cannot see what these prove: that the principal the delete stamps
/// is the one the restore compares (NameIdentifier claim), that the 60-second window and the sealed/locked guards hold
/// in the real handler chain, that container children travel with a hand-over and are deleted with a bulk delete, that
/// period hours follow, that a scenario work stays in its scenario, and that a call without any HTTP context (the
/// Telegram path) is answered honestly instead of failing. The group-restricted cases run the real client visibility
/// chain (a real group, group items, a real user row and its visibility rows); the same goes for the membership and
/// group-period refusals of the hand-over and the day seal that locks only the target employee. Every fixture row
/// carries the INTEGRATION_TEST_ prefix and is removed by its own ids. The confirmation of bulk_delete_works is bound to
/// its preview: the cases below run the real chain (real gate and preview provider, a token with its binding in the real
/// pending_confirmations table, confirm_pending_action in a new scope per turn) and prove that exactly the previewed works
/// are deleted, that a work added, gone, swapped or locked in between deletes nothing and spends the token, that a token
/// sent along with the call itself is refused and stays valid, that the binding column round-trips on Postgres, and that a
/// call without a binding never deletes. The binding covers the container children (child works and breaks) that go with
/// the listed works: one that appears between the preview and the yes deletes nothing, a child Break like a child Work.
/// The BulkDeleteWorksCommand with ExpectedChildren is also run against Postgres directly (a differing set refuses and
/// commits nothing, the exact set deletes). A plan the planner refuses (a
/// sealed day, a locked work, more than the cap) is refused by the preview at the gate, so no token is issued and nothing
/// is deleted; the skill itself, called without a binding, answers only with the missing-binding text. The one double is the ISkillExecutor
/// handed to confirm_pending_action: an NSubstitute forwarder that passes the replay (skill name, parameters and the
/// context with the redeemed binding) straight to the real skill built in the same new scope.
/// </summary>

using System.Security.Claims;
using System.Text.Json;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class GridIntentSkillsIntegrationTests
{
    private const string SeedPrefix = "INTEGRATION_TEST_Grid_";
    private const decimal SeedWorkTime = 8m;
    private static readonly TimeOnly ShiftStart = new(8, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0);

    private SignalRTestWebApplicationFactory _factory = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    private Guid _sourceClientId;
    private Guid _targetClientId;
    private Guid _shiftId;
    private Guid _contractId;
    private DateOnly _date;
    private readonly List<Guid> _sealedDayIds = new();
    private readonly List<Guid> _groupIds = new();
    private readonly List<Guid> _breakIds = new();
    private string? _plannerId;
    private Guid _confirmingUserId;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _sourceClientId = Guid.NewGuid();
        _targetClientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _contractId = Guid.NewGuid();
        _sealedDayIds.Clear();
        _groupIds.Clear();
        _breakIds.Clear();
        _plannerId = null;
        _confirmingUserId = Guid.NewGuid();
        _date =new DateOnly(2200, 1, 1).AddDays(Random.Shared.Next(0, 28000));

        _context.Client.Add(new Client { Id = _sourceClientId, Name = SeedPrefix + "Source", FirstName = "Integration" });
        _context.Client.Add(new Client { Id = _targetClientId, Name = SeedPrefix + "Target", FirstName = "Integration" });
        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = SeedPrefix + "Shift",
            StartShift = ShiftStart,
            EndShift = ShiftEnd
        });
        _context.Set<Contract>().Add(new Contract
        {
            Id = _contractId,
            Name = SeedPrefix + "Contract",
            GuaranteedHours = 168m,
            FullTime = 100m,
            ValidFrom = new DateTime(2199, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await _context.SaveChangesAsync();

        foreach (var clientId in new[] { _sourceClientId, _targetClientId })
        {
            _context.Set<ClientContract>().Add(new ClientContract
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ContractId = _contractId,
                FromDate = new DateOnly(2199, 1, 1),
                IsActive = true
            });
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [TearDown]
    public async Task TearDown()
    {
        var clients = new[] { _sourceClientId, _targetClientId };
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM pending_confirmations WHERE user_id = {0}", _confirmingUserId);
        foreach (var breakId in _breakIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM break WHERE id = {0}", breakId);
        }

        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN ({0}, {1}))", clients[0], clients[1]);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM work WHERE client_id IN ({0}, {1})", clients[0], clients[1]);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_period_hours WHERE client_id IN ({0}, {1})", clients[0], clients[1]);
        foreach (var sealId in _sealedDayIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM sealed_day WHERE id = {0}", sealId);
        }

        if (_plannerId != null)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM group_visibility WHERE app_user_id = {0}", _plannerId);
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM \"AspNetUsers\" WHERE id = {0}", _plannerId);
        }

        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE client_id IN ({0}, {1})", clients[0], clients[1]);
        foreach (var groupId in _groupIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM \"group\" WHERE id = {0}", groupId);
        }

        await _context.Database.ExecuteSqlRawAsync("DELETE FROM membership WHERE client_id IN ({0}, {1})", clients[0], clients[1]);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_contract WHERE client_id IN ({0}, {1})", clients[0], clients[1]);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE id = {0}", _shiftId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM contract WHERE id = {0}", _contractId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client WHERE id IN ({0}, {1})", clients[0], clients[1]);
        _context.Dispose();
    }

    [Test]
    public async Task DeleteThenRestore_AsTheSamePlanner_BringsBackTheWorkItsContainerChildAndThePeriodHours()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var hoursBefore = await HoursAsync(scope, _sourceClientId);

        var deleted = await Skill<DeleteWorkSkill>(scope).ExecuteAsync(Ctx(), WorkId(parentId));
        deleted.Success.ShouldBeTrue(deleted.Message);
        (await StoredAsync(parentId)).IsDeleted.ShouldBeTrue();
        (await StoredAsync(childId)).IsDeleted.ShouldBeTrue("the container child goes with its parent");

        var restored = await Skill<RestoreWorkSkill>(scope).ExecuteAsync(Ctx(), WorkId(parentId));

        restored.Success.ShouldBeTrue(restored.Message);
        (await StoredAsync(parentId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(childId)).IsDeleted.ShouldBeFalse("the child the same delete took away comes back");
        (await HoursAsync(scope, _sourceClientId)).ShouldBe(hoursBefore);
    }

    [Test]
    public async Task Restore_AfterTheUndoWindow_IsExpiredForAPlanner_AndPossibleForAnAdmin()
    {
        var (parentId, _) = await SeedContainerWorkAsync(_sourceClientId, _date);
        using (var deleteScope = _factory.Services.CreateScope())
        {
            ActAs(deleteScope, "planner-1");
            (await Skill<DeleteWorkSkill>(deleteScope).ExecuteAsync(Ctx(), WorkId(parentId))).Success.ShouldBeTrue();
        }

        await BackdateDeleteAsync(parentId, TimeSpan.FromMinutes(5));

        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var plannerAttempt = await Skill<RestoreWorkSkill>(scope).ExecuteAsync(Ctx(), WorkId(parentId));

        plannerAttempt.Success.ShouldBeFalse();
        plannerAttempt.Message.ShouldContain("expired");
        (await StoredAsync(parentId)).IsDeleted.ShouldBeTrue();

        using var adminScope = _factory.Services.CreateScope();
        ActAs(adminScope, "admin-1", Roles.Admin);
        var adminAttempt = await Skill<RestoreWorkSkill>(adminScope).ExecuteAsync(Ctx(), WorkId(parentId));

        adminAttempt.Success.ShouldBeTrue(adminAttempt.Message);
        (await StoredAsync(parentId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task Restore_OfAnotherUsersDelete_ReadsAsNotFound_AndChangesNothing()
    {
        var (parentId, _) = await SeedContainerWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        (await Skill<DeleteWorkSkill>(scope).ExecuteAsync(Ctx(), WorkId(parentId))).Success.ShouldBeTrue();

        ActAs(scope, "planner-2");
        var result = await Skill<RestoreWorkSkill>(scope).ExecuteAsync(Ctx(), WorkId(parentId));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        (await StoredAsync(parentId)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task Restore_WithoutAnyHttpContext_IsReportedHonestly_NotAsAnError()
    {
        var (parentId, _) = await SeedContainerWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        (await Skill<DeleteWorkSkill>(scope).ExecuteAsync(Ctx(), WorkId(parentId))).Success.ShouldBeTrue();

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var result = await Skill<RestoreWorkSkill>(scope).ExecuteAsync(Ctx(), WorkId(parentId));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        result.Message.ShouldContain("identity");
        (await StoredAsync(parentId)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task Reassign_MovesTheWorkAndItsContainerChildToTheTarget_AndTheHoursFollow()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var sourceBefore = await HoursAsync(scope, _sourceClientId);
        var targetBefore = await HoursAsync(scope, _targetClientId);

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = parentId.ToString(),
            ["targetClientId"] = _targetClientId.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        (await StoredAsync(parentId)).ClientId.ShouldBe(_targetClientId);
        (await StoredAsync(childId)).ClientId.ShouldBe(_targetClientId, "container children travel with the parent");
        System.Text.Json.JsonSerializer.Serialize(result.Data).ShouldContain(_sourceClientId.ToString());
        (await HoursAsync(scope, _sourceClientId)).ShouldBe(sourceBefore - SeedWorkTime);
        (await HoursAsync(scope, _targetClientId)).ShouldBe(targetBefore + SeedWorkTime);
    }

    [Test]
    public async Task Reassign_OfAScenarioWork_KeepsItInItsScenario()
    {
        var scenarioToken = Guid.NewGuid();
        var workId = await SeedWorkAsync(_sourceClientId, _date, analyseToken: scenarioToken);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = workId.ToString(),
            ["targetClientId"] = _targetClientId.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        var stored = await StoredAsync(workId);
        stored.ClientId.ShouldBe(_targetClientId);
        stored.AnalyseToken.ShouldBe(scenarioToken);
    }

    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    public async Task Reassign_OfALockedWork_IsRefused_AndNothingMoves(WorkLockLevel level)
    {
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        await SetLockLevelAsync(workId, level);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = workId.ToString(),
            ["targetClientId"] = _targetClientId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(level.ToString());
        (await StoredAsync(workId)).ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task ReassignCommand_OfAnApprovedWork_AsAPlannerWithoutRole_IsRefusedByTheHandlerItself()
    {
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        await SetLockLevelAsync(workId, WorkLockLevel.Approved);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var mediator = scope.ServiceProvider.GetRequiredService<Klacks.Api.Infrastructure.Mediator.IMediator>();

        await Should.ThrowAsync<Klacks.Api.Domain.Exceptions.InvalidRequestException>(() => mediator.Send(
            new Klacks.Api.Application.Commands.Works.ReassignWorkClientCommand(workId, _targetClientId),
            CancellationToken.None));

        (await StoredAsync(workId)).ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task Reassign_ToAnEmployeeWhoseDayIsSealed_IsRefused_AndNothingMoves()
    {
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        await SeedGlobalSealAsync(_date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workId"] = workId.ToString(),
            ["targetClientId"] = _targetClientId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("sealed");
        (await StoredAsync(workId)).ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task BulkDelete_DeletesTheRangeWorksAndTheirContainerChildren_NotTheOtherDays()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        var secondId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        var outsideId = await SeedWorkAsync(_sourceClientId, _date.AddDays(10));
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");

        var parameters = BulkParameters(_date, _date.AddDays(2));
        var result = await Skill<BulkDeleteWorksSkill>(scope).ExecuteAsync(await BoundCtxAsync(scope, parameters), parameters);

        result.Success.ShouldBeTrue(result.Message);
        (await StoredAsync(parentId)).IsDeleted.ShouldBeTrue();
        (await StoredAsync(secondId)).IsDeleted.ShouldBeTrue();
        (await StoredAsync(childId)).IsDeleted.ShouldBeTrue("the container child must not stay behind");
        (await StoredAsync(outsideId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BulkDelete_ConfirmationPreview_ListsTheWorks_AndWritesNothing()
    {
        await SeedWorkAsync(_sourceClientId, _date);
        await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var provider = ActivatorUtilities.CreateInstance<BulkWorkDeletionConfirmationPreviewProvider>(scope.ServiceProvider);

        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), BulkParameters(_date, _date.AddDays(1)));

        preview.IsRefusal.ShouldBeFalse(preview.Text);
        preview.Text.ShouldContain("2 work");
        preview.Text.ShouldContain(_date.ToString("yyyy-MM-dd"));
        (await _context.Work.AsNoTracking().CountAsync(w => w.ClientId == _sourceClientId)).ShouldBe(2);
    }

    [Test]
    public async Task BulkDelete_ConfirmedReplayOfItsOwnPreview_DeletesExactlyThePreviewedWorks()
    {
        var firstId = await SeedWorkAsync(_sourceClientId, _date);
        var secondId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        var outsideId = await SeedWorkAsync(_sourceClientId, _date.AddDays(10));
        var token = await HoldBulkDeleteAsync(_date, _date.AddDays(1));

        var result = await RedeemAsync(token);

        result.Success.ShouldBeTrue(result.Message);
        (await StoredAsync(firstId)).IsDeleted.ShouldBeTrue();
        (await StoredAsync(secondId)).IsDeleted.ShouldBeTrue();
        (await StoredAsync(outsideId)).IsDeleted.ShouldBeFalse();
        (await PendingRowCountAsync(token)).ShouldBe(0);
    }

    [Test]
    public async Task BulkDelete_AWorkAddedAfterThePreview_DeletesNothing_SpendsTheToken_AndTheNextCallIsHeldAgain()
    {
        var firstId = await SeedWorkAsync(_sourceClientId, _date);
        var secondId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        var token = await HoldBulkDeleteAsync(_date, _date.AddDays(2));
        var lateId = await SeedWorkAsync(_sourceClientId, _date.AddDays(2));

        var result = await RedeemAsync(token);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 3 apply now (1 new, 0 no longer there)");
        (await StoredAsync(firstId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(secondId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(lateId)).IsDeleted.ShouldBeFalse();
        (await PendingRowCountAsync(token)).ShouldBe(0);

        var secondTry = await RedeemAsync(token);
        secondTry.Success.ShouldBeFalse();
        (await StoredAsync(firstId)).IsDeleted.ShouldBeFalse();

        var freshToken = await HoldBulkDeleteAsync(_date, _date.AddDays(2));
        freshToken.ShouldNotBe(token);
        (await RedeemAsync(freshToken)).Success.ShouldBeTrue();
        (await StoredAsync(lateId)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task BulkDelete_AWorkGoneAfterThePreview_DeletesNothing()
    {
        var firstId = await SeedWorkAsync(_sourceClientId, _date);
        var secondId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        var token = await HoldBulkDeleteAsync(_date, _date.AddDays(1));
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE work SET is_deleted = true, deleted_time = now() WHERE id = {0}", firstId);

        var result = await RedeemAsync(token);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 1 apply now (0 new, 1 no longer there)");
        (await StoredAsync(secondId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BulkDelete_ASwappedWorkAfterThePreview_DeletesNothing_ReportingOneNewAndOneGone()
    {
        var firstId = await SeedWorkAsync(_sourceClientId, _date);
        var secondId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        var token = await HoldBulkDeleteAsync(_date, _date.AddDays(1));
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE work SET is_deleted = true, deleted_time = now() WHERE id = {0}", secondId);
        var replacementId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));

        var result = await RedeemAsync(token);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 2 apply now (1 new, 1 no longer there)");
        (await StoredAsync(firstId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(replacementId)).IsDeleted.ShouldBeFalse();
        var swappedOut = await StoredAsync(secondId);
        swappedOut.IsDeleted.ShouldBeTrue("the work that left the range stays as it was");
        (await PendingRowCountAsync(token)).ShouldBe(0);
        (await _context.Work.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(w => w.ClientId == _sourceClientId && w.IsDeleted)).ShouldBe(1);
    }

    [Test]
    public async Task BulkDelete_AContainerChildAppearingAfterThePreview_DeletesNothing_AndTheNextPreviewNamesIt()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        var token = await HoldBulkDeleteAsync(_date, _date);
        var lateChild = NewWork(_sourceClientId, _date, parentWorkId: parentId);
        lateChild.WorkTime = 0m;
        await _context.Work.AddAsync(lateChild);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await RedeemAsync(token);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 3 apply now (1 new, 0 no longer there)");
        (await StoredAsync(parentId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(childId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(lateChild.Id)).IsDeleted.ShouldBeFalse();
        (await PendingRowCountAsync(token)).ShouldBe(0);

        var fresh = await HoldBulkDeleteRawAsync(_date, _date);
        fresh.Type.ShouldBe(SkillResultType.Confirmation, fresh.Message);
        fresh.Message!.ShouldContain("together with 2 container entries");
    }

    [Test]
    public async Task BulkDelete_AContainerChildBreakAppearingAfterThePreview_DeletesNothing_AndTheNextPreviewNamesIt()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        var token = await HoldBulkDeleteAsync(_date, _date);
        var lateBreakId = await SeedChildBreakAsync(parentId, _date);

        var result = await RedeemAsync(token);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 3 apply now (1 new, 0 no longer there)");
        (await StoredAsync(parentId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(childId)).IsDeleted.ShouldBeFalse();
        (await StoredBreakAsync(lateBreakId)).IsDeleted.ShouldBeFalse();
        (await PendingRowCountAsync(token)).ShouldBe(0);

        var fresh = await HoldBulkDeleteRawAsync(_date, _date);
        fresh.Type.ShouldBe(SkillResultType.Confirmation, fresh.Message);
        fresh.Message!.ShouldContain("together with 2 container entries");

        (await RedeemAsync((string)fresh.Metadata!["confirmationToken"])).Success.ShouldBeTrue();
        (await StoredAsync(parentId)).IsDeleted.ShouldBeTrue();
        (await StoredAsync(childId)).IsDeleted.ShouldBeTrue();
        (await StoredBreakAsync(lateBreakId)).IsDeleted.ShouldBeTrue("the confirmed child break goes with its parent");
    }

    [Test]
    public async Task BulkDeleteCommand_WithContainerChildrenThatDifferFromTheExpectedOnes_RefusesAndCommitsNothing()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        var secondId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        var lateBreakId = await SeedChildBreakAsync(parentId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var mediator = scope.ServiceProvider.GetRequiredService<Klacks.Api.Infrastructure.Mediator.IMediator>();
        var request = new Klacks.Api.Application.DTOs.Schedules.BulkDeleteWorksRequest { WorkIds = [parentId, secondId] };

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => mediator.Send(
            new Klacks.Api.Application.Commands.Works.BulkDeleteWorksCommand(
                request, new ContainerChildIds([childId], [])),
            CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ConfirmationBindingMessages.ContainerChildrenChanged, 1, 2));
        (await StoredAsync(parentId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(childId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(secondId)).IsDeleted.ShouldBeFalse();
        (await StoredBreakAsync(lateBreakId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BulkDeleteCommand_WithExactlyTheExpectedContainerChildren_DeletesTheWorksAndTheirChildren()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        var breakId = await SeedChildBreakAsync(parentId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var mediator = scope.ServiceProvider.GetRequiredService<Klacks.Api.Infrastructure.Mediator.IMediator>();

        var response = await mediator.Send(
            new Klacks.Api.Application.Commands.Works.BulkDeleteWorksCommand(
                new Klacks.Api.Application.DTOs.Schedules.BulkDeleteWorksRequest { WorkIds = [parentId] },
                new ContainerChildIds([childId], [breakId])),
            CancellationToken.None);

        response.DeletedIds.ShouldBe([parentId]);
        (await StoredAsync(parentId)).IsDeleted.ShouldBeTrue();
        (await StoredAsync(childId)).IsDeleted.ShouldBeTrue();
        (await StoredBreakAsync(breakId)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task BulkDelete_AContainerChildTheConfirmationWasGivenFor_IsDeletedWithItsParent()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        var held = await HoldBulkDeleteRawAsync(_date, _date);
        held.Type.ShouldBe(SkillResultType.Confirmation, held.Message);
        held.Message!.ShouldContain("together with 1 container entry");

        var result = await RedeemAsync((string)held.Metadata!["confirmationToken"]);

        result.Success.ShouldBeTrue(result.Message);
        (await StoredAsync(parentId)).IsDeleted.ShouldBeTrue();
        (await StoredAsync(childId)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task BulkDelete_AWorkLockedAfterThePreview_IsRefusedByThePlanner_AndNothingIsDeleted()
    {
        var openId = await SeedWorkAsync(_sourceClientId, _date);
        var lockedId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        var token = await HoldBulkDeleteAsync(_date, _date.AddDays(1));
        await SetLockLevelAsync(lockedId, WorkLockLevel.Confirmed);

        var result = await RedeemAsync(token);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("locked");
        (await StoredAsync(openId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(lockedId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BulkDelete_ATokenSentWithTheCallItself_IsRefused_AndStillRedeemsThroughTheConfirmSkill()
    {
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        var token = await HoldBulkDeleteAsync(_date, _date);
        var parameters = BulkParameters(_date, _date);
        parameters[AutonomyDefaults.ConfirmationTokenParameter] = token;

        using (var scope = _factory.Services.CreateScope())
        {
            ActAs(scope, "planner-1");
            var gate = scope.ServiceProvider.GetRequiredService<IAutonomyGate>();
            var direct = await gate.CheckAsync(BulkDeleteDescriptor(), TurnCtx(true), parameters);

            direct.ShouldNotBeNull();
            direct.Type.ShouldBe(SkillResultType.Error);
            direct.Message.ShouldBe(string.Format(ConfirmationBindingMessages.DirectRedemptionRefused, BulkDeleteWorksSkill.SkillName));
        }

        (await StoredAsync(workId)).IsDeleted.ShouldBeFalse();
        (await PendingRowCountAsync(token)).ShouldBe(1);
        (await RedeemAsync(token)).Success.ShouldBeTrue();
        (await StoredAsync(workId)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task BulkDelete_TheBindingColumn_RoundTripsOnPostgres()
    {
        var binding = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") });
        var store = _factory.Services.GetRequiredService<IPendingConfirmationStore>();

        var token = store.Create(
            _confirmingUserId, BulkDeleteWorksSkill.SkillName, BulkParameters(_date, _date),
            PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat, binding);

        (await _context.Database
            .SqlQuery<string>($"SELECT binding_json AS \"Value\" FROM pending_confirmations WHERE token = {token}")
            .SingleAsync()).ShouldContain(binding.Fingerprint);
        var consumed = store.Consume(token, _confirmingUserId);
        consumed.ShouldNotBeNull();
        consumed.Binding.ShouldBe(binding);
    }

    [Test]
    public async Task BulkDelete_WithoutABinding_NeverDeletes_EvenWithTheGateBypassed()
    {
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var bypassed = Ctx() with { BypassAutonomyGate = true };

        var result = await Skill<BulkDeleteWorksSkill>(scope).ExecuteAsync(bypassed, BulkParameters(_date, _date));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("only runs as the confirmed replay");
        (await StoredAsync(workId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BulkDelete_WithASealedDayInTheRange_DeletesNothing()
    {
        var firstId = await SeedWorkAsync(_sourceClientId, _date);
        var sealedId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        await SeedGlobalSealAsync(_date.AddDays(1));

        var result = await HoldBulkDeleteRawAsync(_date, _date.AddDays(1));

        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldContain("sealed");
        (await StoredAsync(firstId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(sealedId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BulkDelete_WithAConfirmedWorkInTheRange_DeletesNothing()
    {
        var openId = await SeedWorkAsync(_sourceClientId, _date);
        var lockedId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        await SetLockLevelAsync(lockedId, WorkLockLevel.Confirmed);

        var result = await HoldBulkDeleteRawAsync(_date, _date.AddDays(1));

        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldContain("locked");
        (await StoredAsync(openId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(lockedId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BulkDelete_OverTheCap_IsRefusedAsAWhole_AndDeletesNothing()
    {
        var count = BulkWorkDeletionDefaults.MaxWorksPerCall + 1;
        for (var i = 0; i < count; i++)
        {
            _context.Work.Add(NewWork(_sourceClientId, _date.AddDays(i)));
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await HoldBulkDeleteRawAsync(_date, _date.AddDays(count));

        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldContain(count.ToString());
        (await _context.Work.AsNoTracking().CountAsync(w => w.ClientId == _sourceClientId && !w.IsDeleted)).ShouldBe(count);
    }

    [Test]
    public async Task Reassign_ToATargetOutsideTheCallersGroups_ReadsLikeAnUnknownEmployee_AndNothingMoves()
    {
        var hiddenGroupId = await SeedGroupAsync("HiddenTarget");
        await AddToGroupAsync(_targetClientId, hiddenGroupId);
        var plannerId = await SeedPlannerAsync();
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, plannerId);
        var unknownId = Guid.NewGuid();

        var hidden = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(workId, _targetClientId));
        var unknown = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(workId, unknownId));

        hidden.Success.ShouldBeFalse();
        unknown.Success.ShouldBeFalse();
        hidden.Message!.Replace(_targetClientId.ToString(), "<id>")
            .ShouldBe(unknown.Message!.Replace(unknownId.ToString(), "<id>"));
        (await StoredAsync(workId)).ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task Reassign_OfAWorkHeldByAHiddenEmployee_ReadsLikeAnUnknownWork_AndNothingMoves()
    {
        var hiddenGroupId = await SeedGroupAsync("HiddenHolder");
        await AddToGroupAsync(_sourceClientId, hiddenGroupId);
        var plannerId = await SeedPlannerAsync();
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, plannerId);
        var unknownWorkId = Guid.NewGuid();

        var hidden = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(workId, _targetClientId));
        var unknown = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(unknownWorkId, _targetClientId));

        hidden.Success.ShouldBeFalse();
        hidden.Message!.Replace(workId.ToString(), "<id>")
            .ShouldBe(unknown.Message!.Replace(unknownWorkId.ToString(), "<id>"));
        (await StoredAsync(workId)).ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task BulkDelete_OfAnEmployeeOutsideTheCallersGroups_ReadsLikeAnUnknownEmployee_AndDeletesNothing()
    {
        var hiddenGroupId = await SeedGroupAsync("HiddenEmployee");
        await AddToGroupAsync(_sourceClientId, hiddenGroupId);
        var plannerId = await SeedPlannerAsync();
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, plannerId);
        var unknownId = Guid.NewGuid();
        var unknownParameters = BulkParameters(_date, _date);
        unknownParameters["clientId"] = unknownId.ToString();

        var gate = scope.ServiceProvider.GetRequiredService<IAutonomyGate>();

        var hidden = await gate.CheckAsync(BulkDeleteDescriptor(), TurnCtx(false), BulkParameters(_date, _date));
        var unknown = await gate.CheckAsync(BulkDeleteDescriptor(), TurnCtx(false), unknownParameters);

        hidden.ShouldNotBeNull();
        unknown.ShouldNotBeNull();
        hidden.Type.ShouldBe(SkillResultType.Error);
        hidden.Message!.Replace(_sourceClientId.ToString(), "<id>")
            .ShouldBe(unknown.Message!.Replace(unknownId.ToString(), "<id>"));
        (await StoredAsync(workId)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BulkDelete_ThenRestoreOneByOne_BringsBackTheContainerParentWithItsChild_AndTheHours()
    {
        var (parentId, childId) = await SeedContainerWorkAsync(_sourceClientId, _date);
        var secondId = await SeedWorkAsync(_sourceClientId, _date.AddDays(1));
        decimal hoursBefore;
        List<Guid> deletedIds;
        using (var deleteScope = _factory.Services.CreateScope())
        {
            ActAs(deleteScope, "planner-1");
            hoursBefore = await HoursAsync(deleteScope, _sourceClientId);
            var deleteParameters = BulkParameters(_date, _date.AddDays(1));
            var deleted = await Skill<BulkDeleteWorksSkill>(deleteScope).ExecuteAsync(
                await BoundCtxAsync(deleteScope, deleteParameters), deleteParameters);
            deleted.Success.ShouldBeTrue(deleted.Message);
            deletedIds = ReadDeletedWorkIds(deleted);
        }

        deletedIds.ShouldBe([parentId, secondId], ignoreOrder: true);
        (await StoredAsync(childId)).IsDeleted.ShouldBeTrue();

        using var restoreScope = _factory.Services.CreateScope();
        ActAs(restoreScope, "planner-1");
        foreach (var id in deletedIds)
        {
            var restored = await Skill<RestoreWorkSkill>(restoreScope).ExecuteAsync(Ctx(), WorkId(id));
            restored.Success.ShouldBeTrue(restored.Message);
        }

        (await StoredAsync(parentId)).IsDeleted.ShouldBeFalse();
        (await StoredAsync(childId)).IsDeleted.ShouldBeFalse("the child the bulk delete took away comes back with its parent");
        (await StoredAsync(secondId)).IsDeleted.ShouldBeFalse();
        (await HoursAsync(restoreScope, _sourceClientId)).ShouldBe(hoursBefore);
    }

    [Test]
    public async Task Reassign_ToAnEmployeeWhoseGroupIsSealedThatDay_IsRefusedForTheTargetOnly_AndNothingMoves()
    {
        var groupId = await SeedGroupAsync("SealedTargetGroup");
        await AddToGroupAsync(_targetClientId, groupId, validFrom: _date.AddDays(-30));
        await AddMembershipAsync(_targetClientId, _date.AddDays(-30));
        await SeedGroupSealAsync(_date, groupId);
        var plannerId = await SeedPlannerAsync(groupId);
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, plannerId);

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(workId, _targetClientId));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("sealed");
        result.Message.ShouldContain(SeedPrefix + "Target");
        result.Message.ShouldNotContain("current holder");
        (await StoredAsync(workId)).ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task Reassign_ToAnEmployeeBeforeTheirMembershipStarts_IsRefusedForSkillAndRestAlike_AndNothingMoves()
    {
        await AddMembershipAsync(_targetClientId, _date.AddDays(1));
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(workId, _targetClientId));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not handed over");
        result.Message.ShouldContain("membership");
        var mediator = scope.ServiceProvider.GetRequiredService<Klacks.Api.Infrastructure.Mediator.IMediator>();
        var rest = await Should.ThrowAsync<InvalidRequestException>(() => mediator.Send(
            new Klacks.Api.Application.Commands.Works.ReassignWorkClientCommand(workId, _targetClientId),
            CancellationToken.None));
        rest.Message.ShouldContain("membership");
        (await StoredAsync(workId)).ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task Reassign_ToAnEmployeeWhoseMembershipCoversTheDay_IsAllowed_OnTheBoundaryDayToo()
    {
        await AddMembershipAsync(_targetClientId, _date, _date);
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(workId, _targetClientId));

        result.Success.ShouldBeTrue(result.Message);
        (await StoredAsync(workId)).ClientId.ShouldBe(_targetClientId);
    }

    [Test]
    public async Task Reassign_ToAnEmployeeOutsideTheirGroupPeriodInTheSelectedGroup_IsRefused_AndNothingMoves()
    {
        var groupId = await SeedGroupAsync("EndedGroupPeriod");
        await AddToGroupAsync(_targetClientId, groupId, validUntil: _date.AddDays(-1));
        var plannerId = await SeedPlannerAsync(groupId);
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, plannerId, selectedGroupId: groupId);

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(workId, _targetClientId));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not handed over");
        result.Message.ShouldContain("group period");
        (await StoredAsync(workId)).ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task Reassign_ToAnEmployeeInsideTheirGroupPeriodInTheSelectedGroup_IsAllowed()
    {
        var groupId = await SeedGroupAsync("OpenGroupPeriod");
        await AddToGroupAsync(_targetClientId, groupId, validFrom: _date.AddDays(-5), validUntil: _date.AddDays(5));
        var plannerId = await SeedPlannerAsync(groupId);
        var workId = await SeedWorkAsync(_sourceClientId, _date);
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, plannerId, selectedGroupId: groupId);

        var result = await Skill<ReassignWorkSkill>(scope).ExecuteAsync(Ctx(), ReassignById(workId, _targetClientId));

        result.Success.ShouldBeTrue(result.Message);
        (await StoredAsync(workId)).ClientId.ShouldBe(_targetClientId);
    }

    private static Dictionary<string, object> ReassignById(Guid workId, Guid targetClientId) => new()
    {
        ["workId"] = workId.ToString(),
        ["targetClientId"] = targetClientId.ToString()
    };

    private static List<Guid> ReadDeletedWorkIds(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data)
            .GetProperty("DeletedWorkIds")
            .EnumerateArray()
            .Select(element => element.GetGuid())
            .ToList();

    private async Task<Guid> SeedGroupAsync(string suffix)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = SeedPrefix + suffix,
            Description = "Grid intent skills integration test",
            ValidFrom = DateTime.UtcNow.AddYears(-1)
        };
        _context.Group.Add(group);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _groupIds.Add(group.Id);
        return group.Id;
    }

    private async Task AddToGroupAsync(Guid clientId, Guid groupId, DateOnly? validFrom = null, DateOnly? validUntil = null)
    {
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            ValidFrom = validFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ValidUntil = validUntil?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task AddMembershipAsync(Guid clientId, DateOnly validFrom, DateOnly? validUntil = null)
    {
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = validFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ValidUntil = validUntil?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<string> SeedPlannerAsync(params Guid[] visibleGroupIds)
    {
        var email = $"{SeedPrefix}planner-{Guid.NewGuid():N}@example.invalid";
        var planner = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = SeedPrefix,
            LastName = "Planner",
            SecurityStamp = Guid.NewGuid().ToString()
        };
        _context.AppUser.Add(planner);
        await _context.SaveChangesAsync();
        foreach (var groupId in visibleGroupIds)
        {
            _context.GroupVisibility.Add(new GroupVisibility
            {
                Id = Guid.NewGuid(),
                AppUserId = planner.Id,
                GroupId = groupId
            });
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _plannerId = planner.Id;
        return planner.Id;
    }

    private async Task SeedGroupSealAsync(DateOnly date, Guid groupId)
    {
        var seal = new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = date,
            GroupId = groupId,
            Level = WorkLockLevel.Closed,
            Reason = SeedPrefix + "GroupSeal",
            SealedAt = DateTime.UtcNow,
            SealedBy = SeedPrefix + "Test"
        };
        _context.SealedDay.Add(seal);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _sealedDayIds.Add(seal.Id);
    }

    private Dictionary<string, object> BulkParameters(DateOnly from, DateOnly until) => new()
    {
        ["clientId"] = _sourceClientId.ToString(),
        ["fromDate"] = from.ToString("yyyy-MM-dd"),
        ["untilDate"] = until.ToString("yyyy-MM-dd")
    };

    private static Dictionary<string, object> WorkId(Guid id) => new() { ["workId"] = id.ToString() };

    private static SkillDescriptor BulkDeleteDescriptor() =>
        new(BulkDeleteWorksSkill.SkillName, "integration", SkillCategory.Crud, [], [], [], null);

    private SkillExecutionContext TurnCtx(bool explicitYes) => new()
    {
        UserId = _confirmingUserId,
        TenantId = Guid.NewGuid(),
        UserName = "integration-tester",
        UserLanguage = "en",
        UserPermissions = new List<string> { "CanEditSchedule" },
        UserConfirmation = explicitYes ? UserConfirmationEvidence.ExplicitConfirmationReply : UserConfirmationEvidence.None
    };

    private static async Task<SkillExecutionContext> BoundCtxAsync(IServiceScope scope, Dictionary<string, object> parameters)
    {
        var provider = ActivatorUtilities.CreateInstance<BulkWorkDeletionConfirmationPreviewProvider>(scope.ServiceProvider);
        var preview = await provider.BuildAsync(BulkDeleteWorksSkill.SkillName, Ctx(), parameters);
        preview.IsRefusal.ShouldBeFalse(preview.Text);
        return Ctx() with { ConfirmedActionBinding = preview.Binding };
    }

    private async Task<SkillResult> HoldBulkDeleteRawAsync(DateOnly from, DateOnly until)
    {
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var gate = scope.ServiceProvider.GetRequiredService<IAutonomyGate>();

        var held = await gate.CheckAsync(BulkDeleteDescriptor(), TurnCtx(false), BulkParameters(from, until));

        held.ShouldNotBeNull();
        return held;
    }

    private async Task<string> HoldBulkDeleteAsync(DateOnly from, DateOnly until)
    {
        var held = await HoldBulkDeleteRawAsync(from, until);
        held.Type.ShouldBe(SkillResultType.Confirmation, held.Message);
        return (string)held.Metadata!["confirmationToken"];
    }

    private async Task<SkillResult> RedeemAsync(string token)
    {
        using var scope = _factory.Services.CreateScope();
        ActAs(scope, "planner-1");
        var executor = Substitute.For<ISkillExecutor>();
        executor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call => Skill<BulkDeleteWorksSkill>(scope).ExecuteAsync(
                call.Arg<SkillExecutionContext>(),
                call.Arg<SkillInvocation>().Parameters.ToDictionary(entry => entry.Key, entry => entry.Value)));
        var confirm = ActivatorUtilities.CreateInstance<ConfirmPendingActionSkill>(scope.ServiceProvider, executor);

        return await confirm.ExecuteAsync(
            TurnCtx(true), new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });
    }

    private async Task<int> PendingRowCountAsync(string token) =>
        await _context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pending_confirmations WHERE token = {token}")
            .SingleAsync();

    private static T Skill<T>(IServiceScope scope) where T : class
        => ActivatorUtilities.CreateInstance<T>(scope.ServiceProvider);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "integration-tester",
        UserLanguage = "en",
        UserPermissions = new List<string> { "CanEditSchedule" }
    };

    private static void ActAs(IServiceScope scope, string userId, params string[] roles)
        => ActAs(scope, userId, null, roles);

    private static void ActAs(IServiceScope scope, string userId, Guid? selectedGroupId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var identity = new ClaimsIdentity(claims, "IntegrationTest");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        if (selectedGroupId.HasValue)
        {
            httpContext.Request.Headers[HttpHeaderNames.SelectedGroup] = selectedGroupId.Value.ToString();
        }

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
    }

    private async Task<decimal> HoursAsync(IServiceScope scope, Guid clientId)
    {
        var periodHours = scope.ServiceProvider.GetRequiredService<IPeriodHoursService>();
        var (start, end) = await periodHours.GetPeriodBoundariesAsync(_date);
        var hours = await periodHours.CalculatePeriodHoursAsync(clientId, start, end);
        return hours.Hours;
    }

    private async Task<Work> StoredAsync(Guid id)
    {
        _context.ChangeTracker.Clear();
        return await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == id);
    }

    private async Task<Break> StoredBreakAsync(Guid id)
    {
        _context.ChangeTracker.Clear();
        return await _context.Break.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == id);
    }

    private async Task<Guid> SeedChildBreakAsync(Guid parentWorkId, DateOnly date)
    {
        var absenceId = await _context.Absence.Where(a => !a.IsDeleted).Select(a => a.Id).FirstOrDefaultAsync();
        if (absenceId == Guid.Empty)
        {
            throw new InvalidOperationException("No absence found in the database for the child break seed.");
        }

        var childBreak = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _sourceClientId,
            CurrentDate = date,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = 0m,
            AbsenceId = absenceId,
            ParentWorkId = parentWorkId,
            Information = SeedPrefix + "ChildBreak"
        };
        _context.Break.Add(childBreak);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _breakIds.Add(childBreak.Id);
        return childBreak.Id;
    }

    private Work NewWork(Guid clientId, DateOnly date, Guid? parentWorkId = null, Guid? analyseToken = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = _shiftId,
        CurrentDate = date,
        StartTime = ShiftStart,
        EndTime = ShiftEnd,
        WorkTime = SeedWorkTime,
        ParentWorkId = parentWorkId,
        AnalyseToken = analyseToken
    };

    private async Task<Guid> SeedWorkAsync(Guid clientId, DateOnly date, Guid? analyseToken = null)
    {
        var work = NewWork(clientId, date, analyseToken: analyseToken);
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return work.Id;
    }

    private async Task<(Guid ParentId, Guid ChildId)> SeedContainerWorkAsync(Guid clientId, DateOnly date)
    {
        var parent = NewWork(clientId, date);
        await _context.Work.AddAsync(parent);
        await _context.SaveChangesAsync();
        var child = NewWork(clientId, date, parentWorkId: parent.Id);
        child.WorkTime = 0m;
        await _context.Work.AddAsync(child);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (parent.Id, child.Id);
    }

    private Task SetLockLevelAsync(Guid workId, WorkLockLevel level) => _context.Work
        .Where(w => w.Id == workId)
        .ExecuteUpdateAsync(s => s.SetProperty(w => w.LockLevel, level));

    private Task BackdateDeleteAsync(Guid workId, TimeSpan age)
    {
        var deletedAt = DateTime.UtcNow - age;
        return _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == workId || w.ParentWorkId == workId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.DeletedTime, deletedAt));
    }

    private async Task SeedGlobalSealAsync(DateOnly date)
    {
        var seal = new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = date,
            GroupId = null,
            Level = WorkLockLevel.Closed,
            Reason = SeedPrefix + "Seal",
            SealedAt = DateTime.UtcNow,
            SealedBy = SeedPrefix + "Test"
        };
        _context.SealedDay.Add(seal);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _sealedDayIds.Add(seal.Id);
    }
}
