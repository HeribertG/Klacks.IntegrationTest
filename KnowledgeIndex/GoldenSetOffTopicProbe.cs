// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Off-topic report of the assembler fixture: assembles the toolset for every question of
/// knowledge-index-offtopic.json (no Klacks meaning at all) as Admin without route or history and reports what the
/// model would be handed - retrieved candidates, non-always-on tools, Mutate tools among them and the
/// HasDomainSkillContext signal the world-model gate reads. A clean result is a toolset of the always-on skills only
/// with HasDomainSkillContext false; the score cutoff (KnowledgeIndexConstants.DefaultScoreCutoff) is what produces
/// it under the cross-encoder, while cosine scores never fall below it.
/// </summary>
/// <param name="assembler">The production assembler from the host DI</param>
/// <param name="agent">The default agent whose skills form the catalogue</param>
/// <param name="rights">The caller's expanded rights (Admin)</param>
/// <param name="userId">The fixture's test user id</param>
/// <param name="toolBudget">maxToolsForProvider handed to AssembleAsync</param>

using System.Globalization;
using System.Text.Json;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed class GoldenSetOffTopicProbe(
    ISkillToolsetAssembler assembler,
    Agent agent,
    List<string> rights,
    string userId,
    int toolBudget)
{
    private const string Label = "ASSEMBLER OFFTOPIC";
    private const string QueryLanguage = "de";

    public async Task<int> MeasureAsync(string path)
    {
        var queries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
        TestContext.WriteLine($"=== {Label} ({queries.Count} questions) ===");

        var withRetrieved = 0;
        var withMutate = 0;
        var withDomainContext = 0;
        var retrievedSum = 0;
        var nonAlwaysOnSum = 0;
        var mutateSum = 0;
        foreach (var query in queries)
        {
            var toolset = await assembler.AssembleAsync(
                agent, rights, query, null, null, userId, QueryLanguage,
                maxToolsForProvider: toolBudget, applyLearnedPhraseGuarantee: true);

            var nonAlwaysOn = toolset.Functions.Where(f => f.ToolsetSource != ToolsetSkillSource.AlwaysOn).ToList();
            var mutating = nonAlwaysOn.Where(f => f.Effect == SkillEffect.Mutate).ToList();
            var retrievedCount = toolset.Trace?.RetrievedNames.Count ?? 0;
            var topScore = toolset.Functions.Max(f => f.RetrievalScore);

            retrievedSum += retrievedCount;
            nonAlwaysOnSum += nonAlwaysOn.Count;
            mutateSum += mutating.Count;
            withRetrieved += retrievedCount > 0 ? 1 : 0;
            withMutate += mutating.Count > 0 ? 1 : 0;
            withDomainContext += toolset.HasDomainSkillContext ? 1 : 0;

            TestContext.WriteLine(
                $"OFFTOPIC | toolset={toolset.Functions.Count} | retrieved={retrievedCount} | " +
                $"nonAlwaysOn={nonAlwaysOn.Count} | mutate={mutating.Count} | " +
                $"effectNull={nonAlwaysOn.Count(f => f.Effect == null)} | domainContext={toolset.HasDomainSkillContext} | " +
                $"topScore={(topScore.HasValue ? topScore.Value.ToString("F4", CultureInfo.InvariantCulture) : "-")} | " +
                $"query=\"{query}\"");
            if (mutating.Count > 0)
            {
                TestContext.WriteLine($"  MUTATE OFFERED: {CorrectionAmbiguityTally.Describe(mutating)}");
            }
        }

        TestContext.WriteLine(
            $"{Label} summary | questions={queries.Count} | with retrieved candidates={withRetrieved} | " +
            $"with Mutate tool={withMutate} | HasDomainSkillContext true={withDomainContext} | " +
            $"retrieved sum={retrievedSum} | nonAlwaysOn sum={nonAlwaysOnSum} | mutate sum={mutateSum}");
        return queries.Count;
    }
}
