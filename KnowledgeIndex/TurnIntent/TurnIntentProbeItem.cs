// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public sealed record TurnIntentProbeItem(
    string Id,
    string TemplateId,
    string Locale,
    string Text,
    string Label,
    string Kind,
    string? PairId);
