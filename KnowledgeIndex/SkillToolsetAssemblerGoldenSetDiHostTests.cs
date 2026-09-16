// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Recall report for the REAL production toolset: the hard golden sets scored through
/// <see cref="ISkillToolsetAssembler.AssembleAsync"/>, i.e. retrieval plus every deterministic guarantee
/// layer (keyword, learned phrase, recipe forcing, concept/page hints, co-required expansion) and the
/// provider-cap truncation. <see cref="KnowledgeIndexHardGoldenSetDiHostTests"/> measures the retrieval
/// arm alone; this fixture measures what the model is actually handed, and reports the difference per
/// case: "lift" (assembler hit, retrieval-only miss) is what the guarantee layers buy, "loss"
/// (retrieval-only hit, assembler miss) is what truncation to the provider cap costs. The loss cases are
/// listed individually because they are the actionable output.
/// Same DI-host boot and workarounds as the hard fixture (UiControl/RegionSetup startup bug, neutralized
/// IKnowledgeIndexSynchronizer). The "Active embedding space" line must match the space the stored rows
/// were written in, or every figure compares vectors across incompatible spaces: since 2026-08-20 the
/// production embedder is the fp16 export (KnowledgeIndexConstants.EmbeddingModelName), so the expected
/// value is "onnx:multilingual-e5-base-fp16@768" (verified 2026-09-10 against the dev DB's 499 rows).
/// Additionally neutralizes the two pending stores the assembler consults: PersistentPendingConfirmationStore
/// hard-deletes proposal-hint rows when a message leads with a negation (DiscardProposalHints), and
/// PersistentPendingPlanningProfileDraftStore.Get deletes an expired draft row. Both are keyed on the
/// user id and the test user owns no rows, so the replacement is measurement-neutral - but the dev DB on
/// 5434 is shared with the running dev app and this fixture must not own a write path at all.
/// Reporting only - always green as long as the sets load; no MinPassRate gate.
/// The co-required expansion skips with a small random probability (SkillRetrievalExpander), so the
/// assembler figure can move by a case or two between runs without any code change; expansion only ever
/// adds skills, so it can turn a miss into a hit but never the reverse.
/// </summary>
/// <param name="KLACKS_GOLDENSET_MAX_CASES">
/// Optional environment variable: caps every set to its first N cases so a smoke run finishes in
/// minutes. Unset or non-positive means no cap (the nightly run). Capped figures are NOT comparable to
/// full-set figures - the sets are not shuffled, so the first N cases carry a skewed language mix.
/// </param>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Explicit("Boots the real app host and reads the real DB on port 5434. Reporting only, run manually.")]
[Category("RealDatabase")]
public class SkillToolsetAssemblerGoldenSetDiHostTests
{
    public const string MaxCasesEnvVar = "KLACKS_GOLDENSET_MAX_CASES";

    private const string CoreLabel = "ASSEMBLER CORE";
    private const string ExtendedLabel = "ASSEMBLER EXTENDED";
    private const string CombinedLabel = "ASSEMBLER COMBINED";
    private const string LanguageCoverageLabel = "ASSEMBLER LANGS";

    private const string AssemblerArmTag = "[assembler]";
    private const string RetrievalArmTag = "[retrieval-only]";

    // A fixed, syntactically valid GUID that no real user carries. It must parse: the pending-notes,
    // proposal-hint and planning-draft guarantees all skip silently on an unparsable id, which would
    // hide those layers from the measurement instead of exercising them against an empty state.
    private const string TestUserId = "00000000-0000-4000-8000-00000000600d";

    private static readonly string GoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard.json");

    private static readonly string ExtendedGoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard-ext.json");

    private static readonly string LanguageCoverageSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard-langs.json");

    private sealed class NoOpPendingConfirmationStore : IPendingConfirmationStore
    {
        public string Create(
            Guid userId,
            string skillName,
            IReadOnlyDictionary<string, object> parameters,
            string purpose = PendingConfirmationPurposes.GateReplay) =>
            string.Empty;

