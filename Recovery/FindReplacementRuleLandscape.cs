// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// What the shared integration database holds besides the rules a cost fixture seeds itself, read without changing a
/// row (read-only queries on a context whose commands are not counted): the active rules per rule table whose import
/// source key does not start with the fixture prefix, the active overtime caps among them that apply to every client
/// (no scheduling rule; the evaluator reads those per person and a fixture cannot switch them off), and whether the
/// compensatory-rest feature is enabled (a fixed, unprefixed setting this fixture neither writes nor removes). It
/// makes a measurement readable: a foreign overtime cap changes the per-candidate command count and the disabled
/// compensatory-rest feature means that evaluator reads nothing.
/// </summary>
/// <param name="ForeignActiveRules">Active rules per rule table whose import source key does not start with the fixture prefix</param>
/// <param name="ForeignOvertimeCaps">Active overtime-hours period caps without a scheduling rule and without the fixture prefix</param>
/// <param name="CompensatoryRestEnabled">True when the compensatory-rest setting is set to true</param>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klacks.IntegrationTest.Recovery;

public sealed record FindReplacementRuleLandscape(
    IReadOnlyDictionary<string, int> ForeignActiveRules,
    int ForeignOvertimeCaps,
    bool CompensatoryRestEnabled)
{
    private const string EnabledValue = "true";

    public static async Task<FindReplacementRuleLandscape> ReadAsync(DataBaseContext context, string prefix)
    {
        var foreignRules = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var table in FindReplacementRuleSeed.RuleTables)
        {
            foreignRules[table] = await CountAsync(
                context,
                "SELECT count(*)::int AS value FROM " + table + " WHERE NOT is_deleted AND NOT starts_with(import_source_key, {0})",
                prefix);
        }

        var overtimeCaps = await CountAsync(
            context,
            "SELECT count(*)::int AS value FROM period_cap_rule WHERE NOT is_deleted AND scope = {0} AND scheduling_rule_id IS NULL AND NOT starts_with(import_source_key, {1})",
            (int)PeriodCapScope.OvertimeHours,
            prefix);
        var enabled = await CountAsync(
            context,
            "SELECT count(*)::int AS value FROM settings WHERE type = {0} AND lower(value) = {1}",
            SettingKeys.ComplianceCompensatoryRestEnabled,
            EnabledValue);

        return new FindReplacementRuleLandscape(foreignRules, overtimeCaps, enabled > 0);
    }

    public string Describe()
    {
        var rules = string.Join(
            ", ", ForeignActiveRules.Select(entry => FormattableString.Invariant($"{entry.Key}={entry.Value}")));
        return FormattableString.Invariant(
            $"foreign active rules: {rules}; foreign overtime caps without scheduling rule={ForeignOvertimeCaps}; compensatory rest enabled={CompensatoryRestEnabled}");
    }

    private static async Task<int> CountAsync(DataBaseContext context, string sql, params object[] parameters)
    {
        var rows = await context.Database.SqlQueryRaw<int>(sql, parameters).ToListAsync();
        return rows.Single();
    }
}
