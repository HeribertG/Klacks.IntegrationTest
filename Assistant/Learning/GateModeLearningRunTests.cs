// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A description gate run in Gate mode on real Postgres and the real knowledge index. It must measure the
/// proposal while it is live (the index shows the new description during the second replay), then leave the
/// skill exactly as it found it - description, version and indexed text - and store the verdict as
/// gate_metrics_json on a gate_passed proposal.
/// The proposal row is the only row this test creates; its value_after starts with INTEGRATION_TEST_, which is
/// what the cleanup deletes by. The probe skill is a real skill and is only put back, never deleted.
/// </summary>
using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.Learning;

[TestFixture]
[Category("RealDatabase")]
[Category("SlowModelLoad")]
public class GateModeLearningRunTests
{
    private const string ProbeSkillName = "list_agent_skills";
    private const string ProbeMarker = "INTEGRATION_TEST_";
    private const string ProbeMarkerLikePattern = ProbeMarker + "%";
    private const string ProbeText = " gate probe. ";
    private const string VerdictProperty = "verdict";
    private const string RollbackReason = "integration test rollback";

    private GateModeTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new GateModeTestWebApplicationFactory();
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory?.Dispose();

    [Test]
    public async Task AGateRun_LeavesTheSkillAndItsIndexAsTheyWereAndStoresTheMetrics()
    {
        await using (var context = NewContext())
        {
            var foreignPending = await context.ProposedSkillChanges.AsNoTracking()
                .CountAsync(p => p.Status == ProposedChangeStatuses.Pending && p.Field == ProposedChangeFields.Description);
            if (foreignPending > 0)
            {
                Assert.Inconclusive(
                    $"{foreignPending} pending description proposal(s) already exist in this database; the gate would "
                    + "decide them too, and this test never touches rows it did not write.");
            }

            var holdoutGoldenCases = await context.SkillLearningGoldenCases.AsNoTracking()
                .CountAsync(g => g.Partition == GoldenCasePartitions.Holdout);
            if (holdoutGoldenCases < SkillLearningDefaults.MinGoldenCasesForAutoApply)
            {
                Assert.Inconclusive(
                    $"Only {holdoutGoldenCases} holdout golden case(s); the gate does not measure below "
                    + $"{SkillLearningDefaults.MinGoldenCasesForAutoApply}.");
            }
        }

        var skill = await LoadSkillAsync();
        var originalDescription = skill.Description;
        var originalVersion = skill.Version;
        var valueAfter = ProbeMarker + ProbeText + originalDescription;

        var gate = _factory.Services.GetRequiredService<RecordingReplayGate>();
        gate.SkillName = skill.Name;

        var proposalId = await SeedProposalAsync(skill, valueAfter);
        try
        {
            SkillDescriptionSharpenerResult result;
            using (var scope = _factory.Services.CreateScope())
            {
                result = await scope.ServiceProvider.GetRequiredService<ISkillDescriptionSharpener>()
                    .RunAsync(SkillLearningRunTrigger.Manual);
            }

            result.Applied.ShouldBe(1);
            result.Blocked.ShouldBe(0);

            var after = await LoadSkillAsync();
            after.Description.ShouldBe(originalDescription);
            after.Version.ShouldBe(originalVersion);

            gate.IndexTextsSeenByReplays.Count.ShouldBe(2);
            gate.IndexTextsSeenByReplays[0].ShouldStartWith(SkillEmbeddingTextPrefix.Build(skill.Name, originalDescription));
            gate.IndexTextsSeenByReplays[1].ShouldStartWith(
                SkillEmbeddingTextPrefix.Build(skill.Name, valueAfter),
                customMessage: "the second replay has to see the proposed description in the index, or nothing was measured");

            using (var scope = _factory.Services.CreateScope())
            {
                var index = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
                var entries = await index.GetByKeysAsync([(KnowledgeEntryKind.Skill, skill.Name)], CancellationToken.None);
                entries.ShouldHaveSingleItem().Text
                    .ShouldStartWith(SkillEmbeddingTextPrefix.Build(skill.Name, originalDescription));
            }

            await using var verifyContext = NewContext();
            var proposal = await verifyContext.ProposedSkillChanges.AsNoTracking().SingleAsync(p => p.Id == proposalId);
            proposal.Status.ShouldBe(ProposedChangeStatuses.GatePassed);
            using var metrics = JsonDocument.Parse(proposal.GateMetricsJson.ShouldNotBeNull());
            metrics.RootElement.GetProperty(VerdictProperty).GetString().ShouldBe(GoldsetGateVerdicts.Passed);
        }
        finally
        {
            await CleanUpAsync(proposalId, originalDescription, originalVersion);
        }
    }

    private static async Task<AgentSkill> LoadSkillAsync()
    {
        await using var context = NewContext();
        return await context.AgentSkills.AsNoTracking().SingleAsync(s => s.Name == ProbeSkillName);
    }

    private static async Task<Guid> SeedProposalAsync(AgentSkill skill, string valueAfter)
    {
        await using var context = NewContext();
        var proposal = new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            AgentId = skill.AgentId,
            SkillId = skill.Id,
            SkillName = skill.Name,
            Field = ProposedChangeFields.Description,
            Origin = ProposedChangeOrigins.GoldsetEval,
            ValueBefore = skill.Description,
            ValueAfter = valueAfter,
            Justification = ProbeMarker,
            Status = ProposedChangeStatuses.Pending,
            EvidenceJson = GoldsetMissEvidenceCodec.Serialize(
                new GoldsetMissEvidence([ProbeMarker], [RecordingReplayGate.ProbeItem]))
        };

        context.ProposedSkillChanges.Add(proposal);
        await context.SaveChangesAsync();
        return proposal.Id;
    }

    // Deletes only the row this test wrote (id plus marker); the probe skill is put back only if the run left it
    // changed, through the same catalogue refresh production uses.
    private async Task CleanUpAsync(Guid proposalId, string originalDescription, int originalVersion)
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM proposed_skill_changes WHERE id = {0} AND value_after LIKE {1}",
            proposalId, ProbeMarkerLikePattern);

        var skill = await context.AgentSkills.SingleAsync(s => s.Name == ProbeSkillName);
        if (skill.Description == originalDescription && skill.Version == originalVersion)
        {
            return;
        }

        skill.Description = originalDescription;
        skill.Version = originalVersion;
        await context.SaveChangesAsync();

        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISkillCatalogRefresher>()
            .RefreshAndWaitForIndexAsync(RollbackReason);
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
