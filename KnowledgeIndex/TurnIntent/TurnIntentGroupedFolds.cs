// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Deterministic grouped k-fold assignment. Items that share a templateId (translations, paraphrases) or a pairId
/// (the two members of a minimal pair, in any locale) are merged into one connected component and never separated, so
/// neither a translation nor the other half of a minimal pair can leak from the training into the test fold.
/// Components are shuffled with a fixed seed, then dealt largest-first onto the currently smallest fold.
/// </summary>
/// <param name="items">The labelled items</param>
/// <param name="foldCount">Number of folds</param>
/// <param name="seed">Seed of the component shuffle</param>

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentGroupedFolds
{
    private const string TemplatePrefix = "t:";
    private const string PairPrefix = "p:";

    public static int[] Assign(IReadOnlyList<TurnIntentProbeItem> items, int foldCount, int seed)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);

        string Find(string key)
        {
            if (!parent.TryGetValue(key, out var up))
            {
                parent[key] = key;
                return key;
            }

            if (up == key)
            {
                return key;
            }

            var root = Find(up);
            parent[key] = root;
            return root;
        }

        void Union(string a, string b) => parent[Find(a)] = Find(b);

        foreach (var item in items)
        {
            var template = TemplatePrefix + item.TemplateId;
            Find(template);
            if (item.PairId != null)
            {
                Union(template, PairPrefix + item.PairId);
            }
        }

        var components = items
            .Select((item, index) => (Root: Find(TemplatePrefix + item.TemplateId), Index: index))
            .GroupBy(entry => entry.Root, StringComparer.Ordinal)
            .Select(group => group.Select(entry => entry.Index).ToList())
            .OrderBy(indices => indices[0])
            .ToList();

        var random = new Random(seed);
        var shuffled = components.OrderBy(_ => random.Next()).OrderByDescending(indices => indices.Count).ToList();

        var folds = new int[items.Count];
        var sizes = new int[foldCount];
        foreach (var component in shuffled)
        {
            var target = Array.IndexOf(sizes, sizes.Min());
            foreach (var index in component)
            {
                folds[index] = target;
            }

            sizes[target] += component.Count;
        }

        return folds;
    }
}
