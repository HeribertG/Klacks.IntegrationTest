// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The cleanup of a stopped turn against real Postgres: its Dispatched UiAction rows become Cancelled and
/// nothing else is touched (other turns, decided rows), and a late outcome report for a Cancelled row changes
/// nothing. The rest of the fixture is M-3: the cleanup is one ExecuteUpdateAsync statement, so it neither
/// commits whatever else is pending in the request's shared DbContext (it used to load the rows and call
/// SaveChanges, which wrote foreign pending changes with it) nor fails because an earlier failed write left
/// its entity Added in the change tracker (the rows used to stay Dispatched, directly and through the
/// recorder). The row selection and the count it reports are pinned here too, because the in-memory provider
/// cannot run ExecuteUpdateAsync.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[TestFixture]
public class StoppedTurnCleanupPostgresTests : StopTurnPostgresTestBase
{
    private const string PendingDescription = Prefix + "pending-description-of-another-writer";

    [Test]
    public async Task TheCleanup_CancelsTheDispatchedRowsOfTheStoppedTurnOnly()
    {
        var stopped = Guid.NewGuid();
        var other = Guid.NewGuid();
        var dispatched = await SeedAsync(stopped, UiActionStatus.Dispatched);
        var completed = await SeedAsync(stopped, UiActionStatus.Completed, success: true);
        var plain = await SeedAsync(stopped, null, success: true);
        var otherDispatched = await SeedAsync(other, UiActionStatus.Dispatched);

        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IStoppedTurnCleanup>().CleanUpAsync(UserId, stopped, CancellationToken.None);

        (await StatusOfAsync(dispatched)).ShouldBe((int)UiActionStatus.Cancelled);
        (await SuccessOfAsync(dispatched)).ShouldBe(false);
        (await StatusOfAsync(completed)).ShouldBe((int)UiActionStatus.Completed);
        (await SuccessOfAsync(completed)).ShouldBe(true);
        (await StatusOfAsync(plain)).ShouldBeNull();
        (await StatusOfAsync(otherDispatched)).ShouldBe((int)UiActionStatus.Dispatched);
    }

    [Test]
    public async Task TheRepository_ReportsTheNumberOfClosedRowsAndLeavesOutcomesAndNonUiRowsAlone()
    {
        var turn = Guid.NewGuid();
        await SeedAsync(turn, UiActionStatus.Dispatched, success: true);
        await SeedAsync(turn, UiActionStatus.Dispatched, success: true);
        var failed = await SeedAsync(turn, UiActionStatus.Failed);
        var completed = await SeedAsync(turn, UiActionStatus.Completed, success: true);
        var plain = await SeedAsync(turn, null, success: true);
        using var scope = Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISkillUsageRepository>();

        (await repository.CancelDispatchedForTurnAsync(turn)).ShouldBe(2);
        (await repository.CancelDispatchedForTurnAsync(turn)).ShouldBe(0);
        (await repository.CancelDispatchedForTurnAsync(Guid.NewGuid())).ShouldBe(0);

        (await StatusOfAsync(failed)).ShouldBe((int)UiActionStatus.Failed);
        (await StatusOfAsync(completed)).ShouldBe((int)UiActionStatus.Completed);
        (await SuccessOfAsync(completed)).ShouldBe(true);
        (await StatusOfAsync(plain)).ShouldBeNull();
        (await SuccessOfAsync(plain)).ShouldBe(true);
    }

    [Test]
    public async Task ALateBrowserReportForACancelledRow_ChangesNothing()
    {
        var turn = Guid.NewGuid();
        var row = await SeedAsync(turn, UiActionStatus.Dispatched);
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IStoppedTurnCleanup>().CleanUpAsync(UserId, turn, CancellationToken.None);
        var handler = new ReportUiActionResultCommandHandler(
            scope.ServiceProvider.GetRequiredService<ISkillUsageRepository>(),
            NullLogger<ReportUiActionResultCommandHandler>.Instance);

        var result = await handler.Handle(
            new ReportUiActionResultCommand { UserId = UserId, TrackingId = row, Status = "completed" },
            CancellationToken.None);

        result.ShouldBe(new ReportUiActionResultResult(Found: true, Updated: false, Error: null));
        (await StatusOfAsync(row)).ShouldBe((int)UiActionStatus.Cancelled);
        (await SuccessOfAsync(row)).ShouldBe(false);
    }

