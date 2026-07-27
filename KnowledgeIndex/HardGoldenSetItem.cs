// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// One case of the hard golden set, shared by both runners so they score identically.
/// Some queries have more than one defensible target: the catalog contains recipe/skill pairs for the
/// same action (bulk-add-absence-for-group vs. bulk_add_absence_for_group), where the recipe is the
/// guided flow and the skill does the same job in one call with a preview mode. Forcing a single
/// answer there measures an arbitrary preference rather than retrieval quality, so a case may list
/// additional accepted targets via "alsoAcceptedSourceIds".
/// This is deliberately narrow: it exists for genuinely equivalent targets, NOT to wave through
/// misses. Whether the catalog should carry both variants at all is a product question, not a
/// retrieval one.
/// </summary>

using System.Text.Json;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed record HardGoldenSetItem(
    string Query,
    string ExpectedSourceId,
    IReadOnlyList<string> AlsoAcceptedSourceIds)
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
                : []))
            .ToList();
    }
}
