// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Follow-up 5 of the stop-turn stage 2 against real Postgres: persisting a turn writes only the columns the
/// turn owns, as increments in SQL, and never the columns other writers own. Each test lets a second writer
/// change the conversation row between the moment the turn loaded it and the moment it persists: a delete, a
/// compaction summary, a rename, another turn. The old code wrote the entity loaded at turn start back as a
/// whole (Update), so a late or parallel turn reverted the delete, dropped the summary, overwrote the count
/// and never stored the token totals; here every one of those is asserted. The two end-to-end tests run the
/// real chat service (scripted provider, a write skill that blocks) and delete the conversation while the
/// skill runs. Everything is keyed by the INTEGRATION_TEST_ prefix of the base class.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;
using ProviderUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[TestFixture]
public class ConversationRowPersistencePostgresTests : StopTurnPostgresTestBase
{
    private const string UserMessage = "one two three four five six seven";
    private const string ExpectedTitle = "one two three four five...";
    private const string AssistantMessage = "The answer.";
    private const string CompactedSummary = "summary written by a compaction";
    private const string RenamedTitle = "Renamed by the user";
    private const int MessagesPerTurn = 2;
    private const int TurnTokens = 120;
    private const decimal TurnCost = 0.25m;
    private const string BlockingRequest = "run the first test write action";
    private const string FinalAnswer = "The test action was executed.";
    private const string ConversationSql = "SELECT {0} FROM llm_conversations WHERE conversation_id = @conversation";

    [Test]
    public async Task ADeleteThatHappensAfterTheTurnLoadedTheConversation_IsNotRevertedByThePersistence()
    {
        await using var turnContext = NewContext();
        var (manager, conversation) = await LoadAsync(turnContext);
        await ExecuteAsync(
            "UPDATE llm_conversations SET is_deleted = true WHERE conversation_id = @conversation",
            ("conversation", ConversationKey));

        await manager.SaveConversationMessagesAsync(conversation, UserMessage, AssistantMessage, ModelKey);

        (await ColumnAsync("is_deleted")).ShouldBe(true);
    }

    [Test]
    public async Task ASummaryWrittenByACompactionAfterTheTurnLoadedTheConversation_SurvivesThePersistence()
    {
        await using var turnContext = NewContext();
        var (manager, conversation) = await LoadAsync(turnContext);
        await ExecuteAsync(
            "UPDATE llm_conversations SET summary = @summary WHERE conversation_id = @conversation",
            ("summary", CompactedSummary), ("conversation", ConversationKey));

        await manager.SaveConversationMessagesAsync(conversation, UserMessage, AssistantMessage, ModelKey);

        (await ColumnAsync("summary")).ShouldBe(CompactedSummary);
        (await ColumnAsync("message_count")).ShouldBe(MessagesPerTurn);
    }

    [Test]
    public async Task ACompactionThatLoadedTheConversationBeforeATurnPersisted_DoesNotRevertTheTurnsCount()
    {
        await using var compactionContext = NewContext();
        var (_, staleConversation) = await LoadAsync(compactionContext);

        await using var turnContext = NewContext();
        var (manager, conversation) = await LoadAsync(turnContext);
        await manager.SaveConversationMessagesAsync(conversation, UserMessage, AssistantMessage, ModelKey);

        staleConversation.Summary = CompactedSummary;
        await new LLMRepository(compactionContext, NullLogger<LLMModel>.Instance)
            .UpdateConversationSummaryAsync(staleConversation);

        (await ColumnAsync("summary")).ShouldBe(CompactedSummary);
        (await ColumnAsync("message_count")).ShouldBe(MessagesPerTurn);
        (await ColumnAsync("title")).ShouldBe(ExpectedTitle);
    }

