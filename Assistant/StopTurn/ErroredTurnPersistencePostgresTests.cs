// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// F21 against real Postgres: a turn whose provider fails after the server ran a write action leaves its
/// history (under the neutral error marker), one usage row booked as an error, the correction anchor and an
/// interrupted trajectory row behind; a turn that fails after only reading, or before any call, leaves
/// nothing. Also the usage booking of the other endings: a stop books an ordinary row (a stop is no error), a
/// completed turn as well, and a skill row a stop cancelled carries failure kind 7 in its integer column.
/// The safety net is called the way the controller's finally block calls it, once the enumeration is over.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[TestFixture]
public class ErroredTurnPersistencePostgresTests : StopTurnPostgresTestBase
{
    private const string PartialAnswer = "Creating the employee now";

    [Test]
    public async Task AProviderFailureAfterAWrite_LeavesHistoryUsageAnchorAndTrajectoryBehind()
    {
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.FailsAfter(PartialAnswer));
        var turnId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var chunks = await RunTurnAsync(scope, NewContext("run the first test write action", turnId));
        var finalizer = await scope.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>()
            .FinalizeAsync(UserId, turnId, endedInError: false);

        chunks.ShouldContain(chunk => chunk.Type == SseChunkType.Error);
        chunks.ShouldNotContain(chunk => chunk.Type == SseChunkType.TurnStopped);
        finalizer.ShouldBeNull();

        var messages = await MessagesAsync();
        messages.Select(message => message.Role).ShouldBe(new[] { "user", "assistant" });
        messages[1].Content.ShouldBe(PartialAnswer + "\n" + TurnInterruptionDefaults.ErroredMarker);

        var usages = await UsagesAsync();
        usages.Count.ShouldBe(1);
        usages[0][0].ShouldBe(turnId);
        usages[0][1].ShouldBe(true);
        usages[0][2].ShouldBe(TurnInterruptionDefaults.ErroredUsageMessage);
        usages[0][3].ShouldBe(Json(new[] { WriteSkill }));

        var anchor = await AnchorAsync();
        anchor.ShouldNotBeNull();
        anchor[3]!.ToString()!.ShouldContain(WriteSkill);

        var trajectories = await WaitForRowsAsync(() => TrajectoriesAsync(turnId), 1);
        trajectories.Count.ShouldBe(1);
        trajectories[0][0].ShouldBe(true);
        trajectories[0][1].ShouldBe(InterruptedTurnPhases.DuringText);
    }

    [Test]
    public async Task AProviderFailureAfterOnlyAReadSkill_LeavesNothingBehind()
    {
        Provider.Enqueue(ScriptedStep.Tools(ReadSkill), ScriptedStep.FailsAfter(PartialAnswer));
        var turnId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        var chunks = await RunTurnAsync(scope, NewContext("show the test lookup", turnId));
        await scope.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>().FinalizeAsync(UserId, turnId, endedInError: false);

        chunks.ShouldContain(chunk => chunk.Type == SseChunkType.Error);
        await AssertNothingWasPersistedAsync(turnId);
    }

    [Test]
    public async Task AProviderFailureBeforeAnyCall_LeavesNothingBehind()
    {
        Provider.Enqueue(ScriptedStep.FailsAfter(PartialAnswer));
        var turnId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        await RunTurnAsync(scope, NewContext("hello", turnId));
        await scope.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>().FinalizeAsync(UserId, turnId, endedInError: false);

        await AssertNothingWasPersistedAsync(turnId);
    }

    [Test]
    public async Task TheErroredTurnReportedTwice_IsPersistedOnce()
    {
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.FailsAfter(PartialAnswer));
        var turnId = Guid.NewGuid();

        using var scope = Factory.Services.CreateScope();
        await RunTurnAsync(scope, NewContext("run the first test write action", turnId));
        var finalizer = scope.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>();
        await finalizer.FinalizeAsync(UserId, turnId, endedInError: false);
        await finalizer.FinalizeAsync(UserId, turnId, endedInError: true);

        (await MessagesAsync()).Count.ShouldBe(2);
        (await UsagesAsync()).Count.ShouldBe(1);
    }

    [Test]
    public async Task ACompletedTurnAndAStoppedTurn_BookAnOrdinaryUsageRow_AndOnlyTheErroredOneBooksAnError()
    {
        Provider.Enqueue(ScriptedStep.Text("All done."));
        var completedTurn = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            await RunTurnAsync(scope, NewContext("hello", completedTurn));
        }

        using var stop = new CancellationTokenSource();
        Provider.Enqueue(ScriptedStep.EndlessText("Thinking"));
        Provider.OnStreamStarted = streamToken =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                stop.Cancel();
            });
        };
        var stoppedTurn = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var chunks = await RunTurnAsync(scope, NewContext("hello again", stoppedTurn, stop.Token));
            chunks.ShouldContain(chunk => chunk.Type == SseChunkType.TurnStopped, string.Join(",", chunks.Select(c => c.Type + ":" + c.Text + c.ErrorMessage)) + " providerCalls=" + Provider.CallCount);
        }

        var usages = await UsagesAsync();
        usages.Count.ShouldBe(2);
        usages.Single(usage => (Guid)usage[0]! == completedTurn)[1].ShouldBe(false);
        usages.Single(usage => (Guid)usage[0]! == stoppedTurn)[1].ShouldBe(false);
        usages.Single(usage => (Guid)usage[0]! == stoppedTurn)[2].ShouldBeNull();
    }

    [Test]
    public async Task ASkillRowCancelledByAStop_CarriesFailureKindSevenInItsIntegerColumn()
    {
        using var scope = Factory.Services.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<ISkillUsageTracker>();
        var turnId = Guid.NewGuid();

        await tracker.TrackFailureAsync(
            Prefix + "cancelled_read",
            SkillFailureKind.Cancelled,
            new SkillExecutionContext
            {
                UserId = Guid.Parse(UserId),
                TenantId = Guid.NewGuid(),
                UserName = Prefix + "user",
                UserPermissions = Array.Empty<string>(),
                TurnId = turnId
            },
            parameters: null,
            errorMessage: null,
            TimeSpan.FromMilliseconds(5),
            SkillCategory.Read);

        (await ScalarAsync(
            "SELECT failure_kind FROM skill_usage_records WHERE turn_id = @turn", ("turn", turnId)))
            .ShouldBe((int)SkillFailureKind.Cancelled);
        (await ScalarAsync(
            "SELECT success FROM skill_usage_records WHERE turn_id = @turn", ("turn", turnId)))
            .ShouldBe(false);
    }

    private async Task AssertNothingWasPersistedAsync(Guid turnId)
    {
        (await MessagesAsync()).ShouldBeEmpty();
        (await UsagesAsync()).ShouldBeEmpty();
        (await AnchorAsync()).ShouldBeNull();
        await Task.Delay(1000);
        (await TrajectoriesAsync(turnId)).ShouldBeEmpty();
    }
}
