// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end proofs of the Wizard-1 apply compliance seam against the integration DB (5434):
/// a deterministic GA run (fixed seed) is materialised through the REAL IWizardApplyService of the
/// in-process server, so the REAL CompliancePartitionService, PreCommitConflictChecker and
/// PeriodCapEvaluator decide row by row. Case 1: an active Block-mode PeriodCapRule keeps the
/// cap-busting rows out of the DB and reports them as SkippedPlacements. Case 2: the same rule in
/// Warn mode materialises everything, surfaces the violation on the response AND a direct
/// PeriodValidationLoader run proves the error list carries the entry. Case 3: ApplyAsScenario
/// partitions AFTER the clone-slot soft-delete under the NEW scenario token — the planner row on
/// the incumbent's slot must be accepted (no phantom collision), proving the checker saw the
/// flushed clone world. All seeds use the builder's deterministic GUID prefixes or the
/// INTEGRATION_TEST marker; the flipped setting is captured in SetUp and ALWAYS restored.
/// </summary>

using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class WizardApplyComplianceSeamTests : WizardHarnessTestBase
{
    private const string Marker = "INTEGRATION_TEST_WIZCOMPLIANCE_";
    private const string CapRuleGuid = "00000000-0000-0000-00e2-000000000001";
    private const string BlockValue = "block";
    private const string WarnValue = "warn";
    private const decimal TightCapHours = 10m;
    private const int ExpectedTokenCount = 3;

    private static readonly DateOnly PeriodFrom = new(2099, 5, 4);
    private static readonly DateOnly PeriodUntil = new(2099, 5, 6);
    private static readonly DateOnly MonthStart = new(2099, 5, 1);
    private static readonly DateOnly MonthEnd = new(2099, 5, 31);

    private WizardScenarioBuilder _builder = null!;
    private string? _originalEnforcement;
    private bool _enforcementExisted;
    private readonly List<Guid> _scenarioTokens = [];

    [SetUp]
    public async Task SeamSetUp()
    {
        _builder = new WizardScenarioBuilder(Context);
        _scenarioTokens.Clear();

        var setting = await Context.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Type == SettingKeys.ComplianceEnforcementPeriodCap);
        _enforcementExisted = setting is not null;
        _originalEnforcement = setting?.Value;

        await PurgeAppliedArtifactsAsync();
    }

    [TearDown]
    public async Task SeamTearDown()
    {
        await RestoreEnforcementAsync();
        foreach (var token in _scenarioTokens)
        {
            await PurgeScenarioTokenAsync(token);
        }

        await PurgeAppliedArtifactsAsync();
        await Context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM period_cap_rule WHERE id::text LIKE '00000000-0000-0000-00e2-%'");
        await _builder.CleanupAsync();
    }

    [Test]
    public async Task Apply_BlockMode_KeepsCapBustingRowsOutOfDb_AndReportsSkippedPlacements()
    {
        var seeded = await SeedScenarioWithCapRuleAsync();
        var jobId = await RunGaAndCacheAsync(seeded);
        await UpsertEnforcementAsync(BlockValue);

        using var scope = CreateScope();
        var applyService = scope.ServiceProvider.GetRequiredService<IWizardApplyService>();
        var outcome = await applyService.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        outcome.CreatedWorkIds.Count.ShouldBe(1,
            "with a 10h monthly cap and three 8h planned days only the first (greedy) row fits the cap");
        var skipped = outcome.SkippedPlacements;
        skipped.Count.ShouldBe(ExpectedTokenCount - 1,
            "the two cap-busting rows must be reported as skipped placements");
        skipped.ShouldAllBe(s => s.ReasonKey == ScheduleValidationKeys.PeriodCap);
        skipped.ShouldAllBe(s => s.ClientId == seeded.AgentIds[0]);
        outcome.OverrideApplied.ShouldBeFalse();

        var realWorks = await LoadRealWorksAsync(seeded.AgentIds[0]);
        realWorks.Count.ShouldBe(1, "only the accepted row may exist as a real Work in the DB");
        realWorks[0].CurrentDate.ShouldBe(PeriodFrom, "the greedy partition accepts the earliest date first");
    }

    [Test]
    public async Task Apply_WarnMode_MaterialisesEverything_AndErrorListCarriesTheViolation()
    {
        var seeded = await SeedScenarioWithCapRuleAsync();
        var jobId = await RunGaAndCacheAsync(seeded);
        await UpsertEnforcementAsync(WarnValue);

        using var scope = CreateScope();
        var applyService = scope.ServiceProvider.GetRequiredService<IWizardApplyService>();
        var outcome = await applyService.ApplyAsync(jobId, overrideBlock: false, CancellationToken.None);

        outcome.CreatedWorkIds.Count.ShouldBe(ExpectedTokenCount, "warn mode must materialise every planned row");
        outcome.SkippedPlacements.ShouldBeEmpty("warn mode blocks nothing");
        outcome.ComplianceViolations.ShouldContain(
            v => v.Comment == ScheduleValidationKeys.PeriodCap && v.ClientId == seeded.AgentIds[0],
            "the cap violation must be surfaced on the apply response, not only in the logs");

        var realWorks = await LoadRealWorksAsync(seeded.AgentIds[0]);
        realWorks.Count.ShouldBe(ExpectedTokenCount);

        var loader = scope.ServiceProvider.GetRequiredService<IPeriodValidationLoader>();
        var issues = await loader.LoadAsync(MonthStart, MonthEnd, null, null, int.MaxValue, CancellationToken.None);
        issues.ShouldContain(
            i => i.MessageKey == ScheduleValidationKeys.PeriodCap && i.ClientId == seeded.AgentIds[0],
            "the error-list engine must report the materialised cap breach for the real plan");
    }

    [Test]
    public async Task ApplyAsScenario_BlockMode_ChecksTheFlushedCloneWorld_UnderTheNewToken()
    {
        var seeded = await SeedScenarioWithCapRuleAsync();
        var jobId = await RunGaAndCacheAsync(seeded);

        // Incumbent movable REAL work on the first planned slot, inserted AFTER the GA ran so the
        // cached plan is unaffected. The clone copies it; the slot soft-delete removes the clone;
        // only then may the partition accept the planner's row on that slot without a collision.
        Context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = seeded.AgentIds[0],
            ShiftId = seeded.ShiftIds[0],
            CurrentDate = PeriodFrom,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
            AnalyseToken = null,
            IsDeleted = false,
        });
        await Context.SaveChangesAsync();

        await UpsertEnforcementAsync(BlockValue);

        using var scope = CreateScope();
        var applyService = scope.ServiceProvider.GetRequiredService<IWizardApplyService>();
        var (resource, outcome) = await applyService.ApplyAsScenarioAsync(
            jobId, null, overrideBlock: false, CancellationToken.None);
        _scenarioTokens.Add(resource.Token);

        outcome.CreatedWorkIds.Count.ShouldBe(1,
            "the first planned slot must be ACCEPTED: the incumbent's clone was removed before the "
            + "partition ran, so no phantom collision may block it (order + token seam)");
        outcome.SkippedPlacements.Count.ShouldBe(ExpectedTokenCount - 1);
        outcome.SkippedPlacements.ShouldAllBe(s => s.ReasonKey == ScheduleValidationKeys.PeriodCap,
            "the blocked rows must be cap blocks, never collisions against the removed incumbent clone");

        var scenarioWorks = await Context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == resource.Token && !w.IsDeleted
                && w.CurrentDate >= PeriodFrom && w.CurrentDate <= PeriodUntil)
            .ToListAsync();
        scenarioWorks.Count.ShouldBe(1, "the scenario world must hold exactly the accepted planner row");
        scenarioWorks[0].CurrentDate.ShouldBe(PeriodFrom);

        var realWorks = await LoadRealWorksAsync(seeded.AgentIds[0]);
        realWorks.Count.ShouldBe(1, "the real plan must stay untouched by a scenario apply");
        realWorks[0].AnalyseToken.ShouldBeNull();
    }

    private async Task<WizardScenarioContext> SeedScenarioWithCapRuleAsync()
    {
        var allDays = new[] { true, true, true, true, true, true, true };
        var spec = new WizardScenarioSpec(
            ScenarioName: "ComplianceSeam",
            ClientCount: 1,
            PeriodFrom: PeriodFrom,
            PeriodUntil: PeriodUntil,
            GuaranteedHoursPerClient: _ => 24m,
            ShiftDefs:
            [
                new Spec.ShiftDef("FD", new TimeOnly(8, 0), new TimeOnly(16, 0), WorkTime: 8m, Quantity: 1, Weekdays: allDays, CuttingAfterMidnight: false),
            ],
            ContractWorkDays: allDays,
            SchedulingRule: new Spec.SchedulingRuleSpec(MaxConsecutiveDays: 7, MinPauseHours: 1m));

        var seeded = await _builder.SeedAsync(spec);

        // Cap rule scoped to the builder's deterministic SchedulingRule so it can never affect
        // clients outside this fixture (the dev app shares this database).
        Context.PeriodCapRule.Add(new PeriodCapRule
        {
            Id = Guid.Parse(CapRuleGuid),
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = TightCapHours,
            SchedulingRuleId = Guid.Parse("00000000-0000-0000-0005-000000000001"),
            ImportSourceKey = Marker + "CAP",
            ImportContentHash = string.Empty,
            IsDeleted = false,
        });
        await Context.SaveChangesAsync();

        return seeded;
    }

    private async Task<Guid> RunGaAndCacheAsync(WizardScenarioContext seeded)
    {
        var config = new TokenEvolutionConfig
        {
            RandomSeed = 42,
            PopulationSize = 20,
            MaxGenerations = 100,
        };
        var (best, _) = await BuildContextAndRunAsync(seeded.ContextRequest, config);

        var plannedTokens = best.Tokens.Where(t => !t.IsLocked).ToList();
        plannedTokens.Count.ShouldBe(ExpectedTokenCount,
            "fixture precondition: the deterministic GA must fill all three day slots");

        var cache = Factory.Services.GetRequiredService<WizardResultCache>();
        var jobId = Guid.NewGuid();
        cache.Store(jobId, best, analyseToken: null);
        return jobId;
    }

    private async Task<List<Work>> LoadRealWorksAsync(Guid clientId)
        => await Context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.ClientId == clientId && w.AnalyseToken == null && !w.IsDeleted
                && w.CurrentDate >= PeriodFrom && w.CurrentDate <= PeriodUntil)
            .ToListAsync();

    private async Task UpsertEnforcementAsync(string value)
    {
        var existing = await Context.Settings
            .FirstOrDefaultAsync(s => s.Type == SettingKeys.ComplianceEnforcementPeriodCap);
        if (existing is null)
        {
            Context.Settings.Add(new Klacks.Api.Domain.Models.Settings.Settings
            {
                Id = Guid.NewGuid(),
                Type = SettingKeys.ComplianceEnforcementPeriodCap,
                Value = value,
            });
        }
        else
        {
            existing.Value = value;
        }

        await Context.SaveChangesAsync();
    }

    private async Task RestoreEnforcementAsync()
    {
        if (_enforcementExisted)
        {
            await UpsertEnforcementAsync(_originalEnforcement ?? string.Empty);
            return;
        }

        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM settings WHERE type = {SettingKeys.ComplianceEnforcementPeriodCap}");
    }

    private async Task PurgeScenarioTokenAsync(Guid token)
    {
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE analyse_token = {token})");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM wizard_run_capture_work WHERE work_id IN (SELECT id FROM work WHERE analyse_token = {token})");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM break WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work_softening WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM client_period_hours WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM schedule_notes WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM group_item WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM shift WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM analyse_scenarios WHERE token = {token}");
    }

    /// <summary>
    /// Removes everything the REAL bulk-add pipeline persisted for the deterministic seed clients
    /// (works, run captures, period-hours cache, change tracking, softenings), keyed exclusively by
    /// the builder's deterministic client GUID prefix or the far-future capture window.
    /// </summary>
    private async Task PurgeAppliedArtifactsAsync()
    {
        var sql = $@"
            DELETE FROM wizard_run_capture_work WHERE capture_id IN
                (SELECT id FROM wizard_run_capture WHERE period_from >= '2099-05-01' AND period_until <= '2099-05-31');
            DELETE FROM wizard_run_capture WHERE period_from >= '2099-05-01' AND period_until <= '2099-05-31';
            DELETE FROM work_softening WHERE client_id::text LIKE '00000000-0000-0000-0001-%'
                OR client_id IN (SELECT id FROM client WHERE id::text LIKE '00000000-0000-0000-0001-%');
            DELETE FROM schedule_change WHERE client_id::text LIKE '00000000-0000-0000-0001-%';
            DELETE FROM client_period_hours WHERE client_id::text LIKE '00000000-0000-0000-0001-%';
            DELETE FROM work_change WHERE work_id IN
                (SELECT id FROM work WHERE client_id::text LIKE '00000000-0000-0000-0001-%');
            DELETE FROM expenses WHERE work_id IN
                (SELECT id FROM work WHERE client_id::text LIKE '00000000-0000-0000-0001-%');
            DELETE FROM work WHERE client_id::text LIKE '00000000-0000-0000-0001-%';
        ";
        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
