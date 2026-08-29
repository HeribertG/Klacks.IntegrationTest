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
    private const string PhraseKey = KeyPrefix + "phrase_gap";
    private const string CapabilityKey = KeyPrefix + "capability_gap";
    private const string FirstUser = KeyPrefix + "user_a";
    private const string SecondUser = KeyPrefix + "user_b";
    private const string Locale = "de";

    // Oblique on purpose. Both share no vocabulary with the name or description of the skill they are
    // meant to reach, so retrieval has a real chance of missing them - which is the situation the loop
    // exists for. A wording the index already resolves would end the run as "already routed" and prove
    // nothing about learning.
    private const string PhraseWish = "sind die kartenpunkte der teams inzwischen gesetzt";
    private const string CapabilityWish =
        "sag mir das heutige datum und dazu, welche zeitfenster fuer eine abwesenheit noch frei waeren";

    private SignalRTestWebApplicationFactory _factory = null!;
    private Guid _agentId;
    private readonly List<Guid> _seededClusters = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();

        using var scope = _factory.Services.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var agent = await agents.GetDefaultAgentAsync();
        agent.ShouldNotBeNull("No default agent in the database; the loop cannot run at all.");
        _agentId = agent!.Id;

        await PurgeAsync();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory?.Dispose();

    [Test]
    public async Task TheLoop_ProcessesSeededWishesEndToEndAndLeavesNothingBehind()
    {
        await SeedClusterAsync(PhraseKey, PhraseWish, [FirstUser, SecondUser, FirstUser]);
        await SeedClusterAsync(CapabilityKey, CapabilityWish, [FirstUser, SecondUser, FirstUser]);

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
        summary.Processed.ShouldBe(2, "Both seeded clusters must have been claimed.");

        await using var context = NewContext();
        var clusters = await context.SkillLearningClusters
            .AsNoTracking()
            .Where(c => c.ClusterKey.StartsWith(KeyPrefix))
            .ToListAsync();

        clusters.Count.ShouldBe(2);
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

        foreach (var wish in new[] { PhraseWish, CapabilityWish })
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

    private static string Shorten(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Length <= 400 ? value : value[..400] + "…";

    private async Task SeedClusterAsync(string clusterKey, string wish, IReadOnlyList<string> users)
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
            SignalKindsJson = $"{{\"{SkillLearningSignals.Refusal}\":{users.Count}}}",
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
                Signal = SkillLearningSignals.Refusal,
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
        (await context.SkillLearningCases.IgnoreQueryFilters()
            .CountAsync(c => c.UserId != null && c.UserId.StartsWith(KeyPrefix)))
            .ShouldBe(0, "Seeded cases survived the rollback.");
        (await context.SkillPhrases.IgnoreQueryFilters().CountAsync(p => p.Source == SkillPhraseSources.Learned))
            .ShouldBe(0, "A learned phrase survived the rollback and now affects the shared index.");
        (await context.AgentRecipes.IgnoreQueryFilters().CountAsync(r => r.Origin == AgentRecipeOrigins.Learned))
            .ShouldBe(0, "A learned recipe survived the rollback and would force its steps for everyone.");

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
