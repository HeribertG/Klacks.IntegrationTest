// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// One case of the hard golden set, shared by both runners so they score identically. Some queries
/// have more than one defensible target, so a case may list additional accepted targets via
/// "alsoAcceptedSourceIds". This is deliberately narrow: it exists for genuinely equivalent targets,
/// NOT to wave through misses — the recipe/skill duplicates that originally motivated this field were
/// resolved 2026-07-28 in favour of the skill.
/// </summary>

using System.Text.Json;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed record HardGoldenSetItem(
    string Query,
    string ExpectedSourceId,
    IReadOnlyList<string> AlsoAcceptedSourceIds,
    string Lang)
{
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
            e.TryGetProperty("lang", out var lang) ? lang.GetString()! : "unknown"))
            .ToList();
    }
}
