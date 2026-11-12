// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// In-memory history source for the assembler golden set: replaces the playground conversation store and reports
/// itself active, so LLMConversationManager reads the conversation history from here instead of the database. The
/// production RetrievalQueryBuilder then anchors the retrieval query exactly as in a live chat, while the fixture
/// writes nothing to the shared dev database. Only user messages are held, because only those enter the query.
/// </summary>
/// <param name="conversationId">Key of one golden case's conversation; must be unique per case, since the scoped
/// LLMConversationManager memoizes the history per conversation</param>
/// <param name="userMessages">Earlier user messages of the case, oldest first</param>

using System.Collections.Concurrent;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed class GoldenSetConversationHistoryStore : IPlaygroundConversationStore
{
    private const string UserRole = "user";

    private readonly ConcurrentDictionary<string, IReadOnlyList<LLMMessage>> _conversations = new(StringComparer.Ordinal);

    public bool IsActive => true;

    public void Set(string conversationId, IReadOnlyList<string> userMessages)
    {
        var start = DateTime.UtcNow.AddMinutes(-userMessages.Count);
        _conversations[conversationId] = userMessages
            .Select((content, index) => new LLMMessage
            {
                Role = UserRole,
                Content = content,
                CreateTime = start.AddMinutes(index)
            })
            .ToList();
    }

    public LLMConversation GetOrCreate(string conversationId, string userId) =>
        new() { ConversationId = conversationId, UserId = userId };

    public bool IsOngoing(string conversationId, string userId) =>
        _conversations.TryGetValue(conversationId, out var messages) && messages.Count > 0;

    public IReadOnlyList<LLMMessage> GetMessages(string conversationId, string userId) =>
        _conversations.TryGetValue(conversationId, out var messages) ? messages : [];

    public void AppendTurn(
        LLMConversation conversation, string userMessage, string assistantMessage, string modelId, DateTime turnStartedUtc,
        string title)
    {
    }
}
