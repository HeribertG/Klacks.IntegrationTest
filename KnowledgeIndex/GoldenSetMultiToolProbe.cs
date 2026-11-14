// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Completeness report of the assembler fixture over the multi-tool golden set: assembles the toolset for every task
/// as Admin without route or history and reports COMP (every required group offered), the first-group hit
/// (displacement guard) and the per-group recall, split by kind (explicit/implied) and language. Each missed group is
/// listed with the source of the skills that were offered instead, so a change in retrieval or graph expansion shows
/// which step it recovered or lost. Single-target sets cannot show this: they ask for one skill per question.
/// </summary>
/// <param name="assembler">The production assembler from the host DI</param>
/// <param name="agent">The default agent whose skills form the catalogue</param>
/// <param name="rights">The caller's expanded rights (Admin)</param>
/// <param name="userId">The fixture's test user id</param>
/// <param name="toolBudget">maxToolsForProvider handed to AssembleAsync</param>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed class GoldenSetMultiToolProbe(
    ISkillToolsetAssembler assembler,
    Agent agent,
    List<string> rights,
    string userId,
    int toolBudget)
{
    private const string Label = "ASSEMBLER MULTI";

    public async Task<int> MeasureAsync(string path)
    {
        var items = MultiToolGoldenSetItem.Load(path);
        TestContext.WriteLine($"=== {Label} ({items.Count} tasks, budget {toolBudget}) ===");

        var complete = new Dictionary<string, int>();
        var total = new Dictionary<string, int>();
        var firstGroupHits = 0;
        var groupHits = 0;
        var groupTotal = 0;
        var expansionAdded = 0;
        foreach (var item in items)
        {
            var toolset = await assembler.AssembleAsync(
                agent, rights, item.Query, null, null, userId, item.LangCode,
                maxToolsForProvider: toolBudget, applyLearnedPhraseGuarantee: true);

            var offered = toolset.Functions.Select(f => f.Name).ToList();
            var hits = item.GroupHits(offered);
            var isComplete = hits.All(h => h);
            expansionAdded += toolset.Functions.Count(f => f.ToolsetSource == ToolsetSkillSource.Expansion);

            foreach (var key in new[] { "all", item.Kind, item.LangCode })
            {
                total[key] = total.GetValueOrDefault(key) + 1;
                complete[key] = complete.GetValueOrDefault(key) + (isComplete ? 1 : 0);
            }

            firstGroupHits += hits[0] ? 1 : 0;
            groupHits += hits.Count(h => h);
            groupTotal += hits.Length;

            if (!isComplete)
            {
                var missing = item.RequiredGroups.Where((_, i) => !hits[i]).Select(g => string.Join("|", g));
                TestContext.WriteLine(
                    $"MULTI MISS {item.Id} [{item.Kind}] | missing={string.Join(", ", missing)} | " +
                    $"toolset={toolset.Functions.Count} | query=\"{item.Query}\"");
            }
        }

        TestContext.WriteLine(
            $"{Label} summary | COMP {Ratio(complete, total, "all")} | explicit {Ratio(complete, total, "explicit")} | " +
            $"implied {Ratio(complete, total, "implied")} | first group {firstGroupHits}/{items.Count} | " +
            $"group recall {groupHits}/{groupTotal} | expansion skills offered {expansionAdded}");
        foreach (var language in total.Keys.Where(k => k.Length == 2).OrderBy(k => k, StringComparer.Ordinal))
        {
            TestContext.WriteLine($"{Label} {language} | COMP {Ratio(complete, total, language)}");
        }

        return items.Count;
    }

    private static string Ratio(Dictionary<string, int> complete, Dictionary<string, int> total, string key) =>
        $"{complete.GetValueOrDefault(key)}/{total.GetValueOrDefault(key)}";
}
