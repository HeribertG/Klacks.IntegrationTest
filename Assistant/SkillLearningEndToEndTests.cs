// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The one thing the unit suite of the learning loop cannot show: that classification, generation,
/// oracle O1, oracle O2 and activation actually work together against the real catalogue, the real
/// retrieval index and a real language model. Everything below this level runs against substitutes,
/// where the answer to "does the generator produce something the routing oracle accepts" is whatever
/// the test author decided it should be.
///
/// Deliberately [Explicit]. It costs real model calls, it loads the ONNX index, and — unavoidably —
/// it writes to the database the dev application shares on port 5434.
///
/// WHAT IT WRITES AND HOW IT IS TAKEN BACK. Seeded rows are recognisable by the INTEGRATION_TEST_
/// prefix in cluster_key (NOT in intent_excerpt: that text is fed to retrieval and to the model, and a
/// marker inside it would change the very thing under test). Cases and candidates hang off the cluster
/// with a cascading foreign key. What the run itself may create — a skill_phrase row for a REAL skill,
/// an agent_recipes row, golden cases — carries no prefix, so those are removed by the identifiers this
/// fixture recorded while creating them, never by a pattern that could match production data.
/// The catalogue is rebuilt afterwards, because a learned phrase changes the embedding text of a real
/// skill and leaving that behind would silently alter routing for everyone on this database.
///
/// It asserts invariants, not outcomes. Whether a wish ends up a phrase, a capability or unservable is
/// a judgement of the model and the corpus; asserting a particular verdict would make the fixture a
/// test of today's model. What must hold regardless: nothing stays claimed, an activated artefact is
/// really reachable, and the database looks afterwards exactly as it did before.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.IntegrationTest.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Explicit("Boots the real host, calls a real language model and writes to the shared dev database on port 5434.")]
[Category("RealDatabase")]
[Category("SlowModelLoad")]
public class SkillLearningEndToEndTests
{
    private const string KeyPrefix = "INTEGRATION_TEST_";
    // Named for what the arm really is. It used to be called phrase_gap, which was the arm that
    // structurally cannot produce a phrase - see the comment on the test below. The arm that learns a
    // phrase is the corrected one.
    private const string RefusalKey = KeyPrefix + "refusal_gap";
    private const string CapabilityKey = KeyPrefix + "capability_gap";
    private const string CorrectionKey = KeyPrefix + "corrected_gap";
    private const int SeededClusterCount = 3;
    private const int RetrievalProbeDepth = 40;
    // Real user ids, not synthetic markers. Oracle O2 mints its identity from the case's user
    // (SkillExecutionOracle.ParseOwner does a Guid.TryParse), so a made-up id leaves the probe with no
    // owner and the composition is reported as unjudged - which is correct behaviour for a missing
    // identity, but tests the wrong thing. Production cases always carry a real user id.
    private const string FirstUser = "672f77e8-e479-4422-8781-84d218377fb3";
    private const string SecondUser = "753fcbc7-8929-4841-a454-6ba208d59ea7";
    private const string Locale = "de";

    // Oblique on purpose: it shares no vocabulary with the name or description of any skill, so
    // retrieval has a real chance of missing it - the situation the loop exists for. This wish carries
    // no correction, and a wish without one can only ever be dismissed (see the test comment), so what
    // it exercises is the loop up to the verdict and the clean release afterwards.
    private const string RefusalWish = "sind die kartenpunkte der teams inzwischen gesetzt";
    // Distinctive vocabulary on purpose. The generator builds its trigger stems out of the wish, and a
    // short generic stem collides with somebody else's phrase: an earlier wording produced the stem
    // "frei" (from "freie Zeitfenster"), which anyWordStart also matches in "freigegebene dateiformate"
    // - the phrase users reach get_export_formats_settings with - so the draft validator rightly threw
    // all three variants away before either oracle saw them. Long, specific nouns collide far less.
    private const string CapabilityWish =
        "welche mitarbeitenden haben im september abwesenheiten und wie steht die kapazitaetsreserve";

