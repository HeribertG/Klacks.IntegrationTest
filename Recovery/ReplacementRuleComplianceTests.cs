// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end A/B proofs that the replacement functions respect the admin-editable rules, driven
/// through the REAL mediator pipeline of the in-process server against the integration database:
/// find_replacement hard-excludes a candidate whose placement would violate the (admin-editable)
/// SchedulingRule min-pause threshold and re-admits him when the rule is loosened; a Block-mode
/// RestrictedTimeWindowRule hard-excludes the candidate and deleting the rule re-admits him; and
/// cover_absence refuses to materialise a recovery delta that violates a Block-mode restricted time
/// window (reported as uncovered/"blocked") while Warn mode materialises it as a Replacement
/// WorkChange and surfaces the violation in the outcome's ComplianceWarnings. All seeded rows carry the INTEGRATION_TEST_RRC_ marker and are cleaned up
/// prefix-scoped; the restricted-time-window rule is scoped to the seeded group's name tag so it can
/// never affect real data; the single flipped global setting is captured in SetUp and ALWAYS
/// restored in TearDown.
/// </summary>

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class ReplacementRuleComplianceTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_RRC_";
    private const string GroupName = Prefix + "GROUP";
    private const string BlockValue = "block";
    private const string WarnValue = "warn";
    private const string BlockedUncoveredReason = "blocked";
    private const decimal BindingMinPauseHours = 11m;
    private const decimal LooseMinPauseHours = 1m;
    private const string AbsenceGuid = "00000000-0000-0000-00e1-000000000001";

    private static readonly DateOnly RestPrevDay = new(2090, 6, 4);
    private static readonly DateOnly RestSlotDay = new(2090, 6, 5);
    private static readonly DateOnly WindowSlotDay = new(2090, 7, 10);
    private static readonly DateOnly AbsenceDay = new(2090, 8, 12);
    private static readonly TimeOnly ShiftStart = new(12, 30);
    private static readonly TimeOnly ShiftEnd = new(20, 30);

    private Guid _groupId;
    private Guid _candidateId;
    private Guid _absentId;
    private Guid _shiftId;
    private Guid _schedulingRuleId;
    private string? _originalRtwEnforcement;
    private bool _rtwEnforcementExisted;
    private readonly List<Guid> _scenarioTokens = [];

    [SetUp]
    public async Task SeedSetUp()
    {
        await PurgeAsync();
        _scenarioTokens.Clear();

        var setting = await Context.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Type == SettingKeys.ComplianceEnforcementRestrictedTimeWindow);
        _rtwEnforcementExisted = setting is not null;
        _originalRtwEnforcement = setting?.Value;

        await SeedFixtureAsync();
    }

    [TearDown]
    public async Task SeedTearDown()
    {
        await RestoreRtwEnforcementAsync();
        foreach (var token in _scenarioTokens)
        {
            await PurgeScenarioTokenAsync(token);
        }

        await PurgeAsync();
    }

    [Test]
    public async Task FindReplacement_MinPauseRule_BindingExcludes_LoosenedAdmits()
    {
        Context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _candidateId,
            CurrentDate = RestPrevDay,
            ShiftId = _shiftId,
            StartTime = new TimeOnly(14, 0),
            EndTime = new TimeOnly(22, 0),
            WorkTime = 8m,
            ParentWorkId = null,
            AnalyseToken = null,
            IsDeleted = false,
        });
        await Context.SaveChangesAsync();

        var query = new FindReplacementQuery(
            ShiftId: _shiftId,
            Date: RestSlotDay,
            StartTime: new TimeOnly(4, 0),
            EndTime: new TimeOnly(12, 0),
            GroupId: _groupId,
            AnalyseToken: null);

        var binding = await SendAsync(query);
        binding.Eligible.ShouldNotContain(c => c.ClientId == _candidateId,
            "with MinPauseHours=11 a 6h rest gap must exclude the candidate");
        var exclusion = binding.Excluded.ShouldHaveSingleItem(
            "only the candidate with the adjacent late shift may be excluded");
        exclusion.ClientId.ShouldBe(_candidateId);
        exclusion.Reason.ShouldBe(ScheduleValidationKeys.RestViolation);

        await UpdateSchedulingRuleMinPauseAsync(LooseMinPauseHours);

        var loosened = await SendAsync(query);
        loosened.Excluded.ShouldNotContain(c => c.ClientId == _candidateId,
            "after the admin lowers MinPauseHours to 1 the same placement must be admitted");
        loosened.Eligible.ShouldContain(c => c.ClientId == _candidateId,
            "the loosened rule must re-admit the candidate");
    }

    [Test]
    public async Task FindReplacement_RestrictedTimeWindowRule_BlockMode_HardExcludes_RuleDeleteReadmits()
    {
        await UpsertRtwEnforcementAsync(BlockValue);

        var query = new FindReplacementQuery(
            ShiftId: _shiftId,
            Date: WindowSlotDay,
            StartTime: ShiftStart,
            EndTime: ShiftEnd,
            GroupId: _groupId,
            AnalyseToken: null);

        var withoutRule = await SendAsync(query);
        withoutRule.Eligible.ShouldContain(c => c.ClientId == _candidateId,
            "without a restricted-time-window rule the candidate is clean");
        CandidateWindowConflicts(withoutRule).ShouldBeEmpty(
            "without the rule no restricted-time-window conflict may be attached");

        await AddRestrictedWindowRuleAsync();

        var withRule = await SendAsync(query);
        withRule.Eligible.ShouldBeEmpty(
            "a Block-mode restricted-time-window breach is an Error-level conflict and must "
            + "hard-exclude every group member whose placement falls into the window");
        withRule.Excluded.ShouldContain(
            c => c.ClientId == _candidateId && c.Reason == ScheduleValidationKeys.RestrictedTimeWindow,
            "the candidate must be excluded with the restricted-time-window reason");

        await DeleteRestrictedWindowRulesAsync();

        var afterDelete = await SendAsync(query);
        afterDelete.Eligible.ShouldContain(c => c.ClientId == _candidateId,
            "deleting the rule must re-admit the candidate");
        CandidateWindowConflicts(afterDelete).ShouldBeEmpty(
            "deleting the rule must remove the conflict again");
    }

    [Test]
    public async Task CoverAbsence_RestrictedWindowBlockMode_RefusesDelta_WarnModeMaterialises()
    {
        Context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _absentId,
            CurrentDate = AbsenceDay,
            ShiftId = _shiftId,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = 8m,
            ParentWorkId = null,
            AnalyseToken = null,
            IsDeleted = false,
        });
        await Context.SaveChangesAsync();
        await AddRestrictedWindowRuleAsync();

        await UpsertRtwEnforcementAsync(BlockValue);
        var blockedOutcome = await SendAsync(
            new CoverAbsenceCommand(_absentId, AbsenceDay, _groupId, Guid.Parse(AbsenceGuid)));
        _scenarioTokens.Add(blockedOutcome.Token);

        blockedOutcome.Covered.ShouldBeEmpty(
            "a delta violating a Block-mode restricted time window must NOT be reported as covered");
        blockedOutcome.Uncovered.ShouldContain(u => u.Date == AbsenceDay && u.Reason == BlockedUncoveredReason,
            "the blocked delta must be reported as uncovered with the 'blocked' reason");
        (await ReplacementWorkChangeCountAsync(blockedOutcome.Token)).ShouldBe(0,
            "no Replacement WorkChange may be materialised for a blocked delta");

        await UpsertRtwEnforcementAsync(WarnValue);
        var warnOutcome = await SendAsync(
            new CoverAbsenceCommand(_absentId, AbsenceDay, _groupId, Guid.Parse(AbsenceGuid)));
        _scenarioTokens.Add(warnOutcome.Token);

        var covered = warnOutcome.Covered.ShouldHaveSingleItem(
            "in Warn mode the same delta must be materialised and reported as covered");
        covered.ReplacementClientId.ShouldBe(_candidateId);
        covered.Date.ShouldBe(AbsenceDay);
        warnOutcome.Uncovered.ShouldBeEmpty("nothing is blocked in Warn mode");
        warnOutcome.ComplianceWarnings.ShouldContain(
            w => w.Comment == ScheduleValidationKeys.RestrictedTimeWindow && w.ClientId == _candidateId,
            "the Warn-mode restricted-time-window violation must be surfaced in the outcome, "
            + "not only in the logs");
        (await ReplacementWorkChangeCountAsync(warnOutcome.Token)).ShouldBe(1,
            "the Warn-mode delta must exist as a Replacement WorkChange in the scenario");
    }

    private async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        using var scope = CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(request, CancellationToken.None);
    }

    private List<Klacks.Api.Application.DTOs.Notifications.ScheduleValidationNotificationDto>
        CandidateWindowConflicts(Klacks.Api.Application.DTOs.Schedules.ReplacementSearchResult result)
    {
        return result.Eligible
            .Where(c => c.ClientId == _candidateId)
            .SelectMany(c => c.SoftConflicts)
            .Where(c => c.Comment == ScheduleValidationKeys.RestrictedTimeWindow)
            .ToList();
    }

    private async Task<int> ReplacementWorkChangeCountAsync(Guid token)
    {
        return await Context.WorkChange
            .AsNoTracking()
            .Where(wc => !wc.IsDeleted
                && wc.ReplaceClientId == _candidateId
                && Context.Work.Any(w => w.Id == wc.WorkId && w.AnalyseToken == token))
            .CountAsync();
    }

    private async Task SeedFixtureAsync()
    {
        _groupId = Guid.NewGuid();
        _candidateId = Guid.NewGuid();
        _absentId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _schedulingRuleId = Guid.NewGuid();

        Context.Group.Add(new Group
        {
            Id = _groupId,
            Name = GroupName,
            ValidFrom = new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = null,
            Root = null,
            IsDeleted = false,
        });

        Context.SchedulingRules.Add(new SchedulingRule
        {
            Id = _schedulingRuleId,
            Name = Prefix + "RULE",
            MinPauseHours = BindingMinPauseHours,
            IsDeleted = false,
        });

        AddClientWithContract(_candidateId, Prefix + "Candidate", _schedulingRuleId);
        AddClientWithContract(_absentId, Prefix + "Absent", _schedulingRuleId);

        Context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = Prefix + "SHIFT",
            Abbreviation = "ITRRC",
            StartShift = ShiftStart,
            EndShift = ShiftEnd,
            WorkTime = 8m,
            FromDate = new DateOnly(2090, 1, 1),
            UntilDate = null,
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            IsSaturday = true,
            IsSunday = true,
            Quantity = 1,
            CuttingAfterMidnight = false,
            Status = ShiftStatus.OriginalOrder,
            IsDeleted = false,
        });

        Context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = _candidateId,
            GroupId = _groupId,
            ValidFrom = new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDeleted = false,
        });
        Context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = _absentId,
            GroupId = _groupId,
            ValidFrom = new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDeleted = false,
        });
        Context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ShiftId = _shiftId,
            GroupId = _groupId,
            ValidFrom = new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDeleted = false,
        });

        var absenceName = new Klacks.Api.Domain.Common.MultiLanguage();
        absenceName.SetValue("en", Prefix + "Absence");
        var absenceAbbreviation = new Klacks.Api.Domain.Common.MultiLanguage();
        absenceAbbreviation.SetValue("en", "ITA");
        var absenceDescription = new Klacks.Api.Domain.Common.MultiLanguage();
        absenceDescription.SetValue("en", "Replacement rule compliance test absence");
        Context.Absence.Add(new Absence
        {
            Id = Guid.Parse(AbsenceGuid),
            Name = absenceName,
            Abbreviation = absenceAbbreviation,
            Description = absenceDescription,
            Color = "#888888",
            IsDeleted = false,
        });

        await Context.SaveChangesAsync();
    }

    private void AddClientWithContract(Guid clientId, string name, Guid schedulingRuleId)
    {
        var contractId = Guid.NewGuid();

        Context.Client.Add(new Client
        {
            Id = clientId,
            FirstName = Prefix,
            Name = name,
            Type = EntityTypeEnum.Employee,
            IsDeleted = false,
        });

        Context.Contract.Add(new Contract
        {
            Id = contractId,
            Name = name + "_Contract",
            ValidFrom = new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = null,
            GuaranteedHours = 100m,
            MaximumHours = 200m,
            FullTime = 40m,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
            SchedulingRuleId = schedulingRuleId,
            IsDeleted = false,
        });

        Context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contractId,
            FromDate = new DateOnly(2090, 1, 1),
            UntilDate = null,
            IsActive = true,
        });
    }

    private async Task UpdateSchedulingRuleMinPauseAsync(decimal minPauseHours)
    {
        var rule = await Context.SchedulingRules.FirstAsync(r => r.Id == _schedulingRuleId);
        rule.MinPauseHours = minPauseHours;
        await Context.SaveChangesAsync();
    }

    private async Task AddRestrictedWindowRuleAsync()
    {
        Context.RestrictedTimeWindowRule.Add(new RestrictedTimeWindowRule
        {
            Id = Guid.NewGuid(),
            SeasonFromMonth = 1,
            SeasonFromDay = 1,
            SeasonToMonth = 12,
            SeasonToDay = 31,
            DailyStart = new TimeOnly(12, 0),
            DailyEnd = new TimeOnly(15, 0),
            AppliesToGroupTag = GroupName,
            ImportSourceKey = $"{Prefix}RTW_{Guid.NewGuid():N}",
            ImportContentHash = string.Empty,
            IsDeleted = false,
        });
        await Context.SaveChangesAsync();
    }

    private async Task DeleteRestrictedWindowRulesAsync()
    {
        await Context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM restricted_time_window_rule WHERE import_source_key LIKE '{Prefix}%'");
    }

    private async Task UpsertRtwEnforcementAsync(string value)
    {
        var existing = await Context.Settings
            .FirstOrDefaultAsync(s => s.Type == SettingKeys.ComplianceEnforcementRestrictedTimeWindow);
        if (existing is null)
        {
            Context.Settings.Add(new Klacks.Api.Domain.Models.Settings.Settings
            {
                Id = Guid.NewGuid(),
                Type = SettingKeys.ComplianceEnforcementRestrictedTimeWindow,
                Value = value,
            });
        }
        else
        {
            existing.Value = value;
        }

        await Context.SaveChangesAsync();
    }

    private async Task RestoreRtwEnforcementAsync()
    {
        if (_rtwEnforcementExisted)
        {
            await UpsertRtwEnforcementAsync(_originalRtwEnforcement ?? string.Empty);
            return;
        }

        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM settings WHERE type = {SettingKeys.ComplianceEnforcementRestrictedTimeWindow}");
    }

    private async Task PurgeScenarioTokenAsync(Guid token)
    {
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE analyse_token = {token})");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM break WHERE analyse_token = {token}");
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
    /// Prefix-scoped hard cleanup, FK children first. Only rows carrying the INTEGRATION_TEST_RRC_
    /// marker (in first_name, name or import_source_key) or referencing such rows are ever deleted;
    /// scenario clones are covered because they keep the seeded client id / shift name / group id.
    /// </summary>
    private async Task PurgeAsync()
    {
        var sql = $@"
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%'));
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM analyse_scenarios WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM shift WHERE name LIKE '{Prefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%';
            DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM contract WHERE name LIKE '{Prefix}%';
            DELETE FROM scheduling_rules WHERE name LIKE '{Prefix}%';
            DELETE FROM restricted_time_window_rule WHERE import_source_key LIKE '{Prefix}%';
            DELETE FROM break WHERE absence_id = '{AbsenceGuid}';
            DELETE FROM absence WHERE id = '{AbsenceGuid}';
            DELETE FROM client WHERE first_name LIKE '{Prefix}%';
        ";
        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
