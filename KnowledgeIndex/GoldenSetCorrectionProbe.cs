// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Correction report of the assembler fixture: re-assembles every item of the turn-eval goldset correction-v1.json
/// the way CorrectionTurnPreparer does after a decided correction - the composite of the previous message and the
/// correction (RecipeCorrectionComposer.Compose), the previous turn's skill excluded, no pins - and hands the result
/// to the score rule of CorrectionOutcomeComposer (CorrectionAmbiguityTally) and to BuildClarification itself, with
/// the item's recorded display label standing in for the authored previous-action label. The correction gates
/// G0-G5 of PlanCorrectionAsync are NOT run, so every item is treated as an engaged correction: the figures are an
/// upper bound of how often the two-option question could be asked, not how often production asks it.
/// </summary>
/// <param name="assembler">The production assembler from the host DI</param>
/// <param name="agent">The default agent whose skills form the catalogue</param>
/// <param name="rights">The caller's expanded rights (Admin)</param>
/// <param name="userId">The fixture's test user id</param>
/// <param name="toolBudget">maxToolsForProvider handed to AssembleAsync</param>

using System.Text.Json;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed class GoldenSetCorrectionProbe(
    ISkillToolsetAssembler assembler,
    Agent agent,
    List<string> rights,
    string userId,
    int toolBudget)
{
    private const string Label = "ASSEMBLER CORRECTION";
    private const string ItemsProperty = "items";
    private const string IdProperty = "id";
    private const string MessageProperty = "message";
    private const string LocaleProperty = "locale";
    private const string RouteProperty = "currentRoute";
    private const string ExpectedToolProperty = "expectedTool";
    private const string AlternativeToolsProperty = "alternativeTools";
    private const string PreviousTurnProperty = "previousTurn";
    private const string CalledSkillProperty = "calledSkill";
    private const string DisplayLabelProperty = "skillDisplayLabel";

    public async Task<CorrectionAmbiguityTally> MeasureAsync(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var items = document.RootElement.GetProperty(ItemsProperty).EnumerateArray()
            .Where(i => i.TryGetProperty(PreviousTurnProperty, out var previous) && previous.ValueKind == JsonValueKind.Object)
            .ToList();
        TestContext.WriteLine($"=== {Label} ({items.Count} items with a previous turn) ===");

        var tally = new CorrectionAmbiguityTally();
        var clarifications = 0;
        var hits = 0;
        var withExpectation = 0;
        foreach (var item in items)
        {
            var previous = item.GetProperty(PreviousTurnProperty);
            var calledSkill = ReadString(previous, CalledSkillProperty);
            var locale = ReadString(item, LocaleProperty);
            var composite = RecipeCorrectionComposer.Compose(
                ReadString(previous, MessageProperty), ReadString(item, MessageProperty) ?? string.Empty);
            IReadOnlyCollection<string> excluded = calledSkill is null
                || string.Equals(calledSkill, AutonomyDefaults.ConfirmPendingActionSkillName, StringComparison.OrdinalIgnoreCase)
                    ? []
                    : [calledSkill];

            var toolset = await assembler.AssembleAsync(
                agent, rights, composite, null, ReadString(item, RouteProperty), userId, locale,
                toolBudget, applyLearnedPhraseGuarantee: true, excluded, pinnedSkillNames: null,
                previousTurnSkillNames: null, continuationSkillNames: null, CancellationToken.None);

            var candidates = tally.Add(toolset.Functions);
            var clarification = CorrectionOutcomeComposer.BuildClarification(
                candidates, ReadString(previous, DisplayLabelProperty), locale);
            clarifications += clarification is null ? 0 : 1;

            var accepted = AcceptedTools(item);
            var hit = accepted.Count > 0 && toolset.Functions.Any(f => accepted.Contains(f.Name));
            withExpectation += accepted.Count > 0 ? 1 : 0;
            hits += hit ? 1 : 0;

            TestContext.WriteLine(
                $"CORRECTION | {ReadString(item, IdProperty)} | toolset={toolset.Functions.Count} | " +
                $"hit={(accepted.Count == 0 ? "-" : hit.ToString())} | clarification={clarification is not null} | " +
                $"deterministic=[{CorrectionAmbiguityTally.Describe(candidates)}]");
        }

        tally.Write(Label);
        TestContext.WriteLine(
            $"{Label} summary | clarification built={clarifications}/{items.Count} | " +
            $"expected tool offered={hits}/{withExpectation}");
        return tally;
    }

    private static HashSet<string> AcceptedTools(JsonElement item)
    {
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expected = ReadString(item, ExpectedToolProperty);
        if (expected is not null)
        {
            accepted.Add(expected);
            if (item.TryGetProperty(AlternativeToolsProperty, out var alternatives)
                && alternatives.ValueKind == JsonValueKind.Array)
            {
                accepted.UnionWith(alternatives.EnumerateArray().Select(a => a.GetString()!).Where(a => a is not null));
            }
        }

        return accepted;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
