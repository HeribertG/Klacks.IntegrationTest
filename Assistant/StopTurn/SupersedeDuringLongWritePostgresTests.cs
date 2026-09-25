// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// I-1 against real Postgres: the user stops a turn that is stuck in a long write skill, sends a new message
/// and the new turn runs to its end; only then does the write skill of the old turn finish, and the old,
/// stopped turn persists itself late. The history must still read old request, old answer, new request, new
/// answer (the late rows carry the time the old turn began), and the correction anchor must still be the new
/// turn's: a "no, I meant..." after the new turn must find the new turn's action, not the old one's. The
/// message count of the conversation is read and reported, not asserted: it is fed from the conversation row
/// each turn loaded when it began, which is outside the fix (a known limit, present before this stage).
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[TestFixture]
public class SupersedeDuringLongWritePostgresTests : StopTurnPostgresTestBase
{
    private const string FirstRequest = "run the first test write action";
    private const string SecondRequest = "run the second test write action";
    private const string SecondAnswer = "The second test action was executed.";
    private const int SeparationMs = 100;

    [Test]
    public async Task AStoppedTurnPersistedAfterTheNewerTurn_KeepsTheHistoryOrderAndTheNewerAnchor()
    {
        using var stopFirst = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Skills.On(WriteSkill, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return ScriptedSkillBridge.Succeeded();
        });
        Provider.Enqueue(
            ScriptedStep.Tools(WriteSkill),
            ScriptedStep.Tools(SecondWriteSkill),
            ScriptedStep.Text(SecondAnswer));
        var firstTurn = Guid.NewGuid();
        var secondTurn = Guid.NewGuid();

        using var firstScope = Factory.Services.CreateScope();
        var first = Task.Run(() => RunTurnAsync(firstScope, NewContext(FirstRequest, firstTurn, stopFirst.Token)));
        await entered.Task.WaitAsync(Patience);
        await Task.Delay(SeparationMs);

        stopFirst.Cancel();
        using (var secondScope = Factory.Services.CreateScope())
        {
            var second = await RunTurnAsync(secondScope, NewContext(SecondRequest, secondTurn));
            second.ShouldContain(chunk => chunk.Type == SseChunkType.Metadata);
        }

        (await MessagesAsync()).Count.ShouldBe(2, "only the newer turn has persisted so far");
        var anchorBefore = await AnchorAsync();
        anchorBefore.ShouldNotBeNull($"usages={(await UsagesAsync()).Count} providerCalls={Provider.CallCount} skillCalls={string.Join(',', Skills.Calls.Select(c => c.Skill))}");

        release.SetResult();
        var firstChunks = await first.WaitAsync(Patience);

        firstChunks.Last().Type.ShouldBe(SseChunkType.Done);
        firstChunks[^2].Type.ShouldBe(SseChunkType.TurnStopped);

        var messages = await MessagesAsync();
        messages.Select(message => message.Role).ShouldBe(new[] { "user", "assistant", "user", "assistant" });
        messages[0].Content.ShouldBe(Prefix + FirstRequest);
        messages[1].Content.ShouldBe(TurnInterruptionDefaults.InterruptedMarker);
        messages[2].Content.ShouldBe(Prefix + SecondRequest);
        messages[3].Content.ShouldBe(SecondAnswer);
        messages.Select(message => message.CreateTime).ShouldBe(messages.Select(message => message.CreateTime).Distinct().Order(), "no two rows share a time: the history is read ordered by it alone");

        var anchor = await AnchorAsync();
        anchor.ShouldNotBeNull();
        anchor[0].ShouldBe(Prefix + SecondRequest);
        anchor[3]!.ToString()!.ShouldContain(SecondWriteSkill);
        anchor[3]!.ToString()!.ShouldNotContain(WriteSkill);
        anchor[4].ShouldBe(anchorBefore[4]);

        var usages = await UsagesAsync();
        usages.Select(usage => (Guid)usage[0]!).Order().ShouldBe(new[] { firstTurn, secondTurn }.Order());

        var trajectories = await WaitForRowsAsync(() => TrajectoriesAsync(firstTurn), 1);
        trajectories.Count.ShouldBe(1);
        trajectories[0][0].ShouldBe(true);

        var messageCount = await ScalarAsync(
            "SELECT message_count FROM llm_conversations WHERE conversation_id = @conversation", ("conversation", ConversationKey));
        TestContext.Out.WriteLine(
            $"Observed conversation.message_count after both turns = {messageCount} (four rows exist in llm_messages)");
    }

    [Test]
    public async Task TheSameLateStopWithoutANewerTurn_WritesItsOwnAnchor()
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
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill));

        using var scope = Factory.Services.CreateScope();
        var run = Task.Run(() => RunTurnAsync(scope, NewContext(FirstRequest, Guid.NewGuid(), stop.Token)));
        await entered.Task.WaitAsync(Patience);
        stop.Cancel();
        release.SetResult();
        await run.WaitAsync(Patience);

        var anchor = await AnchorAsync();
        anchor.ShouldNotBeNull();
        anchor[0].ShouldBe(Prefix + FirstRequest);
        anchor[3]!.ToString()!.ShouldContain(WriteSkill);
    }
}
