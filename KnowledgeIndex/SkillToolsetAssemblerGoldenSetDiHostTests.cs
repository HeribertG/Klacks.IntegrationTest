// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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
/// The context set (knowledge-index-golden-context.json) carries route, role and history per case: the route goes to
/// both arms, the rights are built like ChatController.GetCurrentUserRights() (Permissions.ExpandRoles over the role
/// claims) and given to both arms, and the history is served from GoldenSetConversationHistoryStore so the
/// production RetrievalQueryBuilder anchors the query; the retrieval-only arm embeds that same anchored query. The
/// short AssembleAsync overload is used, so previousTurnSkillNames and continuationSkillNames stay null: the
/// follow-through and same-skill continuation guarantees are NOT measured by the follow-up cases.
/// The retrieval-only arm passes no kindFilter (same call as the hard report), and SkillRerankMode applies only to
/// passes with kindFilter Skill, so that arm is always cross-encoder ranked: only the assembler figure moves with
/// the rerank mode.
/// </summary>
/// <param name="KLACKS_GOLDENSET_MAX_CASES">
/// Optional environment variable: caps every set to its first N cases so a smoke run finishes in
/// minutes. Unset or non-positive means no cap (the nightly run). Capped figures are NOT comparable to
/// full-set figures - the sets are not shuffled, so the first N cases carry a skewed language mix.
/// </param>
/// <param name="KLACKS_GOLDENSET_LANGS_SOURCES">
/// Optional environment variable: comma-separated skill names; the language-coverage set is reduced to the cases
/// whose accepted targets include one of them (the other sets are untouched). Used to compare a skill change
/// before and after on exactly the cases that can move.
/// </param>
/// <param name="KLACKS_GOLDENSET_SKIP_GAPS">
/// Optional environment variable: "True" leaves out the extra set knowledge-index-golden-hard-gaps.json, which
/// carries cases for skills whose description changed without any case in the other sets (search_shifts views,
/// open_schedule, get_client_details notes, annotation skills, close_period).
/// </param>
/// <param name="KLACKS_GOLDENSET_TOOL_BUDGET">
/// Optional environment variable: maxToolsForProvider handed to AssembleAsync. Unset or non-positive means
/// KnowledgeIndexConstants.MaxToolsForProvider (30); production goes down to ContextBudgetPolicy.MinToolsForProvider (12).
/// </param>
/// <param name="KLACKS_GOLDENSET_SETS">
/// Optional environment variable: comma-separated set keys to run (core, ext, langs, gaps, context, plain). Unset means all.
/// Lets a mode comparison run the context set alone, or core plus ext, without capping any set. Two further keys run
/// only when named explicitly: offtopic (GoldenSetOffTopicProbe over knowledge-index-offtopic.json) and correction
/// (GoldenSetCorrectionProbe over correction-v1.json). Every measured set also reports its assembler misses with the
/// offered tools (GoldenSetMissDiagnostics) and the correction score rule over its toolsets (CorrectionAmbiguityTally).
/// </param>
/// <param name="KnowledgeIndex__SkillRerankMode">
/// Optional environment variable: Always, Adaptive or Never. When set, the fixture replaces the SkillRerankOptions
/// singleton with that mode (exploration rate 0, so Adaptive stays deterministic) instead of relying on the host
/// binding environment variables before the services are registered. The active mode is printed at the start.
/// </param>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
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
    public const string LanguageSetSourcesEnvVar = "KLACKS_GOLDENSET_LANGS_SOURCES";
    public const string SkipGapsSetEnvVar = "KLACKS_GOLDENSET_SKIP_GAPS";
    public const string ToolBudgetEnvVar = "KLACKS_GOLDENSET_TOOL_BUDGET";
    public const string SetsEnvVar = "KLACKS_GOLDENSET_SETS";
    public const string RerankModeEnvVar = "KnowledgeIndex__SkillRerankMode";
    private const string CoreSetKey = "core";
    private const string ExtendedSetKey = "ext";
    private const string LanguageSetKey = "langs";
    private const string GapsSetKey = "gaps";
    private const string ContextSetKey = "context";
    private const string PlainSetKey = "plain";
    private const string OffTopicSetKey = "offtopic";
    private const string CorrectionSetKey = "correction";
    private const string ContextLabel = "ASSEMBLER CONTEXT";
    private const string PlainLabel = "ASSEMBLER PLAIN";
    private const string PlannerRole = "Planner";
    private const string ConversationIdPrefix = "goldenset-context-";
    private const char SourceListSeparator = ',';
    private const string GapsLabel = "ASSEMBLER GAPS";

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

    private static readonly string GapsGoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-hard-gaps.json");

    private static readonly string ContextGoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-context.json");

    private static readonly string PlainGoldenSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-golden-plain.json");

    private static readonly string OffTopicSetPath =
        Path.Combine(AppContext.BaseDirectory, "KnowledgeIndex", "knowledge-index-offtopic.json");

    private static readonly string CorrectionGoldsetPath =
        Path.Combine(AppContext.BaseDirectory, "Application", "Skills", "Goldsets", "correction-v1.json");

    private sealed class NoOpPendingConfirmationStore : IPendingConfirmationStore
    {
        public string Create(
            Guid userId,
            string skillName,
            IReadOnlyDictionary<string, object> parameters,
            string purpose = PendingConfirmationPurposes.GateReplay,
            string channel = PendingConfirmationChannels.Chat,
            ConfirmationBinding? binding = null) =>
            string.Empty;

        public PendingConfirmation? Consume(
            string token,
            Guid userId,
            string? expectedSkillName = null,
            string channel = PendingConfirmationChannels.Chat) => null;

        public PendingConfirmationHandle? PeekLatestForUser(
            Guid userId,
            TimeSpan maxAge,
            string purpose = PendingConfirmationPurposes.GateReplay,
            string channel = PendingConfirmationChannels.Chat) => null;

        public void CreateProposalHint(Guid userId, string applySkillName)
        {
        }

        public void DiscardProposalHints(Guid userId, string? applySkillName = null)
        {
        }

        public void DiscardCorrectionUndo(Guid userId)
        {
        }

        public void DiscardByTokens(Guid userId, IReadOnlyCollection<string> tokens)
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
                services.RemoveAll<IPendingRecipeStore>();
                services.AddSingleton<IPendingRecipeStore, NoOpPendingRecipeStore>();
                services.RemoveAll<IPendingOrderReviewDraftStore>();
                services.AddSingleton<IPendingOrderReviewDraftStore, NoOpPendingOrderReviewDraftStore>();

                // Conversation history of the follow-up cases comes from memory, never from the shared database.
                services.RemoveAll<IPlaygroundConversationStore>();
                services.AddSingleton<GoldenSetConversationHistoryStore>();
                services.AddSingleton<IPlaygroundConversationStore>(sp =>
                    sp.GetRequiredService<GoldenSetConversationHistoryStore>());

                var rerankMode = Environment.GetEnvironmentVariable(RerankModeEnvVar);
                if (!string.IsNullOrWhiteSpace(rerankMode))
                {
                    services.RemoveAll<SkillRerankOptions>();
                    services.AddSingleton(SkillRerankOptions.FromConfiguration(
                        rerankMode, KnowledgeIndexConstants.DefaultSkillRerankMarginThreshold, explorationRate: 0));
                }
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
        var queryBuilder = scope.ServiceProvider.GetRequiredService<IRetrievalQueryBuilder>();
        var historyStore = scope.ServiceProvider.GetRequiredService<GoldenSetConversationHistoryStore>();
        var skillCache = scope.ServiceProvider.GetRequiredService<ISkillCacheService>();

        TestContext.WriteLine($"Active embedding space: {embeddingProvider.EmbeddingSpaceId}");
        TestContext.WriteLine(
            $"Skill rerank mode: {scope.ServiceProvider.GetRequiredService<SkillRerankOptions>().Mode}");

        // Same resolution as the chat paths (SkillCacheService.GetDefaultAgentAsync reads this row):
        // AssembleAsync returns an empty toolset for a null agent, so the agent has to be real.
        var agent = await agentRepository.GetDefaultAgentAsync(CancellationToken.None);
        agent.ShouldNotBeNull("No default agent in the database - the assembler cannot build a toolset.");

        var alwaysOnNames = await LoadAlwaysOnSkillNamesAsync(scope);
        TestContext.WriteLine(
            $"Always-on skills in the catalogue ({alwaysOnNames.Count}): " +
            $"{string.Join(", ", alwaysOnNames.OrderBy(n => n))}");

        var toolBudget = ResolveToolBudget();
        TestContext.WriteLine(
            $"Provider cap (maxToolsForProvider): {toolBudget}, " +
            $"retrieval width (DefaultTopK): {KnowledgeIndexConstants.DefaultTopK}");

        var caseCap = ResolveCaseCap();
        if (caseCap is not null)
        {
            TestContext.WriteLine(
                $"Case cap active: first {caseCap} cases per set ({MaxCasesEnvVar}) - figures below are a " +
                "smoke run, not comparable to the full sets.");
        }

        var selectedSets = ResolveSelectedSets();
        if (selectedSets is not null)
        {
            TestContext.WriteLine($"Set selection active ({SetsEnvVar}): {string.Join(", ", selectedSets)}");
        }

        var probe = new AssemblerProbe(
            assembler, retrieval, agent, alwaysOnNames, queryBuilder, historyStore, toolBudget);

        var measured = new List<(List<HardGoldenSetItem> Cases, AssemblerSetRecall Recall)>();

        // Reported per set, never merged: every hard-fixture figure refers to CORE and EXTENDED
        // separately, and the language set has its own denominator.
        AssemblerSetRecall? coreRecall = null;
        AssemblerSetRecall? extRecall = null;
        if (IsSelected(selectedSets, CoreSetKey))
        {
            var core = ApplyCap(HardGoldenSetItem.Load(GoldenSetPath), caseCap);
            coreRecall = await probe.MeasureSetAsync(CoreLabel, core);
            measured.Add((core, coreRecall.Value));
        }

        if (IsSelected(selectedSets, ExtendedSetKey))
        {
            var extended = ApplyCap(HardGoldenSetItem.Load(ExtendedGoldenSetPath), caseCap);
            extRecall = await probe.MeasureSetAsync(ExtendedLabel, extended);
            measured.Add((extended, extRecall.Value));
        }

        if (coreRecall is not null && extRecall is not null)
        {
            WriteCombined(coreRecall.Value, extRecall.Value);
        }

        if (IsSelected(selectedSets, LanguageSetKey))
        {
            var languages = ApplyCap(
                ApplySourceFilter(HardGoldenSetItem.Load(LanguageCoverageSetPath), ResolveLanguageSetSources()),
                caseCap);
            measured.Add((languages, await probe.MeasureSetAsync(LanguageCoverageLabel, languages)));
        }

        if (IsSelected(selectedSets, GapsSetKey)
            && Environment.GetEnvironmentVariable(SkipGapsSetEnvVar) != bool.TrueString
            && File.Exists(GapsGoldenSetPath))
        {
            var gaps = HardGoldenSetItem.Load(GapsGoldenSetPath);
            measured.Add((gaps, await probe.MeasureSetAsync(GapsLabel, gaps)));
        }

        if (IsSelected(selectedSets, ContextSetKey) && File.Exists(ContextGoldenSetPath))
        {
            var context = ApplyCap(HardGoldenSetItem.Load(ContextGoldenSetPath), caseCap);
            await WriteCatalogueCheckAsync(skillCache, agent.Id, context);
            measured.Add((context, await probe.MeasureSetAsync(ContextLabel, context)));
        }

        if (IsSelected(selectedSets, PlainSetKey) && File.Exists(PlainGoldenSetPath))
        {
            var plain = ApplyCap(HardGoldenSetItem.Load(PlainGoldenSetPath), caseCap);
            await WriteCatalogueCheckAsync(skillCache, agent.Id, plain);
            measured.Add((plain, await probe.MeasureSetAsync(PlainLabel, plain)));
        }

        // Opt-in only: neither set is part of the default (all) run.
        var adminRights = ResolveRights(null);
        var extraSets = 0;
        if (IsExplicitlySelected(selectedSets, OffTopicSetKey))
        {
            extraSets += await new GoldenSetOffTopicProbe(assembler, agent, adminRights, TestUserId, toolBudget)
                .MeasureAsync(OffTopicSetPath);
        }

        if (IsExplicitlySelected(selectedSets, CorrectionSetKey))
        {
            extraSets += (await new GoldenSetCorrectionProbe(assembler, agent, adminRights, TestUserId, toolBudget)
                .MeasureAsync(CorrectionGoldsetPath)).Turns;
        }

        if (measured.Count == 0)
        {
            extraSets.ShouldBeGreaterThan(0);
            return;
        }

        // Retrieval failing inside AssembleAsync is caught there and degrades to always-on plus
        // guarantees. That would still produce plausible-looking figures, so it is checked explicitly.
        var retrievedTotal = measured.Sum(m => m.Recall.RetrievedSourceFunctions);
        TestContext.WriteLine(
            $"Retrieved-source functions across all assembled toolsets: {retrievedTotal}");
        if (retrievedTotal == 0)
        {
            TestContext.WriteLine(
                "WARNING: no assembled toolset contained a Retrieved-source function - retrieval inside " +
                "AssembleAsync most likely failed and was swallowed; the assembler figures above measure " +
                "the guarantee layers alone.");
        }

        measured.ShouldNotBeEmpty();
        foreach (var (cases, _) in measured)
        {
            cases.Count.ShouldBeGreaterThan(0);
        }
    }

    /// <summary>
    /// The context set names skills and roles by hand, while the assembler reads the agent's skills from the
    /// database. A case whose target is missing there, or not permitted for the case's role, can only ever miss,
    /// so it is reported as INVALID instead of being read as a retrieval failure.
    /// </summary>
    private static async Task WriteCatalogueCheckAsync(
        ISkillCacheService skillCache, Guid agentId, List<HardGoldenSetItem> cases)
    {
        var skills = await skillCache.GetEnabledSkillsAsync(agentId, CancellationToken.None);
        var invalid = 0;
        foreach (var item in cases)
        {
            var rights = ResolveRights(item.Role);
            var reachable = skills.Any(s =>
                item.Accepts(s.Name) && Permissions.HasAllRequiredPermissions(rights, s.RequiredPermission));
            if (!reachable)
            {
                invalid++;
                TestContext.WriteLine(
                    $"INVALID CASE | expected={item.ExpectedDisplay} | role={item.Role ?? Roles.Admin} | " +
                    $"query=\"{item.Query}\" - no accepted target is enabled and permitted for this role");
            }
        }

        TestContext.WriteLine($"Catalogue check of the context set: {invalid} invalid of {cases.Count}");
    }

    /// <summary>
    /// Mirrors ChatController.GetCurrentUserRights(): Permissions.ExpandRoles over the role claims. The planner is a
    /// caller without any role claim, which expands to the planner floor. An unknown role fails the run instead of
    /// silently measuring as Admin.
    /// </summary>
    private static List<string> ResolveRights(string? role) => role switch
    {
        null or Roles.Admin => Permissions.ExpandRoles([Roles.Admin]),
        Roles.Authorised => Permissions.ExpandRoles([Roles.Authorised]),
        PlannerRole => Permissions.ExpandRoles([]),
        _ => throw new InvalidOperationException($"Unknown role '{role}' in the golden set.")
    };

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
    /// (agent, always-on names, budget, history source) are not threaded through every call. Rights, route and
    /// history come from each case; a case without them measures as before (Admin, no route, no history).
    /// </summary>
    private sealed class AssemblerProbe
    {
        private readonly ISkillToolsetAssembler _assembler;
        private readonly IKnowledgeRetrievalService _retrieval;
        private readonly Agent _agent;
        private readonly IReadOnlySet<string> _alwaysOnNames;
        private readonly IRetrievalQueryBuilder _queryBuilder;
        private readonly GoldenSetConversationHistoryStore _historyStore;
        private readonly int _toolBudget;
        private int _conversationCounter;

        public AssemblerProbe(
            ISkillToolsetAssembler assembler,
            IKnowledgeRetrievalService retrieval,
            Agent agent,
            IReadOnlySet<string> alwaysOnNames,
            IRetrievalQueryBuilder queryBuilder,
            GoldenSetConversationHistoryStore historyStore,
            int toolBudget)
        {
            _assembler = assembler;
            _retrieval = retrieval;
            _agent = agent;
            _alwaysOnNames = alwaysOnNames;
            _queryBuilder = queryBuilder;
            _historyStore = historyStore;
            _toolBudget = toolBudget;
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
            var caseDetail = new List<string>();
            var perLanguageTotal = new Dictionary<string, int>();
            var perLanguageAssemblerHits = new Dictionary<string, int>();
            var perLanguageRetrievalHits = new Dictionary<string, int>();
            var perSubsetTotal = new Dictionary<string, int>(StringComparer.Ordinal);
            var perSubsetAssemblerHits = new Dictionary<string, int>(StringComparer.Ordinal);
            var perSubsetRetrievalHits = new Dictionary<string, int>(StringComparer.Ordinal);
            var assembleMsWarm = new List<double>();
            var retrieveMsWarm = new List<double>();
            var correctionTally = new CorrectionAmbiguityTally();
            var assemblerMissDetail = new List<string>();

            for (var index = 0; index < golden.Count; index++)
            {
                var item = golden[index];
                if (AcceptsAlwaysOn(item))
                {
                    alwaysOnCases++;
                }

                perLanguageTotal[item.LangCode] = perLanguageTotal.GetValueOrDefault(item.LangCode) + 1;
                if (item.Subset is not null)
                {
                    perSubsetTotal[item.Subset] = perSubsetTotal.GetValueOrDefault(item.Subset) + 1;
                }

                var rights = ResolveRights(item.Role);
                var conversationId = StartConversation(item);

                var assembleWatch = System.Diagnostics.Stopwatch.StartNew();
                var toolset = await _assembler.AssembleAsync(
                    _agent, rights, item.Query, conversationId, item.Route,
                    TestUserId, item.LangCode, maxToolsForProvider: _toolBudget, applyLearnedPhraseGuarantee: true);
                assembleWatch.Stop();

                var accepted = toolset.Functions.FirstOrDefault(f => item.Accepts(f.Name));
                var assemblerHit = accepted is not null;
                correctionTally.Add(toolset.Functions);
                if (!assemblerHit)
                {
                    assemblerMissDetail.AddRange(GoldenSetMissDiagnostics.Describe(
                        item, index, toolset, await AssembleAtReferenceCapAsync(item, rights, conversationId)));
                }
                toolsetSizeSum += toolset.Functions.Count;
                retrievedSourceFunctions += toolset.Functions.Count(f => f.ToolsetSource == ToolsetSkillSource.Retrieved);
                if (toolset.Functions.Count >= _toolBudget)
                {
                    cappedToolsets++;
                }

                // Same query, rights and route as the assembler's retrieval: for a follow-up case the query is the
                // history-anchored text the production builder produces, not the bare golden question.
                var retrievalQuery = await _queryBuilder.BuildAsync(item.Query, conversationId, TestUserId);
                var retrieveWatch = System.Diagnostics.Stopwatch.StartNew();
                var retrieved = await _retrieval.RetrieveAsync(
                    retrievalQuery, rights, rights.Contains(Roles.Admin), KnowledgeIndexConstants.DefaultTopK,
                    item.Route, CancellationToken.None);
                retrieveWatch.Stop();
                var retrievalHit = retrieved.Candidates.Any(c => item.Accepts(c.Entry.SourceId));

                // The first case of a set carries the ONNX cold start and the skill-cache fill.
                if (index > 0)
                {
                    assembleMsWarm.Add(assembleWatch.Elapsed.TotalMilliseconds);
                    retrieveMsWarm.Add(retrieveWatch.Elapsed.TotalMilliseconds);
                }

                if (assemblerHit)
                {
                    assemblerHits++;
                    perLanguageAssemblerHits[item.LangCode] = perLanguageAssemblerHits.GetValueOrDefault(item.LangCode) + 1;
                    if (item.Subset is not null)
                    {
                        perSubsetAssemblerHits[item.Subset] = perSubsetAssemblerHits.GetValueOrDefault(item.Subset) + 1;
                    }
                }

                if (retrievalHit)
                {
                    retrievalHits++;
                    perLanguageRetrievalHits[item.LangCode] = perLanguageRetrievalHits.GetValueOrDefault(item.LangCode) + 1;
                    if (item.Subset is not null)
                    {
                        perSubsetRetrievalHits[item.Subset] = perSubsetRetrievalHits.GetValueOrDefault(item.Subset) + 1;
                    }
                }

                if (item.Subset is not null)
                {
                    caseDetail.Add(
                        $"CASE | {item.Subset} #{index} | assembler={(assemblerHit ? "HIT" : "MISS")} " +
                        $"source={accepted?.ToolsetSource?.ToString() ?? "-"} | " +
                        $"retrieval={(retrievalHit ? "HIT" : "MISS")} | expected={item.ExpectedDisplay} | " +
                        $"role={item.Role ?? Roles.Admin} | route={item.Route ?? "-"} | history={item.History.Count} | " +
                        $"toolset={toolset.Functions.Count} | assembleMs={assembleWatch.Elapsed.TotalMilliseconds:F0} | " +
                        $"query=\"{item.Query}\"");
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
                $"Toolsets at the provider cap ({_toolBudget}): " +
                $"{cappedToolsets}/{golden.Count}; mean toolset size {(double)toolsetSizeSum / golden.Count:F1} " +
                $"({elapsedMinutes:F1} min)");
            TestContext.WriteLine(
                $"Mean latency per case without the first (cold) case: AssembleAsync " +
                $"{(assembleMsWarm.Count == 0 ? 0 : assembleMsWarm.Average()):F0} ms, RetrieveAsync " +
                $"{(retrieveMsWarm.Count == 0 ? 0 : retrieveMsWarm.Average()):F0} ms");

            foreach (var subset in perSubsetTotal.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var total = perSubsetTotal[subset];
                var aHits = perSubsetAssemblerHits.GetValueOrDefault(subset);
                var rHits = perSubsetRetrievalHits.GetValueOrDefault(subset);
                TestContext.WriteLine(
                    $"  SUBSET {subset,-10}: assembler {aHits,2}/{total,-2} = {(double)aHits / total:P1}, " +
                    $"retrieval-only {rHits,2}/{total,-2} = {(double)rHits / total:P1}");
            }

            TestContext.WriteLine($"--- {AssemblerArmTag} recall per query language ---");
            WritePerLanguage(AssemblerArmTag, perLanguageTotal, perLanguageAssemblerHits);
            TestContext.WriteLine(
                $"--- {RetrievalArmTag} recall@{KnowledgeIndexConstants.DefaultTopK} per query language ---");
            WritePerLanguage(RetrievalArmTag, perLanguageTotal, perLanguageRetrievalHits);

            foreach (var line in caseDetail)
            {
                TestContext.WriteLine(line);
            }

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

            TestContext.WriteLine($"--- Assembler misses with offered tools ({golden.Count - assemblerHits}) ---");
            foreach (var line in assemblerMissDetail)
            {
                TestContext.WriteLine(line);
            }

            correctionTally.Write(label);

            return new AssemblerSetRecall(
                assemblerHits, retrievalHits, golden.Count, liftDetail.Count, lossDetail.Count,
                retrievedSourceFunctions);
        }

        /// <summary>
        /// Re-assembles a missed case at the reference cap so the miss report can show which source the target holds
        /// there; null when the fixture already runs at the reference cap. The co-required expansion only runs at this
        /// cap, so the re-assembly can carry an extra expansion skill the measured toolset never had.
        /// </summary>
        private async Task<SkillToolsetResult?> AssembleAtReferenceCapAsync(
            HardGoldenSetItem item, List<string> rights, string? conversationId) =>
            _toolBudget >= KnowledgeIndexConstants.MaxToolsForProvider
                ? null
                : await _assembler.AssembleAsync(
                    _agent, rights, item.Query, conversationId, item.Route, TestUserId, item.LangCode,
                    maxToolsForProvider: KnowledgeIndexConstants.MaxToolsForProvider, applyLearnedPhraseGuarantee: true);

        /// <summary>
        /// A case without history keeps conversationId null, exactly as before. A case with history gets a fresh
        /// conversation id: the scoped LLMConversationManager memoizes history per conversation for the whole run.
        /// </summary>
        private string? StartConversation(HardGoldenSetItem item)
        {
            if (item.History.Count == 0)
            {
                return null;
            }

            var conversationId = $"{ConversationIdPrefix}{++_conversationCounter}";
            _historyStore.Set(conversationId, item.History);
            return conversationId;
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

    private static int ResolveToolBudget()
    {
        var raw = Environment.GetEnvironmentVariable(ToolBudgetEnvVar);
        return int.TryParse(raw, out var budget) && budget > 0 ? budget : KnowledgeIndexConstants.MaxToolsForProvider;
    }

    private static string[]? ResolveSelectedSets()
    {
        var raw = Environment.GetEnvironmentVariable(SetsEnvVar);
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : raw.Split(SourceListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsSelected(string[]? selectedSets, string setKey) =>
        selectedSets is null || IsExplicitlySelected(selectedSets, setKey);

    private static bool IsExplicitlySelected(string[]? selectedSets, string setKey) =>
        selectedSets is not null && selectedSets.Contains(setKey, StringComparer.OrdinalIgnoreCase);

    private static int? ResolveCaseCap()
    {
        var raw = Environment.GetEnvironmentVariable(MaxCasesEnvVar);
        return int.TryParse(raw, out var cap) && cap > 0 ? cap : null;
    }

    private static List<HardGoldenSetItem> ApplyCap(List<HardGoldenSetItem> cases, int? cap) =>
        cap is null ? cases : cases.Take(cap.Value).ToList();

    private static string[]? ResolveLanguageSetSources()
    {
        var raw = Environment.GetEnvironmentVariable(LanguageSetSourcesEnvVar);
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : raw.Split(SourceListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static List<HardGoldenSetItem> ApplySourceFilter(List<HardGoldenSetItem> cases, string[]? sources) =>
        sources is null ? cases : cases.Where(item => sources.Any(item.Accepts)).ToList();

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