    [Test]
    public async Task TwoTurnsThatLoadedTheSameConversationCountTheirMessagesTogether()
    {
        await using var firstContext = NewContext();
        await using var secondContext = NewContext();
        var (firstManager, first) = await LoadAsync(firstContext);
        var (secondManager, second) = await LoadAsync(secondContext);

        await Task.WhenAll(
            firstManager.SaveConversationMessagesAsync(first, UserMessage, AssistantMessage, ModelKey),
            secondManager.SaveConversationMessagesAsync(second, UserMessage, AssistantMessage, ModelKey));

        (await ColumnAsync("message_count")).ShouldBe(2 * MessagesPerTurn);
    }

    [Test]
    public async Task TheTitleIsSetOnlyWhileTheConversationHasNone()
    {
        await using var turnContext = NewContext();
        var (manager, conversation) = await LoadAsync(turnContext);
        await manager.SaveConversationMessagesAsync(conversation, UserMessage, AssistantMessage, ModelKey);
        (await ColumnAsync("title")).ShouldBe(ExpectedTitle);

        await ExecuteAsync(
            "UPDATE llm_conversations SET title = @title WHERE conversation_id = @conversation",
            ("title", RenamedTitle), ("conversation", ConversationKey));
        await manager.SaveConversationMessagesAsync(conversation, "another message text", AssistantMessage, ModelKey);

        (await ColumnAsync("title")).ShouldBe(RenamedTitle);
    }

    [Test]
    public async Task ATitleGivenWhileTheTurnRanIsNotOverwrittenByAFirstTurnThatLoadedNone()
    {
        await using var turnContext = NewContext();
        var (manager, conversation) = await LoadAsync(turnContext);
        await ExecuteAsync(
            "UPDATE llm_conversations SET title = @title WHERE conversation_id = @conversation",
            ("title", RenamedTitle), ("conversation", ConversationKey));

        await manager.SaveConversationMessagesAsync(conversation, UserMessage, AssistantMessage, ModelKey);

        (await ColumnAsync("title")).ShouldBe(RenamedTitle);
    }

    [Test]
    public async Task ThePersistenceNeverMovesTheLastMessageTimeBackwards()
    {
        await using var turnContext = NewContext();
        var (manager, conversation) = await LoadAsync(turnContext);
        var future = DateTime.UtcNow.AddHours(1);
        await ExecuteAsync(
            "UPDATE llm_conversations SET last_message_at = @at, last_model_id = @model WHERE conversation_id = @conversation",
            ("at", future), ("model", "newer-turn-model"), ("conversation", ConversationKey));

        await manager.SaveConversationMessagesAsync(conversation, UserMessage, AssistantMessage, ModelKey);

        var stored = (DateTime)(await ColumnAsync("last_message_at"))!;
        stored.ShouldBe(future, TimeSpan.FromMilliseconds(1));
        (await ColumnAsync("last_model_id")).ShouldBe("newer-turn-model");
    }

    [Test]
    public async Task TheTokensAndTheCostOfTwoTurnsAddUp()
    {
        var model = await LoadModelAsync();
        await using var firstContext = NewContext();
        await using var secondContext = NewContext();
        var (firstManager, first) = await LoadAsync(firstContext);
        var (secondManager, second) = await LoadAsync(secondContext);
        var usage = new ProviderUsage { InputTokens = TurnTokens / 2, OutputTokens = TurnTokens / 2, Cost = TurnCost };

        await Task.WhenAll(
            firstManager.TrackUsageAsync(UserId, model, first, usage, 1, turnId: Guid.NewGuid()),
            secondManager.TrackUsageAsync(UserId, model, second, usage, 1, turnId: Guid.NewGuid()));

        (await ColumnAsync("total_tokens")).ShouldBe(2 * TurnTokens);
        (await ColumnAsync("total_cost")).ShouldBe(2 * TurnCost);
    }

    [Test]
    public async Task AFailedTurnAddsNoTokensAndNoCost()
    {
        var model = await LoadModelAsync();
        await using var turnContext = NewContext();
        var (manager, conversation) = await LoadAsync(turnContext);
        var usage = new ProviderUsage { InputTokens = TurnTokens, Cost = TurnCost };

        await manager.TrackUsageAsync(UserId, model, conversation, usage, 1, hasError: true, turnId: Guid.NewGuid());

        (await ColumnAsync("total_tokens")).ShouldBe(0);
        (await ColumnAsync("total_cost")).ShouldBe(0m);
    }