    [Test]
    public async Task M3_TheCleanupDoesNotCommitWhateverElseIsPendingInTheSharedContext()
    {
        var turn = Guid.NewGuid();
        var row = await SeedAsync(turn, UiActionStatus.Dispatched);
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var model = context.Set<LLMModel>().Single(m => m.ModelId == ModelKey);
        model.Description = PendingDescription;

        await scope.ServiceProvider.GetRequiredService<IStoppedTurnCleanup>().CleanUpAsync(UserId, turn, CancellationToken.None);

        (await StatusOfAsync(row)).ShouldBe((int)UiActionStatus.Cancelled);
        (await ScalarAsync("SELECT description FROM llm_models WHERE model_id = @model", ("model", ModelKey)))
            .ShouldNotBe(PendingDescription);
    }

    [Test]
    public async Task M3_AnEarlierWriteThatFailedAndLeftItsEntityInTheTracker_DoesNotBlockTheCleanup()
    {
        var turn = Guid.NewGuid();
        var row = await SeedAsync(turn, UiActionStatus.Dispatched);
        using var scope = Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILLMRepository>();
        var duplicateId = await SeedUsageAsync();
        var modelPk = await ModelPkAsync();

        await Should.ThrowAsync<Exception>(() => repository.TrackUsageAsync(new LLMUsage
        {
            Id = duplicateId,
            UserId = UserId,
            ModelId = modelPk,
            ConversationId = ConversationKey
        }));

        await scope.ServiceProvider.GetRequiredService<IStoppedTurnCleanup>().CleanUpAsync(UserId, turn, CancellationToken.None);

        (await StatusOfAsync(row)).ShouldBe((int)UiActionStatus.Cancelled);
    }

    [Test]
    public async Task M3_ThroughTheRecorder_AFailingUsageInsertNoLongerLeavesTheUiActionRowsDispatched_AndTheAnchorIsStillWritten()
    {
        var turnId = Guid.NewGuid();
        var row = await SeedAsync(turnId, UiActionStatus.Dispatched);
        await SeedUsageAsync(turnId);
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Skills.On(WriteSkill, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return ScriptedSkillBridge.Succeeded();
        });
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill));

        using var scope = Factory.Services.CreateScope();
        var run = Task.Run(() => RunTurnAsync(scope, NewContext("run the first test write action", turnId, stop.Token)));
        await entered.Task.WaitAsync(Patience);
        stop.Cancel();
        release.SetResult();
        var chunks = await run.WaitAsync(Patience);

        chunks.Last().Type.ShouldBe(SseChunkType.Done);
        (await MessagesAsync()).Count.ShouldBe(2);
        (await UsagesAsync()).Count.ShouldBe(1);
        (await AnchorAsync()).ShouldNotBeNull();
        (await StatusOfAsync(row)).ShouldBe((int)UiActionStatus.Cancelled);
    }

    private async Task<Guid> SeedAsync(Guid turnId, UiActionStatus? status, bool success = false)
    {
        await using var context = NewContext();
        var record = new SkillUsageRecord
        {
            Id = Guid.NewGuid(),
            SkillName = Prefix + "ui_action",
            Category = SkillCategory.UI,
            UserId = Guid.Parse(UserId),
            TenantId = Guid.NewGuid(),
            Success = success,
            Timestamp = DateTime.UtcNow,
            TurnId = turnId,
            UiActionStatus = status
        };
        context.Set<SkillUsageRecord>().Add(record);
        await context.SaveChangesAsync();
        return record.Id;
    }

    private async Task<Guid> SeedUsageAsync(Guid? id = null)
    {
        await using var context = NewContext();
        var usage = new LLMUsage
        {
            Id = id ?? Guid.NewGuid(),
            UserId = UserId,
            ModelId = await ModelPkAsync(),
            ConversationId = ConversationKey
        };
        context.Set<LLMUsage>().Add(usage);
        await context.SaveChangesAsync();
        return usage.Id;
    }

    private async Task<Guid> ModelPkAsync() =>
        (Guid)(await ScalarAsync("SELECT id FROM llm_models WHERE model_id = @model", ("model", ModelKey)))!;

    private static async Task<int?> StatusOfAsync(Guid id) =>
        (int?)await ScalarAsync("SELECT ui_action_status FROM skill_usage_records WHERE id = @id", ("id", id));

    private static async Task<bool> SuccessOfAsync(Guid id) =>
        (bool)(await ScalarAsync("SELECT success FROM skill_usage_records WHERE id = @id", ("id", id)))!;
}
