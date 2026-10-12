// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Loads the non-ambiguous items of turn-intent-v1.json from the build output (copied there by Klacks.Api).
/// </summary>

using System.Text.Json;

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentProbeGoldset
{
    private const string ItemsProperty = "items";
    private const string AmbiguousProperty = "ambiguous";
    private const string PairIdProperty = "pairId";
    private const string IdProperty = "id";
    private const string TemplateIdProperty = "templateId";
    private const string LocaleProperty = "locale";
    private const string TextProperty = "text";
    private const string LabelProperty = "label";
    private const string KindProperty = "kind";

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, TurnIntentProbeSettings.GoldsetRelativePath);

    public static IReadOnlyList<TurnIntentProbeItem> LoadNonAmbiguous(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var items = new List<TurnIntentProbeItem>();

        foreach (var element in document.RootElement.GetProperty(ItemsProperty).EnumerateArray())
        {
            if (element.TryGetProperty(AmbiguousProperty, out var ambiguous) && ambiguous.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            items.Add(new TurnIntentProbeItem(
                element.GetProperty(IdProperty).GetString()!,
                element.GetProperty(TemplateIdProperty).GetString()!,
                element.GetProperty(LocaleProperty).GetString()!,
                element.GetProperty(TextProperty).GetString()!,
                element.GetProperty(LabelProperty).GetString()!,
                element.GetProperty(KindProperty).GetString()!,
                element.TryGetProperty(PairIdProperty, out var pairId) ? pairId.GetString() : null));
        }

        return items;
    }
}