    // Hand-authored wish/skill pairs, and the pairing is the point. A successful phrase run FREEZES its
    // wish against its target in skill_learning_golden_cases, where it stays as a regression case for
    // every later run. The first version of this fixture chose the target by rank alone - the
    // best-scoring retrieval hit outside the toolset - and the run of 2026-08-29 duly froze a sentence
    // about staffing headroom in August onto group_ungrouped_by_city_name. The mechanics worked; the
    // lesson was nonsense, and a nonsense golden case is worse than none because it is asserted forever.
    // Every pair below is one a person can read and agree with. Which one is used is still decided at
    // run time - see SelectCorrectionPairAsync - because only the corpus can say which of them is a real
    // routing gap today, but the candidates are authored, never invented.
    private static readonly (string Wish, string Target)[] CorrectionPairs =
    [
        ("koennen im august noch drei leute gleichzeitig weg oder wird es dann zu duenn",
            "check_absence_capacity_reserve"),
        ("wie viele leute duerfen im august gleichzeitig in die ferien ohne dass es eng wird",
            "find_absence_capacity_windows"),
        ("ueberschneiden sich die abwesenheiten in dieser gruppe im september",
            "get_group_absence_overlap"),
        ("ich moechte festlegen an welchen tagen dieser kunde erreichbar ist",
            "set_client_availability")
    ];

    private SignalRTestWebApplicationFactory _factory = null!;
    private Guid _agentId;
    private string _correctionWish = string.Empty;
    private string _correctionTarget = string.Empty;
    private readonly List<Guid> _seededClusters = [];
    private HashSet<Guid> _preexistingLearnedPhrases = [];
    private HashSet<Guid> _preexistingLearnedRecipes = [];
    private int _preexistingCaseCount;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();

        using var scope = _factory.Services.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var agent = await agents.GetDefaultAgentAsync();
        agent.ShouldNotBeNull("No default agent in the database; the loop cannot run at all.");
        _agentId = agent!.Id;

        // Everything the loop writes to skill_phrase has to be told apart from what was already there,
        // and a generated phrase carries no marker of its own. So the pre-existing set is recorded once
        // and the rollback removes exactly the difference - never "all learned phrases", which would
        // delete an installation's real lessons the first time this fixture runs anywhere else.
        _preexistingLearnedPhrases = await LoadLearnedPhraseIdsAsync();
        _preexistingCaseCount = await CountCasesAsync();
        _preexistingLearnedRecipes = await LoadLearnedRecipeIdsAsync();
        TestContext.WriteLine($"BASELINE learned phrases: {_preexistingLearnedPhrases.Count}");

        await PurgeAsync();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory?.Dispose();

    // Three wishes, and the third one is the only one that can ever be learned as a phrase. That path is
    // reachable ONLY through an explicit correction, and that is not a quirk of this fixture: the
    // classifier may name a skill only if it is already in the candidate list
    // (LearnedArtifactGenerator.ReadClassification), and LearnPhraseAsync dismisses precisely when the
    // target IS in that list. A classifier-chosen target therefore always ends as "already routed", so
    // the refusal wish exercises the loop up to the verdict and no further - which is itself worth
    // observing, and was how that dead end was found. The deterministic half of that dead end is pinned
    // in Klacks.UnitTest, SkillLearningPhraseGapPathTests, where it needs no model to be shown.
    [Test]
    public async Task TheLoop_ProcessesSeededWishesEndToEndAndLeavesNothingBehind()
    {
        await SeedClusterAsync(RefusalKey, RefusalWish, [FirstUser, SecondUser, FirstUser]);
        await SeedClusterAsync(CapabilityKey, CapabilityWish, [FirstUser, SecondUser, FirstUser]);
        await SeedCorrectedClusterAsync();

        await ReportBaselineAsync();

        SkillLearningRunSummary summary;
        try
        {
            using var runScope = _factory.Services.CreateScope();
            var loop = runScope.ServiceProvider.GetRequiredService<ISkillLearningLoop>();
            summary = await loop.RunAsync();

            TestContext.WriteLine(
                $"RUN: claimed={summary.Processed} learned={summary.Learned} alreadyRouted={summary.AlreadyRouted} "
                + $"unfulfillable={summary.Unfulfillable} failed={summary.Failed} "
                + $"sharpened={summary.Sharpened} blocked={summary.Blocked}");

            await ReportOutcomesAsync();
            await AssertInvariantsAsync(summary);
        }
        finally
        {
            await PurgeAsync();
            await RefreshCatalogueAsync();
            await AssertDatabaseIsCleanAsync();
        }
    }