        public PendingConfirmation? Consume(string token, Guid userId, string? expectedSkillName = null) => null;

        public PendingConfirmationHandle? PeekLatestForUser(
            Guid userId, TimeSpan maxAge, string purpose = PendingConfirmationPurposes.GateReplay) => null;

        public void CreateProposalHint(Guid userId, string applySkillName)
        {
        }

        public void DiscardProposalHints(Guid userId, string? applySkillName = null)
        {
        }

        public void DiscardCorrectionUndo(Guid userId)
        {
        }
    }

    private sealed class NoOpPendingPlanningProfileDraftStore : IPendingPlanningProfileDraftStore
    {
        public void Set(Guid userId, string conversationId, PlanningProfileDraft draft)
        {
        }

        public PlanningProfileDraft? Get(Guid userId, string conversationId) => null;

        public void Clear(Guid userId, string conversationId)
        {
        }
    }

    private sealed class AssemblerBypassFactory : SignalRTestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUiControlRepository>();
                services.AddScoped<IUiControlRepository, NoOpUiControlRepository>();
                services.RemoveAll<IRegionSetupService>();
                services.AddScoped<IRegionSetupService, NoOpRegionSetupService>();
                services.RemoveAll<IKnowledgeIndexSynchronizer>();
                services.AddScoped<IKnowledgeIndexSynchronizer, NoOpKnowledgeIndexSynchronizer>();

