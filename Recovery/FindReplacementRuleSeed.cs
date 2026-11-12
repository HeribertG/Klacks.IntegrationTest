// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Seeds the planning rules the find_replacement cost fixtures check against, so that the batched reads of the rule
/// evaluators actually run on the seeded people. One scheduling rule carries a counter rule, two period caps (a month
/// total and a rolling average) and a rest-day rotation: the evaluators apply a rule bound to a scheduling rule only
/// to clients whose contract references it, and the seeded contract is the only one that does, so no other plan of the
/// shared database sees these rules. The restricted time window has no scheduling-rule field; it is bound to the
/// seeded group by its group tag. Every rule is a Warn rule (the counter rule with its own Warn enforcement) and every
/// row carries the fixture prefix in its import source key; each person additionally owns one open compensatory-rest
/// obligation, deleted by seeded client. No overtime cap and no holiday exemption is seeded: an overtime cap reads per
/// person by design, and a global exemption would silence the holiday findings of every plan.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;

namespace Klacks.IntegrationTest.Recovery;

public static class FindReplacementRuleSeed
{
    public const string CounterKey = "COUNTER";
    public const string PeriodCapKey = "PERIODCAP";
    public const string RollingAverageKey = "ROLLING";
    public const string RestDayRotationKey = "RESTROTATION";
    public const string RestrictedWindowKey = "WINDOW";
    public const string SchedulingRuleKey = "RULE";

    public static readonly string[] RuleTables =
    [
        "counter_rule",
        "period_cap_rule",
        "rest_day_rotation_rule",
        "restricted_time_window_rule",
    ];

    public static readonly string[] RulePurgeStatements =
    [
        "DELETE FROM counter_rule WHERE starts_with(import_source_key, {0})",
        "DELETE FROM period_cap_rule WHERE starts_with(import_source_key, {0})",
        "DELETE FROM rest_day_rotation_rule WHERE starts_with(import_source_key, {0})",
        "DELETE FROM restricted_time_window_rule WHERE starts_with(import_source_key, {0})",
    ];

    public const string SchedulingRulePurgeStatement = "DELETE FROM scheduling_rules WHERE starts_with(import_source_key, {0})";

    private const string ContentHash = "INTEGRATION_TEST";
    private const int CounterThreshold = 1;
    private const decimal MonthCapHours = 40m;
    private const int RollingWindowWeeks = 2;
    private const decimal MaxAverageWeeklyHours = 10m;
    private const int RotationMinFreeCount = 2;
    private const int RotationWindowWeeks = 2;
    private const int SeasonFromMonth = 1;
    private const int SeasonFromDay = 1;
    private const int SeasonToMonth = 12;
    private const int SeasonToDay = 31;
    private const int ObligationGapDaysBeforeDay = 3;
    private const int ObligationDueDaysAfterDay = 7;
    private const decimal ObligationShortfallHours = 2m;
    private const decimal ObligationStandardRestHours = 11m;

    private static readonly TimeOnly WindowStart = new(12, 0);
    private static readonly TimeOnly WindowEnd = new(14, 0);

    public static void AddSchedulingRule(DataBaseContext context, string prefix, Guid schedulingRuleId)
    {
        context.SchedulingRules.Add(new SchedulingRule
        {
            Id = schedulingRuleId,
            Name = prefix + SchedulingRuleKey,
            ImportSourceKey = prefix + SchedulingRuleKey,
            ImportContentHash = ContentHash,
        });
    }

    public static void AddRules(DataBaseContext context, string prefix, Guid schedulingRuleId, string groupName, DateOnly day)
    {
        context.CounterRule.Add(new CounterRule
        {
            Id = Guid.NewGuid(),
            EventType = CounterEventType.WorkedDayInWeek,
            Period = CounterPeriod.Week,
            Threshold = CounterThreshold,
            Enforcement = RuleEnforcementMode.Warn,
            SchedulingRuleId = schedulingRuleId,
            ImportSourceKey = prefix + CounterKey,
            ImportContentHash = ContentHash,
        });

        context.PeriodCapRule.Add(new PeriodCapRule
        {
            Id = Guid.NewGuid(),
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = MonthCapHours,
            SchedulingRuleId = schedulingRuleId,
            ImportSourceKey = prefix + PeriodCapKey,
            ImportContentHash = ContentHash,
        });

        context.PeriodCapRule.Add(new PeriodCapRule
        {
            Id = Guid.NewGuid(),
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = 0m,
            RollingWindowWeeks = RollingWindowWeeks,
            MaxAverageWeeklyHours = MaxAverageWeeklyHours,
            SchedulingRuleId = schedulingRuleId,
            ImportSourceKey = prefix + RollingAverageKey,
            ImportContentHash = ContentHash,
        });

        context.RestDayRotationRule.Add(new RestDayRotationRule
        {
            Id = Guid.NewGuid(),
            DayOfWeek = day.DayOfWeek,
            MinFreeCount = RotationMinFreeCount,
            WindowWeeks = RotationWindowWeeks,
            SchedulingRuleId = schedulingRuleId,
            ImportSourceKey = prefix + RestDayRotationKey,
            ImportContentHash = ContentHash,
        });

        context.RestrictedTimeWindowRule.Add(new RestrictedTimeWindowRule
        {
            Id = Guid.NewGuid(),
            SeasonFromMonth = SeasonFromMonth,
            SeasonFromDay = SeasonFromDay,
            SeasonToMonth = SeasonToMonth,
            SeasonToDay = SeasonToDay,
            DailyStart = WindowStart,
            DailyEnd = WindowEnd,
            AppliesToGroupTag = groupName,
            ImportSourceKey = prefix + RestrictedWindowKey,
            ImportContentHash = ContentHash,
        });
    }

    public static void AddObligation(DataBaseContext context, Guid clientId, DateOnly day)
    {
        var triggerDate = day.AddDays(-ObligationGapDaysBeforeDay);
        context.CompensatoryRestObligation.Add(new CompensatoryRestObligation
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TriggerDate = triggerDate,
            RestGapStart = triggerDate.ToDateTime(TimeOnly.MinValue),
            ShortfallHours = ObligationShortfallHours,
            StandardRestHours = ObligationStandardRestHours,
            DueDate = day.AddDays(ObligationDueDaysAfterDay),
        });
    }
}