    [Test]
    public async Task TheTrackedEntityShowsTheDatabaseStateAfterThePersistence()
    {
        var model = await LoadModelAsync();
        await using var turnContext = NewContext();
        var (manager, conversation) = await LoadAsync(turnContext);
        var usage = new ProviderUsage { InputTokens = TurnTokens, Cost = TurnCost };

        await manager.SaveConversationMessagesAsync(conversation, UserMessage, AssistantMessage, ModelKey);
        await manager.TrackUsageAsync(UserId, model, conversation, usage, 1, turnId: Guid.NewGuid());

        conversation.MessageCount.ShouldBe(MessagesPerTurn);
        conversation.Title.ShouldBe(ExpectedTitle);
        conversation.LastModelId.ShouldBe(ModelKey);
        conversation.TotalTokens.ShouldBe(TurnTokens);
        conversation.TotalCost.ShouldBe(TurnCost);
        turnContext.Entry(conversation).State.ShouldBe(EntityState.Unchanged);
    }

    [Test]
    public async Task AStoppedTurnPersistedLate_DoesNotRevertTheDeleteOfItsConversation()
    {
        var (run, release, stop) = await StartBlockedTurnAsync();
        await DeleteConversationAsync();

        stop.Cancel();
        release.SetResult();
        await run.WaitAsync(Patience);

        (await ColumnAsync("is_deleted")).ShouldBe(true);
        stop.Dispose();
    }

    [Test]
    public async Task ACompletedTurn_DoesNotRevertTheDeleteThatHappenedWhileItsSkillRan()
    {
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.Text(FinalAnswer));
        var (run, release, stop) = await StartBlockedTurnAsync(enqueueTools: false);
        await DeleteConversationAsync();

        release.SetResult();
        await run.WaitAsync(Patience);

        (await ColumnAsync("is_deleted")).ShouldBe(true);
        stop.Dispose();
    }

    private async Task<(Task Run, TaskCompletionSource Release, CancellationTokenSource Stop)> StartBlockedTurnAsync(
        bool enqueueTools = true)
    {
        var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Skills.On(WriteSkill, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return ScriptedSkillBridge.Succeeded();
        });
        if (enqueueTools)
        {
            Provider.Enqueue(ScriptedStep.Tools(WriteSkill));
        }

        var scope = Factory.Services.CreateScope();
        var run = Task.Run(async () =>
        {
            using (scope)
            {
                await RunTurnAsync(scope, NewContext(BlockingRequest, Guid.NewGuid(), stop.Token));
            }
        });
        await entered.Task.WaitAsync(Patience);
        return (run, release, stop);
    }

    private async Task DeleteConversationAsync() => (await ExecuteAsync(
        "UPDATE llm_conversations SET is_deleted = true WHERE conversation_id = @conversation",
        ("conversation", ConversationKey))).ShouldBe(1);

    private async Task<(LLMConversationManager Manager, LLMConversation Conversation)> LoadAsync(
        Klacks.Api.Infrastructure.Persistence.DataBaseContext context)
    {
        var repository = new LLMRepository(context, NullLogger<LLMModel>.Instance);
        var manager = new LLMConversationManager(NullLogger<LLMConversationManager>.Instance, repository);
        var conversation = await manager.GetOrCreateConversationAsync(ConversationKey, UserId);
        return (manager, conversation);
    }

    private async Task<LLMModel> LoadModelAsync()
    {
        await using var context = NewContext();
        return await context.Set<LLMModel>().AsNoTracking().SingleAsync(model => model.ModelId == ModelKey);
    }

    private async Task<object?> ColumnAsync(string column) =>
        await ScalarAsync(string.Format(ConversationSql, column), ("conversation", ConversationKey));
}
