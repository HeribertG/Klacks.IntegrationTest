// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public sealed record TurnIntentHeldOutItem(
    string Id,
    string Locale,
    string Set,
    string Category,
    string Text,
    string Label,
    string? PairKey);
