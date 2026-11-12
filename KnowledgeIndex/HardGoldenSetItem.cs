// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// One case of the hard golden set, shared by both runners so they score identically. Some queries
/// have more than one defensible target, so a case may list additional accepted targets via
/// "alsoAcceptedSourceIds". This is deliberately narrow: it exists for genuinely equivalent targets,
/// NOT to wave through misses — the recipe/skill duplicates that originally motivated this field were
/// resolved 2026-07-28 in favour of the skill.
/// The optional context fields are read only by the assembler fixture: "subset" labels the report group, "route"
/// is the UI route of the turn, "role" the caller's role (Admin, Authorised or Planner = no role) and "history" the
/// earlier user messages of the conversation, oldest first. Absent fields keep the old measurement (Admin, no
/// route, no history).
/// </summary>

using System.Text.Json;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed record HardGoldenSetItem(
    string Query,
    string ExpectedSourceId,
    IReadOnlyList<string> AlsoAcceptedSourceIds,
    string Lang,
    string LangCode)
{
    public string? Subset { get; init; }

    public string? Route { get; init; }

    public string? Role { get; init; }

    public IReadOnlyList<string> History { get; init; } = [];

    public bool Accepts(string sourceId) =>
        sourceId.Equals(ExpectedSourceId, StringComparison.OrdinalIgnoreCase)
        || AlsoAcceptedSourceIds.Any(a => sourceId.Equals(a, StringComparison.OrdinalIgnoreCase));

    public string ExpectedDisplay =>
        AlsoAcceptedSourceIds.Count == 0
            ? ExpectedSourceId
            : $"{ExpectedSourceId} (or {string.Join(" / ", AlsoAcceptedSourceIds)})";

    public static List<HardGoldenSetItem> Load(string path)
    {
        var raw = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(path))!;
        return raw.Select(e => new HardGoldenSetItem(
            e.GetProperty("query").GetString()!,
            e.GetProperty("expectedSourceId").GetString()!,
            e.TryGetProperty("alsoAcceptedSourceIds", out var also)
                ? also.EnumerateArray().Select(a => a.GetString()!).ToList()
                : [],
            e.TryGetProperty("lang", out var lang) ? lang.GetString()! : "unknown",
            // "lang" only distinguishes de from other. "langCode" carries the actual language, which
            // matters because the reranker's behaviour differs sharply between them.
            e.TryGetProperty("langCode", out var langCode) ? langCode.GetString()! : "unknown")
            {
                Subset = e.TryGetProperty("subset", out var subset) ? subset.GetString() : null,
                Route = e.TryGetProperty("route", out var route) ? route.GetString() : null,
                Role = e.TryGetProperty("role", out var role) ? role.GetString() : null,
                History = e.TryGetProperty("history", out var history)
                    ? history.EnumerateArray().Select(h => h.GetString()!).ToList()
                    : []
            })
            .ToList();
    }
}
