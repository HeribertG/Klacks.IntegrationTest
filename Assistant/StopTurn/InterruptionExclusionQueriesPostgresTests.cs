// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The learning and statistics queries that leave out what a stop produced, run by their production
/// repositories against real Postgres (the in-memory provider is LINQ to Objects and would translate anything,
/// see the reviewer memory ef-inmemory-hides-untranslatable-queries): trajectory rows flagged as interrupted
/// stay out of the fitness counts of recipes and phrases, out of the sharpening evidence and out of the
/// chosen-source sample; a usage row a stop cancelled (failure kind Cancelled or UiAction status Cancelled)
/// is neither a call, a success nor a failure, while a still Dispatched row is not a failure either. The
/// last test is the M-7 characterisation: the total usage count still counts the cancelled rows.
/// Every row is keyed by the INTEGRATION_TEST_ prefix (recipe, phrase, skill and chosen-skill names, the
/// intent excerpt), and results that other rows could influence are read by those names.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[TestFixture]
public class InterruptionExclusionQueriesPostgresTests : StopTurnPostgresTestBase
{
    private const int SampleLimit = 5000;

    private readonly Guid _agentId = Guid.NewGuid();
    private string _recipe = null!;
    private string _phrase = null!;

    [SetUp]
    public void SetUpNames()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        _recipe = Prefix + "recipe_" + suffix;
        _phrase = Prefix + "phrase_" + suffix;
    }

    [Test]
    public async Task TheRecipeUsageCount_LeavesOutTheInterruptedTurns()
    {
        await SeedTrajectoriesAsync(
            Trajectory(recipe: _recipe, executed: true),
            Trajectory(recipe: _recipe, executed: true),
            Trajectory(recipe: _recipe, executed: true, interrupted: true, phase: InterruptedTurnPhases.DuringTools));

        var usage = await Repository().CountRecipeUsageAsync(_recipe, DateTime.UtcNow.AddHours(-1));

        usage.Uses.ShouldBe(2);
        usage.Successes.ShouldBe(2);
    }

    [Test]
    public async Task ThePhraseUsageCount_LeavesOutTheInterruptedTurns()
    {
        await SeedTrajectoriesAsync(
            Trajectory(phrase: _phrase, chosen: _phrase, executed: true),
            Trajectory(phrase: _phrase, chosen: _phrase, executed: true, interrupted: true, phase: InterruptedTurnPhases.BeforeText));

        var usage = await Repository().CountPhraseUsageAsync(_phrase, DateTime.UtcNow.AddHours(-1));

        usage.Uses.ShouldBe(1);
    }

    [Test]
    public async Task ARecipeWhoseOnlySuccessfulTurnWasInterrupted_HasNoSuccessfulTurn()
    {
        await SeedTrajectoriesAsync(Trajectory(recipe: _recipe, executed: true, interrupted: true, phase: InterruptedTurnPhases.DuringText));

        (await Repository().HasSuccessfulRecipeTurnAsync(_recipe)).ShouldBeFalse();

        await SeedTrajectoriesAsync(Trajectory(recipe: _recipe, executed: true));

        (await Repository().HasSuccessfulRecipeTurnAsync(_recipe)).ShouldBeTrue();
    }

    [Test]
    public async Task TheSharpeningEvidence_LeavesOutAnInterruptedTurnEvenWhenAMenuCorrectedIt()
    {
        await SeedTrajectoriesAsync(
            Trajectory(corrected: true, correctionType: CorrectionTypes.WrongSkill),
            Trajectory(corrected: true, correctionType: CorrectionTypes.WrongSkill, interrupted: true, phase: InterruptedTurnPhases.DuringTools));

        var evidence = await Repository().GetUncorrectedWrongSkillAsync(_agentId, SampleLimit);

        evidence.Count.ShouldBe(1);
        evidence[0].WasInterrupted.ShouldBeFalse();
    }

    [Test]
    public async Task TheChosenSourceSample_LeavesOutTheInterruptedTurns()
    {
        var kept = Prefix + "chosen_kept_" + Guid.NewGuid().ToString("N")[..8];
        var left = Prefix + "chosen_left_" + Guid.NewGuid().ToString("N")[..8];
        await SeedTrajectoriesAsync(
            Trajectory(chosen: kept, executed: true),
            Trajectory(chosen: left, executed: true, interrupted: true, phase: InterruptedTurnPhases.DuringTools));

        using var scope = Factory.Services.CreateScope();
        var sample = await scope.ServiceProvider.GetRequiredService<ISkillEffectivenessRepository>()
            .GetChosenSourceSampleAsync(DateTime.UtcNow.AddHours(-1), SampleLimit);

        sample.ShouldContain(row => row.LlmChosenSkill == kept);
        sample.ShouldNotContain(row => row.LlmChosenSkill == left);
    }

    [Test]
    public async Task ARecipeTurnCountsAsFailedOnlyByARealFailureNotByWhatAStopCancelled()
    {
        var cancelledKind = Guid.NewGuid();
        var cancelledUiAction = Guid.NewGuid();
        var dispatched = Guid.NewGuid();
        var failedUiAction = Guid.NewGuid();
        var exception = Guid.NewGuid();
        await SeedUsageAsync(Prefix + "usage", cancelledKind, success: false, failureKind: SkillFailureKind.Cancelled);
        await SeedUsageAsync(Prefix + "usage", cancelledUiAction, success: false, uiStatus: UiActionStatus.Cancelled);
        await SeedUsageAsync(Prefix + "usage", dispatched, success: false, uiStatus: UiActionStatus.Dispatched);
        await SeedUsageAsync(Prefix + "usage", failedUiAction, success: false, uiStatus: UiActionStatus.Failed);
        await SeedUsageAsync(Prefix + "usage", exception, success: false, failureKind: SkillFailureKind.Exception);
        await SeedTrajectoriesAsync(
            Trajectory(recipe: _recipe, executed: true, turnId: cancelledKind),
            Trajectory(recipe: _recipe, executed: true, turnId: cancelledUiAction),
            Trajectory(recipe: _recipe, executed: true, turnId: dispatched),
            Trajectory(recipe: _recipe, executed: true, turnId: failedUiAction),
            Trajectory(recipe: _recipe, executed: true, turnId: exception));

        var usage = await Repository().CountRecipeUsageAsync(_recipe, DateTime.UtcNow.AddHours(-1));

        usage.Uses.ShouldBe(5);
        usage.Successes.ShouldBe(3);
    }

    [Test]
    public async Task TheSkillStatistics_CountNeitherACancelledRowAsACallNorAsAFailure()
    {
        var skill = Prefix + "stats_" + Guid.NewGuid().ToString("N")[..8];
        await SeedUsageAsync(skill, Guid.NewGuid(), success: true);
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, failureKind: SkillFailureKind.Exception);
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, failureKind: SkillFailureKind.Cancelled);
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, uiStatus: UiActionStatus.Cancelled);
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, uiStatus: UiActionStatus.Dispatched);
        var from = DateTime.UtcNow.AddHours(-1);

        using var scope = Factory.Services.CreateScope();
        var effectiveness = scope.ServiceProvider.GetRequiredService<ISkillEffectivenessRepository>();
        var stats = await effectiveness.GetSkillCallStatsAsync(from);
        var failures = await effectiveness.GetFailureCountsAsync(from);

        var mine = stats.Single(stat => stat.SkillName == skill);
        mine.Calls.ShouldBe(2);
        mine.Failures.ShouldBe(1);
        failures.ShouldNotContain(count => count.Kind == SkillFailureKind.Cancelled);
        failures.ShouldContain(count => count.Kind == SkillFailureKind.Exception && count.Count >= 1);
    }

    [Test]
    public async Task TheUsageRecordsOfASkill_LeaveOutTheCancelledRowsAndKeepTheDispatchedOne()
    {
        var skill = Prefix + "records_" + Guid.NewGuid().ToString("N")[..8];
        await SeedUsageAsync(skill, Guid.NewGuid(), success: true);
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, failureKind: SkillFailureKind.Cancelled);
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, uiStatus: UiActionStatus.Cancelled);
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, uiStatus: UiActionStatus.Dispatched);

        using var scope = Factory.Services.CreateScope();
        var records = await scope.ServiceProvider.GetRequiredService<Klacks.Api.Application.Interfaces.ISkillUsageRepository>()
            .GetRecordsBySkillAsync(skill, DateTime.UtcNow.AddHours(-1));

        records.Count.ShouldBe(2);
        records.ShouldNotContain(record => record.FailureKind == SkillFailureKind.Cancelled);
        records.ShouldNotContain(record => record.UiActionStatus == UiActionStatus.Cancelled);
    }

    [Test]
    public async Task M7_TheTotalUsageCountOfTheEffectivenessReportStillCountsTheCancelledRows()
    {
        var from = DateTime.UtcNow.AddHours(-1);
        using var scope = Factory.Services.CreateScope();
        var effectiveness = scope.ServiceProvider.GetRequiredService<ISkillEffectivenessRepository>();
        var before = await effectiveness.GetUsageCountAsync(from);
        var skill = Prefix + "total_" + Guid.NewGuid().ToString("N")[..8];
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, failureKind: SkillFailureKind.Cancelled);
        await SeedUsageAsync(skill, Guid.NewGuid(), success: false, uiStatus: UiActionStatus.Cancelled);

        var after = await effectiveness.GetUsageCountAsync(from);

        (after - before).ShouldBeGreaterThanOrEqualTo(2);
    }

    private ISkillSelectionTrajectoryRepository Repository() =>
        Factory.Services.CreateScope().ServiceProvider.GetRequiredService<ISkillSelectionTrajectoryRepository>();

    private SkillSelectionTrajectory Trajectory(
        string? recipe = null,
        string? phrase = null,
        string? chosen = null,
        bool executed = false,
        bool interrupted = false,
        string? phase = null,
        bool corrected = false,
        string correctionType = CorrectionTypes.None,
        Guid? turnId = null) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = _agentId,
        TurnId = turnId,
        UserId = UserId,
        Locale = Language,
        UserMessageHash = Guid.NewGuid().ToString("N")[..16],
        IntentExcerpt = Prefix + "excerpt",
        KnowledgeIndexCandidatesJson = "[]",
        LlmChosenSkill = chosen,
        WasExecuted = executed,
        WasSuccessful = executed ? true : null,
        RecipeName = recipe,
        LearnedPhraseHit = phrase,
        WasCorrected = corrected,
        CorrectionType = correctionType,
        WasInterrupted = interrupted,
        InterruptedPhase = phase
    };

    private static async Task SeedTrajectoriesAsync(params SkillSelectionTrajectory[] rows)
    {
        await using var context = NewContext();
        context.Set<SkillSelectionTrajectory>().AddRange(rows);
        await context.SaveChangesAsync();
    }

    private async Task SeedUsageAsync(
        string skill, Guid turnId, bool success, SkillFailureKind? failureKind = null, UiActionStatus? uiStatus = null)
    {
        await using var context = NewContext();
        context.Set<SkillUsageRecord>().Add(new SkillUsageRecord
        {
            Id = Guid.NewGuid(),
            SkillName = skill,
            Category = SkillCategory.Action,
            UserId = Guid.Parse(UserId),
            TenantId = Guid.NewGuid(),
            Success = success,
            Timestamp = DateTime.UtcNow,
            TurnId = turnId,
            FailureKind = failureKind,
            UiActionStatus = uiStatus
        });
        await context.SaveChangesAsync();
    }
}