    // The claim is the part that can strand data: a cluster left in "learning" is invisible to the next
    // run for a full hour, and a run that processed nothing at all would make every other assertion
    // below vacuously true.
    private async Task AssertInvariantsAsync(SkillLearningRunSummary summary)
    {
        summary.Processed.ShouldBe(SeededClusterCount, "Every seeded cluster must have been claimed.");

        await using var context = NewContext();
        var clusters = await context.SkillLearningClusters
            .AsNoTracking()
            .Where(c => c.ClusterKey.StartsWith(KeyPrefix))
            .ToListAsync();

        clusters.Count.ShouldBe(SeededClusterCount);
        clusters.ShouldAllBe(c => c.Status != SkillLearningClusterStatuses.Learning);
        clusters.ShouldAllBe(c => c.LearningClaimedAtUtc == null);

        // Without this the fixture is theatre. Everything above holds just as well for a run in which
        // the model never answered: the clusters get claimed, released and left exactly as they were,
        // and a test that only checks "nothing is stuck" reports green while the loop is inert.
        // A cluster back on ready with an untouched attempt budget is precisely the signature of an
        // infrastructure failure - the loop is careful not to spend a try on one - so that combination
        // is what has to fail here.
        var unanswered = clusters
            .Where(c => c.Status == SkillLearningClusterStatuses.Ready && c.AttemptCount == 0)
            .ToList();

        unanswered.ShouldBeEmpty(
            "The loop released every cluster without a verdict, so nothing downstream of the classifier "
            + "was exercised at all. Reasons recorded: "
            + string.Join(" | ", unanswered.Select(c => $"{c.ClusterKey}: {c.LastError}")));

        foreach (var cluster in clusters)
        {
            await AssertOutcomeIsRealAsync(cluster, context);
        }
    }