                // Same lifetime as production (singletons) so the assembler's scoped resolution is unchanged.
                services.RemoveAll<IPendingConfirmationStore>();
                services.AddSingleton<IPendingConfirmationStore, NoOpPendingConfirmationStore>();
                services.RemoveAll<IPendingPlanningProfileDraftStore>();
                services.AddSingleton<IPendingPlanningProfileDraftStore, NoOpPendingPlanningProfileDraftStore>();
            });
        }
    }

    private AssemblerBypassFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new AssemblerBypassFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [Test]
    public async Task AssemblerGoldenSet_RecallReport_ViaRealHostDi()
    {
        using var scope = _factory.Services.CreateScope();
        var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var assembler = scope.ServiceProvider.GetRequiredService<ISkillToolsetAssembler>();
        var agentRepository = scope.ServiceProvider.GetRequiredService<IAgentRepository>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");

        // Same resolution as the chat paths (SkillCacheService.GetDefaultAgentAsync reads this row):
        // AssembleAsync returns an empty toolset for a null agent, so the agent has to be real.
        var agent = await agentRepository.GetDefaultAgentAsync(CancellationToken.None);
        agent.ShouldNotBeNull("No default agent in the database - the assembler cannot build a toolset.");

        // Mirrors ChatController.GetCurrentUserRights(): the role name itself (the assembler keys isAdmin
        // and the permission bypass on it) plus the granular rights it expands to.
        var userRights = Permissions.ExpandRoles([Roles.Admin]);

        var alwaysOnNames = await LoadAlwaysOnSkillNamesAsync(scope);
        TestContext.WriteLine(
            $"Always-on skills in the catalogue ({alwaysOnNames.Count}): " +
            $"{string.Join(", ", alwaysOnNames.OrderBy(n => n))}");
        TestContext.WriteLine(
            $"Provider cap (maxToolsForProvider): {KnowledgeIndexConstants.MaxToolsForProvider}, " +
            $"retrieval width (DefaultTopK): {KnowledgeIndexConstants.DefaultTopK}");

        var caseCap = ResolveCaseCap();
        if (caseCap is not null)
        {
            TestContext.WriteLine(
                $"Case cap active: first {caseCap} cases per set ({MaxCasesEnvVar}) - figures below are a " +
                "smoke run, not comparable to the full sets.");
        }

        var core = ApplyCap(HardGoldenSetItem.Load(GoldenSetPath), caseCap);
        var extended = ApplyCap(HardGoldenSetItem.Load(ExtendedGoldenSetPath), caseCap);
        var languages = ApplyCap(HardGoldenSetItem.Load(LanguageCoverageSetPath), caseCap);

        var probe = new AssemblerProbe(assembler, retrieval, agent, userRights, alwaysOnNames);

        // Reported per set, never merged: every hard-fixture figure refers to CORE and EXTENDED
        // separately, and the language set has its own denominator.
        var coreRecall = await probe.MeasureSetAsync(CoreLabel, core);
        var extRecall = await probe.MeasureSetAsync(ExtendedLabel, extended);
        WriteCombined(coreRecall, extRecall);

        var langRecall = await probe.MeasureSetAsync(LanguageCoverageLabel, languages);

        // Retrieval failing inside AssembleAsync is caught there and degrades to always-on plus
        // guarantees. That would still produce plausible-looking figures, so it is checked explicitly.
        var retrievedTotal = coreRecall.RetrievedSourceFunctions + extRecall.RetrievedSourceFunctions +
                             langRecall.RetrievedSourceFunctions;
        TestContext.WriteLine(
            $"Retrieved-source functions across all assembled toolsets: {retrievedTotal}");
        if (retrievedTotal == 0)
        {
            TestContext.WriteLine(
                "WARNING: no assembled toolset contained a Retrieved-source function - retrieval inside " +
                "AssembleAsync most likely failed and was swallowed; the assembler figures above measure " +
                "the guarantee layers alone.");
        }

        core.Count.ShouldBeGreaterThan(0);
        extended.Count.ShouldBeGreaterThan(0);
        languages.Count.ShouldBeGreaterThan(0);
    }

    private static void WriteCombined(AssemblerSetRecall coreRecall, AssemblerSetRecall extRecall)
    {
        var cases = coreRecall.Cases + extRecall.Cases;
        var assemblerHits = coreRecall.AssemblerHits + extRecall.AssemblerHits;
        var retrievalHits = coreRecall.RetrievalHits + extRecall.RetrievalHits;
        TestContext.WriteLine(
            $"=== {CombinedLabel} ({cases} cases) === Assembler toolset recall: " +
            $"{assemblerHits}/{cases} = {(double)assemblerHits / cases:P1}");
        TestContext.WriteLine(
            $"=== {CombinedLabel} ({cases} cases) === Retrieval-only recall@" +
            $"{KnowledgeIndexConstants.DefaultTopK}: {retrievalHits}/{cases} = {(double)retrievalHits / cases:P1}");
        TestContext.WriteLine(
            $"=== {CombinedLabel} ({cases} cases) === Guarantee lift: {coreRecall.Lift + extRecall.Lift}, " +
            $"Guarantee loss: {coreRecall.Loss + extRecall.Loss}");
    }

    /// <summary>
    /// Per-set outcome. AssemblerHits is what the model receives; RetrievalHits is the hard fixture's
    /// toolset figure measured on the same call; Lift/Loss are the two off-diagonal cells of that pair.
    /// RetrievedSourceFunctions is a liveness counter for the retrieval arm inside the assembler.
    /// </summary>
    private readonly record struct AssemblerSetRecall(
        int AssemblerHits,
        int RetrievalHits,
        int Cases,
        int Lift,
        int Loss,
        int RetrievedSourceFunctions);

    /// <summary>
    /// Runs the two arms for one golden set and writes its report. Kept as a class so the fixed inputs
    /// (agent, rights, always-on names) are not threaded through every call.
    /// </summary>
    private sealed class AssemblerProbe
    {
        private readonly ISkillToolsetAssembler _assembler;
        private readonly IKnowledgeRetrievalService _retrieval;
        private readonly Agent _agent;
        private readonly List<string> _userRights;
        private readonly IReadOnlySet<string> _alwaysOnNames;

        public AssemblerProbe(
            ISkillToolsetAssembler assembler,
            IKnowledgeRetrievalService retrieval,
            Agent agent,
            List<string> userRights,
            IReadOnlySet<string> alwaysOnNames)
        {
            _assembler = assembler;
            _retrieval = retrieval;
            _agent = agent;
            _userRights = userRights;
            _alwaysOnNames = alwaysOnNames;
        }

        public async Task<AssemblerSetRecall> MeasureSetAsync(string label, List<HardGoldenSetItem> golden)
        {
            TestContext.WriteLine($"=== {label} ({golden.Count} cases) ===");
            var started = DateTime.UtcNow;

            var assemblerHits = 0;
            var retrievalHits = 0;
            var alwaysOnCases = 0;
            var cappedToolsets = 0;
            var toolsetSizeSum = 0;
            var retrievedSourceFunctions = 0;
            var liftBySource = new Dictionary<string, int>(StringComparer.Ordinal);
            var liftDetail = new List<string>();
            var lossDetail = new List<string>();
            var missBothDetail = new List<string>();
            var perLanguageTotal = new Dictionary<string, int>();
            var perLanguageAssemblerHits = new Dictionary<string, int>();
            var perLanguageRetrievalHits = new Dictionary<string, int>();

            foreach (var item in golden)
            {
                if (AcceptsAlwaysOn(item))
                {
                    alwaysOnCases++;
                }

                perLanguageTotal[item.LangCode] = perLanguageTotal.GetValueOrDefault(item.LangCode) + 1;

                // The production call, minus the conversation: no history anchor, no route, so the
                // retrieval query inside is the bare golden question - the same text the arm below embeds.
                var toolset = await _assembler.AssembleAsync(
                    _agent, _userRights, item.Query, conversationId: null, currentRoute: null,
                    TestUserId, item.LangCode, applyLearnedPhraseGuarantee: true);

                var accepted = toolset.Functions.FirstOrDefault(f => item.Accepts(f.Name));
                var assemblerHit = accepted is not null;
                toolsetSizeSum += toolset.Functions.Count;
                retrievedSourceFunctions += toolset.Functions.Count(f => f.ToolsetSource == ToolsetSkillSource.Retrieved);
                if (toolset.Functions.Count >= KnowledgeIndexConstants.MaxToolsForProvider)
                {
                    cappedToolsets++;
                }

                // One retrieval per case, identical to the hard fixture's toolset call, so the two
                // reports agree on what "retrieval-only" means and the cross-encoder runs once for it.
                var retrieved = await _retrieval.RetrieveAsync(
                    item.Query, [], isAdmin: true, KnowledgeIndexConstants.DefaultTopK, currentRoute: null,
                    CancellationToken.None);
                var retrievalHit = retrieved.Candidates.Any(c => item.Accepts(c.Entry.SourceId));

                if (assemblerHit)
                {
                    assemblerHits++;
                    perLanguageAssemblerHits[item.LangCode] = perLanguageAssemblerHits.GetValueOrDefault(item.LangCode) + 1;
                }

                if (retrievalHit)
                {
                    retrievalHits++;
                    perLanguageRetrievalHits[item.LangCode] = perLanguageRetrievalHits.GetValueOrDefault(item.LangCode) + 1;
                }

                if (assemblerHit && !retrievalHit)
                {
                    var source = accepted!.ToolsetSource?.ToString() ?? "unknown";
                    liftBySource[source] = liftBySource.GetValueOrDefault(source) + 1;
                    liftDetail.Add(
                        $"LIFT | expected={item.ExpectedDisplay} | source={source} | langCode={item.LangCode} | " +
                        $"query=\"{item.Query}\"");
                }
                else if (!assemblerHit && retrievalHit)
                {
                    lossDetail.Add(
                        $"LOSS | expected={item.ExpectedDisplay} | langCode={item.LangCode} | " +
                        $"toolset={toolset.Functions.Count} functions | query=\"{item.Query}\"");
                }
                else if (!assemblerHit)
                {
                    missBothDetail.Add(
                        $"MISS/both | expected={item.ExpectedDisplay} | langCode={item.LangCode} | " +
                        $"query=\"{item.Query}\"");
                }
            }

            var elapsedMinutes = (DateTime.UtcNow - started).TotalMinutes;

            TestContext.WriteLine(
                $"Assembler toolset recall (AssembleAsync, what the model actually receives): " +
                $"{assemblerHits}/{golden.Count} = {(double)assemblerHits / golden.Count:P1}");
            TestContext.WriteLine(
                $"Retrieval-only recall@{KnowledgeIndexConstants.DefaultTopK} (RetrieveAsync, same call as " +
                $"the hard report): {retrievalHits}/{golden.Count} = {(double)retrievalHits / golden.Count:P1}");
            TestContext.WriteLine(
                $"Guarantee lift (assembler hit, retrieval-only miss): {liftDetail.Count} " +
                $"[{string.Join(", ", liftBySource.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))}]");
            TestContext.WriteLine(
                $"Guarantee loss (retrieval-only hit, assembler miss): {lossDetail.Count}");
            TestContext.WriteLine(
                $"Always-on cases in the set: {alwaysOnCases} (always reached by the assembler, so they " +
                "can only ever appear as lift)");
            TestContext.WriteLine(
                $"Toolsets at the provider cap ({KnowledgeIndexConstants.MaxToolsForProvider}): " +
                $"{cappedToolsets}/{golden.Count}; mean toolset size {(double)toolsetSizeSum / golden.Count:F1} " +
                $"({elapsedMinutes:F1} min)");

            TestContext.WriteLine($"--- {AssemblerArmTag} recall per query language ---");
            WritePerLanguage(AssemblerArmTag, perLanguageTotal, perLanguageAssemblerHits);
            TestContext.WriteLine(
                $"--- {RetrievalArmTag} recall@{KnowledgeIndexConstants.DefaultTopK} per query language ---");
            WritePerLanguage(RetrievalArmTag, perLanguageTotal, perLanguageRetrievalHits);

            foreach (var loss in lossDetail)
            {
                TestContext.WriteLine(loss);
            }

            foreach (var lift in liftDetail)
            {
                TestContext.WriteLine(lift);
            }

            TestContext.WriteLine($"--- Missed by both arms ({missBothDetail.Count} of {golden.Count}) ---");
            foreach (var miss in missBothDetail)
            {
                TestContext.WriteLine(miss);
            }

            return new AssemblerSetRecall(
                assemblerHits, retrievalHits, golden.Count, liftDetail.Count, lossDetail.Count,
                retrievedSourceFunctions);
        }

        private bool AcceptsAlwaysOn(HardGoldenSetItem item) =>
            _alwaysOnNames.Contains(item.ExpectedSourceId)
            || item.AlsoAcceptedSourceIds.Any(_alwaysOnNames.Contains);

        private static void WritePerLanguage(
            string tag, Dictionary<string, int> total, Dictionary<string, int> hits)
        {
            foreach (var lang in total.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var h = hits.GetValueOrDefault(lang);
                TestContext.WriteLine($"  {tag} {lang,-5}: {h,3}/{total[lang],-3} = {(double)h / total[lang]:P1}");
            }
        }
    }

    private static int? ResolveCaseCap()
    {
        var raw = Environment.GetEnvironmentVariable(MaxCasesEnvVar);
        return int.TryParse(raw, out var cap) && cap > 0 ? cap : null;
    }

    private static List<HardGoldenSetItem> ApplyCap(List<HardGoldenSetItem> cases, int? cap) =>
        cap is null ? cases : cases.Take(cap.Value).ToList();

    private static async Task<IReadOnlySet<string>> LoadAlwaysOnSkillNamesAsync(IServiceScope scope)
    {
        var skillRepository = scope.ServiceProvider.GetRequiredService<IAgentSkillRepository>();
        var skills = await skillRepository.GetAllEnabledAsync(CancellationToken.None);
        return skills
            .Where(s => s.AlwaysOn)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
