// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Keeps the golden-set fixture off the pending-recipe table: the persistent store deletes an expired row on Peek,
/// which the recipe guarantee reaches as soon as a case carries a conversation id. The test user owns no rows, so
/// replacing the store is measurement-neutral.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed class NoOpPendingRecipeStore : IPendingRecipeStore
{
    public void Save(PendingRecipe pending)
    {
    }

    public PendingRecipe? Peek(Guid userId, string conversationId) => null;

    public void Clear(Guid userId, string conversationId)
    {
    }
}