    // An outcome status is a claim about the world: "learned_phrase" says a row exists and the wish now
    // reaches the skill. Both halves are checked, because a status without the artefact would be the
    // worst possible failure - the card would report a lesson nobody can use.
    private async Task AssertOutcomeIsRealAsync(SkillLearningCluster cluster, DataBaseContext context)
    {
        using var scope = _factory.Services.CreateScope();

        if (cluster.Status == SkillLearningClusterStatuses.LearnedPhrase)
        {
            cluster.OutcomeRef.ShouldNotBeNullOrWhiteSpace();
            var phraseId = Guid.Parse(cluster.OutcomeRef!);

            var phrase = await context.SkillPhrases.AsNoTracking().FirstOrDefaultAsync(p => p.Id == phraseId);
            phrase.ShouldNotBeNull("The cluster claims a learned phrase that does not exist.");
            phrase!.Source.ShouldBe(SkillPhraseSources.Learned);
            phrase.Status.ShouldBe(SkillPhraseStatuses.Active);

            var oracle = scope.ServiceProvider.GetRequiredService<ISkillRoutingOracle>();
            var probe = await oracle.ProbeAsync(cluster.IntentExcerpt, cluster.Locale, phrase.OwnerName);
            probe.TargetFound.ShouldBeTrue(
                $"'{phrase.OwnerName}' was learned for \"{cluster.IntentExcerpt}\" but the wish still does not reach it.");

            // The half the mechanics cannot check on their own: WHAT was learned, not just that something
            // was. A golden case is written here and asserted by every later run, so a wish frozen onto a
            // skill it has nothing to do with is a permanent lie in the regression set. The pair is
            // authored, so it can be compared.
            if (string.Equals(cluster.ClusterKey, CorrectionKey, StringComparison.Ordinal))
            {
                phrase.OwnerName.ShouldBe(
                    _correctionTarget,
                    "The phrase was learned for a different skill than the wish was authored against, so "
                    + "the golden case this run froze does not mean what it says.");

                var frozen = await context.SkillLearningGoldenCases
                    .AsNoTracking()
                    .Where(g => g.ClusterId == cluster.Id)
                    .ToListAsync();

                frozen.ShouldAllBe(
                    g => g.Query == _correctionWish && g.ExpectedSourceId == _correctionTarget);
            }
        }

        if (cluster.Status == SkillLearningClusterStatuses.LearnedCapability)
        {
            cluster.OutcomeRef.ShouldNotBeNullOrWhiteSpace();
            var recipe = await context.AgentRecipes.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Name == cluster.OutcomeRef);

            recipe.ShouldNotBeNull("The cluster claims a learned capability that does not exist.");
            recipe!.Origin.ShouldBe(AgentRecipeOrigins.Learned);
            recipe.IsEnabled.ShouldBeTrue();
            recipe.Name.ShouldStartWith(SkillLearningDefaults.LearnedRecipeNamePrefix);
        }
    }

    private async Task ReportBaselineAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var oracle = scope.ServiceProvider.GetRequiredService<ISkillRoutingOracle>();

        foreach (var wish in new[] { RefusalWish, CapabilityWish })
        {
            var probe = await oracle.ProbeAsync(wish, Locale, string.Empty);

            // The whole list, never a prefix. The assembler puts the always-on skills first, so printing
            // only the first few shows nothing but those and makes a perfectly healthy toolset look like
            // retrieval contributed nothing - a misreading this report caused once already.
            TestContext.WriteLine(
                $"BASELINE \"{wish}\" -> {probe.TopSkills.Count} Skills: {string.Join(", ", probe.TopSkills)}");
        }
    }

    private async Task ReportOutcomesAsync()
    {
        await using var context = NewContext();

        var clusters = await context.SkillLearningClusters
            .AsNoTracking()
            .Where(c => c.ClusterKey.StartsWith(KeyPrefix))
            .ToListAsync();

        foreach (var cluster in clusters)
        {
            TestContext.WriteLine(
                $"CLUSTER {cluster.ClusterKey}: status={cluster.Status} outcome={cluster.OutcomeRefKind}/{cluster.OutcomeRef} "
                + $"attempts={cluster.AttemptCount} error={cluster.LastError}");

            var candidates = await context.SkillLearningCandidates
                .AsNoTracking()
                .Where(c => c.ClusterId == cluster.Id)
                .OrderBy(c => c.VariantNo)
                .ToListAsync();

            foreach (var candidate in candidates)
            {
                TestContext.WriteLine(
                    $"  VARIANT {candidate.VariantNo} kind={candidate.Kind} status={candidate.Status} "
                    + $"payload={Shorten(candidate.PayloadJson)} o1={Shorten(candidate.RoutingResultJson)} "
                    + $"o2={Shorten(candidate.ExecutionResultJson)} error={candidate.ErrorText}");
            }

            var golden = await context.SkillLearningGoldenCases
                .AsNoTracking()
                .Where(g => g.ClusterId == cluster.Id)
                .ToListAsync();

            foreach (var goldenCase in golden)
            {
                TestContext.WriteLine($"  GOLDEN \"{goldenCase.Query}\" -> {goldenCase.ExpectedSourceId}");
            }
        }
    }

    // Generous on purpose. At 400 characters the cut landed inside a recipe payload just before its
    // trigger block, so the one thing a rejected capability needs to explain itself - which trigger term
    // collided - was the one thing never written down, and the candidate rows are gone after rollback.
    private const int PayloadLogLimit = 4000;

    private static string Shorten(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Length <= PayloadLogLimit ? value : value[..PayloadLogLimit] + "…";

    // An authored pair still has to be a real routing gap today, and only the corpus can say that. Two
    // conditions, and neither may be assumed: the target must NOT be in the assembled toolset - otherwise
    // the loop rightly dismisses the wish as already routed and nothing is learned - and it must be among
    // the retrieval hits, because oracle O1 demands that the ORIGINAL wish reaches the target after the
    // phrase is added, and no wording bolted onto a skill retrieval never considers will achieve that.
    //
    // What is deliberately NOT done here is falling back to some other skill when no pair qualifies. That
    // fallback is exactly how the meaningless golden case of 2026-08-29 came about: the fixture always
    // found *a* target, so it always ran, and nobody had to notice that the target had stopped making
    // sense. A corpus in which none of the authored pairs is a gap any more is a fact about the corpus,
    // and it belongs in a failure message that names each pair and why it dropped out - not in a
    // substitute target that keeps the run green.
    private async Task SeedCorrectedClusterAsync()
    {
        var (wish, target, diagnosis) = await SelectCorrectionPairAsync();

        target.ShouldNotBeNullOrEmpty(
            "None of the authored wish/skill pairs is a routing gap in this corpus, so there is nothing "
            + "honest to learn. Author a new pair rather than letting the fixture pick one by rank:\n"
            + diagnosis);

        _correctionWish = wish;
        _correctionTarget = target;

        TestContext.WriteLine($"CORRECTION pair \"{wish}\" -> {target}\n{diagnosis}");

        await SeedClusterAsync(
            CorrectionKey, wish, [FirstUser, SecondUser, FirstUser], target, expectedSkill: target);
    }

    private async Task<(string Wish, string Target, string Diagnosis)> SelectCorrectionPairAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var retrieval = scope.ServiceProvider.GetRequiredService<IKnowledgeRetrievalService>();
        var oracle = scope.ServiceProvider.GetRequiredService<ISkillRoutingOracle>();

        var report = new List<string>();

        foreach (var (wish, target) in CorrectionPairs)
        {
            var probe = await oracle.ProbeAsync(wish, Locale, target);

            var hits = await retrieval.RetrieveAsync(
                wish, [Roles.Admin], true, RetrievalProbeDepth, null, CancellationToken.None,
                KnowledgeEntryKind.Skill);

            var retrieved = hits.Candidates
                .Any(c => string.Equals(c.Entry.SourceId, target, StringComparison.OrdinalIgnoreCase));

            report.Add(
                $"  \"{wish}\" -> {target}: offered={probe.TargetFound} retrieved={retrieved} "
                + $"(toolset {probe.TopSkills.Count}, hits {hits.Candidates.Count})");

            if (!probe.TargetFound && retrieved)
            {
                return (wish, target, string.Join(Environment.NewLine, report));
            }
        }

        return (string.Empty, string.Empty, string.Join(Environment.NewLine, report));
    }

    private async Task SeedClusterAsync(
        string clusterKey,
        string wish,
        IReadOnlyList<string> users,
        string? chosenSkill = null,
        string? expectedSkill = null)
    {
        await using var context = NewContext();
        var now = DateTime.UtcNow;

        var cluster = new SkillLearningCluster
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            ClusterKey = clusterKey,
            IntentExcerpt = wish,
            Locale = Locale,
            OccurrenceCount = users.Count,
            DistinctUserCount = users.Distinct(StringComparer.Ordinal).Count(),
            SignalKindsJson = expectedSkill == null
                ? $"{{\"{SkillLearningSignals.Refusal}\":{users.Count}}}"
                : $"{{\"{SkillLearningSignals.WrongSkill}\":{users.Count}}}",
            Status = SkillLearningClusterStatuses.Ready,
            StatusChangedAtUtc = now,
            FirstSeenAtUtc = now.AddDays(-2),
            LastSeenAtUtc = now
        };

        context.SkillLearningClusters.Add(cluster);

        for (var index = 0; index < users.Count; index++)
        {
            context.SkillLearningCases.Add(new SkillLearningCase
            {
                Id = Guid.NewGuid(),
                ClusterId = cluster.Id,
                UserId = users[index],
                Locale = Locale,
                IntentExcerpt = wish,
                Signal = expectedSkill == null
                    ? SkillLearningSignals.Refusal
                    : SkillLearningSignals.WrongSkill,
                ChosenSkill = chosenSkill,
                ExpectedSkill = expectedSkill,
                ToolsetJson = "[]",
                IsGolden = index == 0,
                OccurredAtUtc = now.AddHours(-index * 3)
            });
        }

        await context.SaveChangesAsync();
        _seededClusters.Add(cluster.Id);

        TestContext.WriteLine($"SEEDED {clusterKey}: \"{wish}\" ({users.Count} Fälle, {cluster.DistinctUserCount} User)");
    }

    // Raw DELETE, not EF Remove. Every entity here is soft-deleted by OnBeforeSaving, so RemoveRange
    // only sets is_deleted and the rows stay in the database the dev application shares. The first
    // version of this fixture did exactly that and then asserted cleanliness through the soft-delete
    // query filter, which cannot see them - it reported a clean rollback while leaving its seed behind.
    // A test that writes into a shared database has to remove what it wrote, not hide it.
    // Ordered by dependency, and every statement is anchored on the cluster key prefix or on ids
    // reachable from it, so nothing outside this fixture's own rows can ever match.
    private async Task PurgeAsync()
    {
        await using var context = NewContext();

        var artefacts = await context.SkillLearningClusters
            .IgnoreQueryFilters()
            .Where(c => c.ClusterKey.StartsWith(KeyPrefix) && c.OutcomeRef != null)
            .Select(c => new { c.OutcomeRefKind, c.OutcomeRef })
            .ToListAsync();

        foreach (var artefact in artefacts)
        {
            await RemoveArtefactAsync(context, artefact.OutcomeRefKind, artefact.OutcomeRef!);
        }

        const string clusterScope =
            "SELECT id FROM skill_learning_clusters WHERE cluster_key LIKE 'INTEGRATION_TEST_%'";

        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM skill_learning_fitness WHERE candidate_id IN "
            + $"(SELECT id FROM skill_learning_candidates WHERE cluster_id IN ({clusterScope}))");
        await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM skill_learning_golden_cases WHERE cluster_id IN ({clusterScope})");
        await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM skill_learning_candidates WHERE cluster_id IN ({clusterScope})");
        await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM skill_learning_cases WHERE cluster_id IN ({clusterScope})");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM skill_learning_clusters WHERE cluster_key LIKE 'INTEGRATION_TEST_%'");

        // The probe phrases of a REJECTED variant are the normal case, not an accident: PhraseLearner
        // writes a phrase before oracle O1 has judged it and, on rejection, only sets it to Rejected -
        // the row stays deliberately, as the negative list that stops a later round from proposing the
        // same wording again. Those rows hang off no cluster (the failed cluster has no OutcomeRef), so
        // nothing else can reach them. They are removed by difference against the recorded set.
        foreach (var id in (await LoadLearnedPhraseIdsAsync()).Except(_preexistingLearnedPhrases))
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM skill_phrase WHERE id = {0}", id);
        }

        // Same story for recipes, and it bites harder. CapabilityLearner withdraws a recipe by
        // soft-delete - correct in production, because the engine reads through the query filter and a
        // soft-deleted row stops forcing its steps immediately - but the row stays. Without this the
        // fixture leaves one behind on every run that activates and then withdraws, and its own
        // cleanliness check (which reads with IgnoreQueryFilters) is red for every later run.
        foreach (var id in (await LoadLearnedRecipeIdsAsync()).Except(_preexistingLearnedRecipes))
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM skill_phrase WHERE owner_kind = {0} AND owner_name IN "
                + "(SELECT name FROM agent_recipes WHERE id = {1})",
                SkillPhraseOwnerKinds.Recipe, id);
            await context.Database.ExecuteSqlRawAsync("DELETE FROM agent_recipes WHERE id = {0}", id);
        }
    }

    private static async Task<HashSet<Guid>> LoadLearnedRecipeIdsAsync()
    {
        await using var context = NewContext();
        var ids = await context.AgentRecipes
            .IgnoreQueryFilters()
            .Where(r => r.Origin != AgentRecipeOrigins.Seed)
            .Select(r => r.Id)
            .ToListAsync();

        return [.. ids];
    }

    private static async Task<int> CountCasesAsync()
    {
        await using var context = NewContext();
        return await context.SkillLearningCases.IgnoreQueryFilters().CountAsync();
    }

    private static async Task<HashSet<Guid>> LoadLearnedPhraseIdsAsync()
    {
        await using var context = NewContext();
        var ids = await context.SkillPhrases
            .IgnoreQueryFilters()
            .Where(p => p.Source == SkillPhraseSources.Learned)
            .Select(p => p.Id)
            .ToListAsync();

        return [.. ids];
    }

    private static async Task RemoveArtefactAsync(
        DataBaseContext context, string? outcomeKind, string outcomeRef)
    {
        if (string.Equals(outcomeKind, SkillLearningOutcomeKinds.Capability, StringComparison.Ordinal))
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM agent_recipes WHERE name = {0}", outcomeRef);
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM skill_phrase WHERE owner_kind = {0} AND owner_name = {1}",
                SkillPhraseOwnerKinds.Recipe, outcomeRef);
            return;
        }

        if (Guid.TryParse(outcomeRef, out var phraseId))
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM skill_phrase WHERE id = {0}", phraseId);
        }
    }

    // A learned phrase changes the embedding text of a real skill, so the index has to be rebuilt after
    // the rollback as well. Skipping this would leave the shared index describing a phrase that no
    // longer exists.
    private async Task RefreshCatalogueAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ISkillCatalogRefresher>();
        await refresher.RefreshAsync("integration test rollback");
    }

    private async Task AssertDatabaseIsCleanAsync()
    {
        await using var context = NewContext();

        // IgnoreQueryFilters throughout. Without it this check looks through the soft-delete filter and
        // cannot see a row that was merely marked deleted - which is how the first version of this
        // fixture certified a clean rollback while its seed was still sitting in the shared database.
        (await context.SkillLearningClusters.IgnoreQueryFilters()
            .CountAsync(c => c.ClusterKey.StartsWith(KeyPrefix)))
            .ShouldBe(0, "Seeded clusters survived the rollback.");
        (await CountCasesAsync()).ShouldBe(
            _preexistingCaseCount, "Seeded cases survived the rollback.");
        (await LoadLearnedPhraseIdsAsync()).Except(_preexistingLearnedPhrases).ShouldBeEmpty(
            "A learned phrase this run created survived the rollback.");
        (await LoadLearnedRecipeIdsAsync()).Except(_preexistingLearnedRecipes).ShouldBeEmpty(
            "A learned recipe this run created survived the rollback.");

        TestContext.WriteLine("ROLLBACK: database is back to its starting state.");
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, new Microsoft.AspNetCore.Http.HttpContextAccessor());
    }
}
