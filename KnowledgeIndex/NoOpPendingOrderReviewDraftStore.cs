// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Keeps the golden-set fixture off the order-review draft table: the persistent store deletes an expired row on
/// HasDraft, which the draft-loop policy reaches on every assembled toolset. The test user owns no rows, so replacing
/// the store is measurement-neutral.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant.OrderReview;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed class NoOpPendingOrderReviewDraftStore : IPendingOrderReviewDraftStore
{
    public void Set(Guid userId, string conversationId, OrderReviewDraft draft)
    {
    }

    public OrderReviewDraft? Get(Guid userId, string conversationId) => null;

    public bool HasDraft(Guid userId, string conversationId) => false;

    public void Clear(Guid userId, string conversationId)
    {
    }
}
