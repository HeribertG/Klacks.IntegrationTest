// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Loads the held-out turn-intent corpus (natively written, never seen by the trainer) from the build output and
/// drops the items both labelling passes disagreed on.
/// </summary>

using System.Text.Json;

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentHeldOutCorpus
{
    private const string ItemsProperty = "items";
    private const string AmbiguousProperty = "ambiguous";
    private const string IdProperty = "id";
    private const string LocaleProperty = "locale";
    private const string SetProperty = "set";
    private const string CategoryProperty = "category";
    private const string TextProperty = "text";
    private const string LabelProperty = "label";
    private const string PairKeyProperty = "pairKey";

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(TurnIntentHeldOutSettings.CorpusEnvironmentVariable) is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(AppContext.BaseDirectory, TurnIntentHeldOutSettings.CorpusRelativePath);

    public static IReadOnlyList<TurnIntentHeldOutItem> LoadAgreed(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var items = new List<TurnIntentHeldOutItem>();

        foreach (var element in document.RootElement.GetProperty(ItemsProperty).EnumerateArray())
        {
            if (element.TryGetProperty(AmbiguousProperty, out var ambiguous) && ambiguous.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            items.Add(new TurnIntentHeldOutItem(
                element.GetProperty(IdProperty).GetString()!,
                element.GetProperty(LocaleProperty).GetString()!,
                element.GetProperty(SetProperty).GetString()!,
                element.GetProperty(CategoryProperty).GetString()!,
                element.GetProperty(TextProperty).GetString()!,
                element.GetProperty(LabelProperty).GetString()!,
                element.TryGetProperty(PairKeyProperty, out var pairKey) && pairKey.ValueKind == JsonValueKind.String
                    ? pairKey.GetString()
                    : null));
        }

        return items;
    }
}
