// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// One case of the multi-tool golden set (knowledge-index-golden-multi.json): a lay-language task that needs several
/// skills in the same turn. Each required group is satisfied by any one of its skills; the case is complete only when
/// every group is satisfied. "kind" is "explicit" (the user names every step) or "implied" (a planner would add the
/// later step unasked, e.g. finding cover when someone calls in sick). The first group is the step the user asked
/// for first and serves as the displacement guard.
/// </summary>

using System.Text.Json;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed record MultiToolGoldenSetItem(
    string Id,
    string Query,
    string Kind,
    string LangCode,
    IReadOnlyList<IReadOnlyList<string>> RequiredGroups)
{
    public bool[] GroupHits(IReadOnlyCollection<string> offeredNames) =>
        RequiredGroups
            .Select(group => group.Any(skill => offeredNames.Contains(skill, StringComparer.OrdinalIgnoreCase)))
            .ToArray();

    public static List<MultiToolGoldenSetItem> Load(string path)
    {
        var raw = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(path))!;
        return raw.Select(e => new MultiToolGoldenSetItem(
                e.GetProperty("id").GetString()!,
                e.GetProperty("query").GetString()!,
                e.GetProperty("kind").GetString()!,
                e.GetProperty("langCode").GetString()!,
                e.GetProperty("requiredGroups").EnumerateArray()
                    .Select(g => (IReadOnlyList<string>)g.EnumerateArray().Select(s => s.GetString()!).ToList())
                    .ToList()))
            .ToList();
    }
}
