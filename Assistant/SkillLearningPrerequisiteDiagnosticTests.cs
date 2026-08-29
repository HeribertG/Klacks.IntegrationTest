// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Diagnoses the two stages the learning loop stands on, separately, because when the end-to-end
/// fixture produces nothing it cannot say which of them was silent. Both failures look identical from
/// the outside: a cluster comes back untouched.
///
/// Stage one is retrieval. If it contributes no candidates, the assembled toolset is only the always-on
/// set, the classifier is asked to choose a target out of a list that cannot contain one, and oracle O1
/// can never turn green no matter how good a phrase is.
///
/// Stage two is the model. The loop routes its own reasoning to the cheapest priced model in the
/// installation, whatever that happens to be, and asks it for strict JSON. This prints which model that
/// resolves to and what it actually answers, raw and untrimmed - the loop itself only ever reports
/// "no verdict", which is true but useless.
///
/// Reads only. Writes nothing, so nothing has to be cleaned up.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Explicit("Boots the real host, reads the dev DB on port 5434 and makes one real model call.")]
[Category("RealDatabase")]
[Category("SlowModelLoad")]
public class SkillLearningPrerequisiteDiagnosticTests
{
    private const string PlainWish = "welche zeitfenster fuer eine abwesenheit sind noch frei";
    private const string ObliqueWish = "sind die kartenpunkte der teams inzwischen gesetzt";

    private SignalRTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new SignalRTestWebApplicationFactory();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory?.Dispose();

    [Test]
    public async Task Retrieval_ReportsWhetherItContributesAnyCandidateAtAll()
    {
        using var scope = _factory.Services.CreateScope();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var assembler = scope.ServiceProvider.GetRequiredService<ISkillToolsetAssembler>();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var agent = await agents.GetDefaultAgentAsync();

        foreach (var wish in new[] { PlainWish, ObliqueWish })
        {
            var hits = await retrieval.RetrieveAsync(
                wish, [Roles.Admin], true, 20, null, CancellationToken.None, KnowledgeEntryKind.Skill);

            TestContext.WriteLine($"RETRIEVAL \"{wish}\" -> {hits.Candidates.Count} Treffer");
            foreach (var hit in hits.Candidates.Take(10))
            {
                TestContext.WriteLine($"   {hit.Entry.SourceId} score={hit.Score:F4}");
            }

            var toolset = await assembler.AssembleAsync(
                agent, [Roles.Admin], wish, null, null, Guid.Empty.ToString(), "de", 20, CancellationToken.None);

            TestContext.WriteLine(
                $"TOOLSET \"{wish}\" -> {toolset.Functions.Count}: "
                + string.Join(", ", toolset.Functions.Select(f => f.Name)));
        }
    }

    [Test]
    public async Task TheCheapestModel_ReportsWhatItActuallyAnswersToAStrictJsonRequest()
    {
        using var scope = _factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ICheapestModelResolver>();

        var (model, provider) = await resolver.ResolveAsync();

        TestContext.WriteLine(
            $"MODEL: {model?.ApiModelId ?? "<none>"} | provider={provider?.GetType().Name ?? "<none>"} "
            + $"| cost={(model == null ? 0 : model.CostPerInputToken + model.CostPerOutputToken)}");

        if (model == null || provider == null)
        {
            Assert.Inconclusive("No model or provider resolved; the loop can never classify anything here.");
            return;
        }

        await AskAndReportAsync(model, provider);
    }

    // The resolver takes the single cheapest priced model and nothing else, so one dead credential
    // silently disables every background path that shares it - the learning loop, conversation
    // compaction, read-only research. This walks the priced models in the order the resolver would
    // consider them and reports which ones actually answer, which is the difference between "this
    // installation cannot classify anything" and "this one credential is dead".
    [Test]
    public async Task ThePricedModels_ReportWhichOnesActuallyAnswer()
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILLMRepository>();
        var factory = scope.ServiceProvider.GetRequiredService<ILLMProviderFactory>();

        var priced = (await repository.GetModelsAsync(onlyEnabled: true))
            .Where(m => m.CostPerInputToken + m.CostPerOutputToken > 0)
            .OrderBy(m => m.CostPerInputToken + m.CostPerOutputToken)
            .Take(6)
            .ToList();

        foreach (var candidate in priced)
        {
            var candidateProvider = await factory.GetProviderForModelAsync(candidate.ModelId);
            if (candidateProvider == null)
            {
                TestContext.WriteLine($"MODEL {candidate.ApiModelId}: kein Provider aufgelöst");
                continue;
            }

            await AskAndReportAsync(candidate, candidateProvider);
        }
    }

    private static async Task AskAndReportAsync(LLMModel model, ILLMProvider provider)
    {
        var request = new LLMProviderRequest
        {
            Message =
                "Case 0\nWish: welche zeitfenster fuer eine abwesenheit sind noch frei\nLanguage: de\n"
                + "Offered skills: find_absence_capacity_windows, get_current_time\n\nClassify every case.",
            SystemPrompt =
                "You triage wishes that an assistant could not serve. Classify every case into exactly one "
                + "of: \"phrase_gap\", \"composable\", \"needs_code\". Respond ONLY with a JSON object: "
                + "{\"cases\":[{\"index\":0,\"kind\":\"phrase_gap\",\"skill\":\"...\",\"reason\":\"...\"}]}.",
            ModelId = model.ApiModelId,
            ConversationHistory = [],
            AvailableFunctions = [],
            Temperature = 0.2,
            MaxTokens = 700,
            SupportedParameters = model.SupportedParameters,
            CostPerInputToken = model.CostPerInputToken,
            CostPerOutputToken = model.CostPerOutputToken
        };

        var response = await provider.ProcessAsync(request, CancellationToken.None);

        TestContext.WriteLine(
            $"TRIED  : {model.ApiModelId} (cost={model.CostPerInputToken + model.CostPerOutputToken})");
        TestContext.WriteLine($"SUCCESS: {response.Success}");
        TestContext.WriteLine($"ERROR  : {response.Error ?? "-"}");
        TestContext.WriteLine($"CONTENT: {response.Content ?? "<null>"}");
    }
}
