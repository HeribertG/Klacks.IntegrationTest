// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A stopped turn against real Postgres, through the production chat service, recorder, repositories, anchor
/// store and trajectory capture (only the model and the skills are scripted). The stop arrives while a write
/// skill is running: the skill runs to its end, nothing else is executed, and afterwards the turn is on record
/// - history with the interruption marker, one usage row that names the write, the correction anchor, the
/// trajectory row flagged as interrupted - and the client hears turn_stopped and then done only.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[TestFixture]
public class StoppedTurnPersistencePostgresTests : StopTurnPostgresTestBase
{
    private const int TextGraceMs = 150;
    private const string GreetingAnswer = "Hello there, how can I help?";

    [Test]
    public async Task AStopDuringAWriteSkill_LetsTheSkillFinish_AndPersistsTheTurnOnPostgres()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Skills.On(WriteSkill, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return ScriptedSkillBridge.Succeeded();
        });
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.Text("This must never be asked for."));
        var turnId = Guid.NewGuid();
        var context = NewContext("create the employee Anna Meier", turnId, stop.Token);

        using var scope = Factory.Services.CreateScope();
        var run = Task.Run(() => RunTurnAsync(scope, context));
        await entered.Task.WaitAsync(Patience);
        stop.Cancel();
        release.SetResult();
        var chunks = await run.WaitAsync(Patience);

        var tail = chunks.Skip(chunks.FindIndex(chunk => chunk.Type == SseChunkType.TurnStopped)).ToList();
        tail.Select(chunk => chunk.Type).ShouldBe(new[] { SseChunkType.TurnStopped, SseChunkType.Done });
        tail[0].TurnId.ShouldBe(turnId);
        tail[0].ExecutedCount.ShouldBe(1);
        tail[0].ExecutedSkillLabels.ShouldBe(new[] { WriteLabel });
        chunks.ShouldNotContain(chunk => chunk.Type == SseChunkType.Metadata);
        Provider.CallCount.ShouldBe(1);
        Skills.Calls.Count.ShouldBe(1);

        var messages = await MessagesAsync();
        messages.Select(message => message.Role).ShouldBe(new[] { "user", "assistant" });
        messages[1].Content.ShouldBe(TurnInterruptionDefaults.InterruptedMarker);
        messages[1].CreateTime.ShouldBeGreaterThan(messages[0].CreateTime, "the history is read ordered by this time alone");

        var usages = await UsagesAsync();
        usages.Count.ShouldBe(1);
        usages[0][0].ShouldBe(turnId);
        usages[0][1].ShouldBe(false);
        usages[0][3].ShouldBe(Json(new[] { WriteSkill }));

        var anchor = await AnchorAsync();
        anchor.ShouldNotBeNull();
        anchor[3]!.ToString()!.ShouldContain(WriteSkill);

        var trajectories = await WaitForRowsAsync(() => TrajectoriesAsync(turnId), 1);
        trajectories.Count.ShouldBe(1);
        trajectories[0][0].ShouldBe(true);
        trajectories[0][1].ShouldBe(InterruptedTurnPhases.DuringTools);
    }

    [Test]
    public async Task AStopWhileTheTextStreams_StoresThePartialAnswerWithTheMarker_AndClosesTheUiActionRowOfTheTurn()
    {
        using var stop = new CancellationTokenSource();
        var turnId = Guid.NewGuid();
        var uiRow = await SeedDispatchedRowAsync(turnId);
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.EndlessText("Working on it"));
        var textStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Provider.OnStreamStarted = streamToken =>
        {
            if (Provider.CallCount == 2)
            {
                textStarted.TrySetResult();
            }
        };

        using var scope = Factory.Services.CreateScope();
        var run = Task.Run(() => RunTurnAsync(scope, NewContext("create the employee Anna Meier", turnId, stop.Token)));
        await textStarted.Task.WaitAsync(Patience);
        await Task.Delay(TextGraceMs);
        stop.Cancel();
        var chunks = await run.WaitAsync(Patience);

        chunks[^2].Type.ShouldBe(SseChunkType.TurnStopped);
        chunks[^2].ExecutedCount.ShouldBe(1);
        chunks.ShouldNotContain(chunk => chunk.Type == SseChunkType.Metadata);
        var messages = await MessagesAsync();
        messages[1].Content.ShouldStartWith("Working on it");
        messages[1].Content.ShouldEndWith(TurnInterruptionDefaults.InterruptedMarker);
        var trajectories = await WaitForRowsAsync(() => TrajectoriesAsync(turnId), 1);
        trajectories[0][1].ShouldBe(InterruptedTurnPhases.DuringText);
        (await ScalarAsync("SELECT ui_action_status FROM skill_usage_records WHERE id = @id", ("id", uiRow)))
            .ShouldBe((int)UiActionStatus.Cancelled);
    }

    [Test]
    public async Task TheSafetyNetAfterTheStopTail_WritesNothingASecondTime()
    {
        using var stop = new CancellationTokenSource();
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.EndlessText("Working on it"));
        var turnId = Guid.NewGuid();
        Provider.OnStreamStarted = streamToken =>
        {
            if (Provider.CallCount == 2)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TextGraceMs);
                    stop.Cancel();
                });
            }
        };

        using var scope = Factory.Services.CreateScope();
        await RunTurnAsync(scope, NewContext("create the employee Anna Meier", turnId, stop.Token));
        var again = await scope.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>()
            .FinalizeAsync(UserId, turnId, endedInError: false);

        again.ShouldBeNull();
        (await MessagesAsync()).Count.ShouldBe(2);
        (await UsagesAsync()).Count.ShouldBe(1);
    }

    [Test]
    public async Task AConnectionThatDropsMidStreamWithoutAStop_IsPersistedByTheSafetyNetLikeAStop()
    {
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.EndlessText("Working on it"));
        var turnId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ILLMService>();
        var seenAfterTheWrite = false;
        await foreach (var chunk in service.ProcessStreamAsync(NewContext("create the employee Anna Meier", turnId)))
        {
            if (chunk.Type == SseChunkType.FunctionResult)
            {
                seenAfterTheWrite = true;
            }

            if (seenAfterTheWrite && chunk.Type == SseChunkType.Content)
            {
                break;
            }
        }

        var summary = await scope.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>()
            .FinalizeAsync(UserId, turnId, endedInError: false);

        summary.ShouldNotBeNull();
        summary.ExecutedCount.ShouldBe(1);
        var messages = await MessagesAsync();
        messages.Select(message => message.Role).ShouldBe(new[] { "user", "assistant" });
        messages[1].Content.ShouldEndWith(TurnInterruptionDefaults.InterruptedMarker);
        var usages = await UsagesAsync();
        usages.Count.ShouldBe(1);
        usages[0][1].ShouldBe(false);
        (await AnchorAsync()).ShouldNotBeNull();
        var trajectories = await WaitForRowsAsync(() => TrajectoriesAsync(turnId), 1);
        trajectories[0][0].ShouldBe(true);
    }

    [Test]
    public async Task AStopThatArrivesAfterTheTurnCompleted_LeavesEverythingAsItWas_AndKeepsTheDispatchedRow()
    {
        using var stop = new CancellationTokenSource();
        var turnId = Guid.NewGuid();
        var uiRow = await SeedDispatchedRowAsync(turnId);
        Provider.Enqueue(ScriptedStep.Text(GreetingAnswer));

        using var scope = Factory.Services.CreateScope();
        var chunks = await RunTurnAsync(scope, NewContext("hello", turnId, stop.Token));
        stop.Cancel();
        var summary = await scope.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>()
            .FinalizeAsync(UserId, turnId, endedInError: false);

        chunks.Last().Type.ShouldBe(SseChunkType.Done);
        chunks.ShouldContain(chunk => chunk.Type == SseChunkType.Metadata);
        chunks.ShouldNotContain(chunk => chunk.Type == SseChunkType.TurnStopped);
        summary.ShouldBeNull();
        var messages = await MessagesAsync();
        messages.Select(message => message.Content).ShouldBe(new[] { Prefix + "hello", GreetingAnswer });
        (await UsagesAsync()).Count.ShouldBe(1);
        (await ScalarAsync("SELECT ui_action_status FROM skill_usage_records WHERE id = @id", ("id", uiRow)))
            .ShouldBe((int)UiActionStatus.Dispatched);
    }

    private static async Task<Guid> SeedDispatchedRowAsync(Guid turnId)
    {
        await using var context = NewContext();
        var record = new SkillUsageRecord
        {
            Id = Guid.NewGuid(),
            SkillName = Prefix + "ui_action_of_the_turn",
            Category = SkillCategory.UI,
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Success = false,
            Timestamp = DateTime.UtcNow,
            TurnId = turnId,
            UiActionStatus = UiActionStatus.Dispatched
        };
        context.Set<SkillUsageRecord>().Add(record);
        await context.SaveChangesAsync();
        return record.Id;
    }
}
