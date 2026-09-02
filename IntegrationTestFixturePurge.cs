// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest;

/// <summary>
/// Hard-deletes integration-test fixture rows that a fixture failed to clean up. A per-test
/// [TearDown] cleanup does not run when the test host dies mid-run and stops at the first failing
/// statement otherwise, so leftovers accumulate in the shared dev database (dev app and integration
/// tests use the same instance). Leftover clients are live, non-deleted rows and are therefore picked
/// up by the production detectors of the running dev app - observed 2026-08-30..09-01 as 52
/// target_hours_drift agent conditions over 26 leaked WriteGuardParity clients.
/// Every statement is scoped by the INTEGRATION_TEST_ prefix on a name/company/reason column, never
/// by business-plausible values (incident 2026-07-03), and the statements run in foreign-key order.
/// </summary>
public static class IntegrationTestFixturePurge
{
    private const string Prefix = "INTEGRATION_TEST_";

    private const string PrefixedClients =
        "SELECT id FROM client WHERE starts_with(name, '" + Prefix + "') OR starts_with(company, '" + Prefix + "')";

    private const string PrefixedShifts =
        "SELECT id FROM shift WHERE starts_with(name, '" + Prefix + "') OR client_id IN (" + PrefixedClients + ")";

    private const string PrefixedAbsences =
        "SELECT id FROM absence WHERE starts_with(name->>'de', '" + Prefix + "')";

    private const string PrefixedWorks =
        "SELECT id FROM work WHERE client_id IN (" + PrefixedClients + ") OR shift_id IN (" + PrefixedShifts + ")";

    private static readonly string[] Statements =
    {
        $"DELETE FROM work_change WHERE work_id IN ({PrefixedWorks}) OR replace_client_id IN ({PrefixedClients})",
        $"DELETE FROM surcharge_item WHERE work_id IN ({PrefixedWorks})",
        $"DELETE FROM expenses WHERE work_id IN ({PrefixedWorks})",
        $"DELETE FROM \"break\" WHERE client_id IN ({PrefixedClients}) OR parent_work_id IN ({PrefixedWorks}) OR absence_id IN ({PrefixedAbsences})",
        $"DELETE FROM break_placeholder WHERE client_id IN ({PrefixedClients}) OR absence_id IN ({PrefixedAbsences})",
        $"DELETE FROM work WHERE client_id IN ({PrefixedClients}) OR shift_id IN ({PrefixedShifts})",
        $"DELETE FROM absence_detail WHERE absence_id IN ({PrefixedAbsences})",
        $"DELETE FROM container_shift_override_items WHERE absence_id IN ({PrefixedAbsences}) OR shift_id IN ({PrefixedShifts})",
        $"DELETE FROM container_template_item WHERE absence_id IN ({PrefixedAbsences}) OR shift_id IN ({PrefixedShifts})",
        $"DELETE FROM container_shift_overrides WHERE container_id IN ({PrefixedShifts})",
        $"DELETE FROM container_template WHERE container_id IN ({PrefixedShifts})",
        $"DELETE FROM shift_expenses WHERE shift_id IN ({PrefixedShifts})",
        $"DELETE FROM shift_required_qualification WHERE shift_id IN ({PrefixedShifts})",
        $"DELETE FROM client_shift_preference WHERE client_id IN ({PrefixedClients}) OR shift_id IN ({PrefixedShifts})",
        $"DELETE FROM group_item WHERE client_id IN ({PrefixedClients}) OR shift_id IN ({PrefixedShifts})",
        $"DELETE FROM client_period_hours WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM client_availability WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM client_qualification WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM schedule_commands WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM schedule_notes WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM history WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM identity_provider_sync_logs WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM membership WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM communication WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM address WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM annotation WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM assigned_group WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM client_image WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM client_contract WHERE client_id IN ({PrefixedClients})",
        $"DELETE FROM sealed_day WHERE starts_with(reason, '{Prefix}')",
        $"DELETE FROM shift WHERE id IN ({PrefixedShifts})",
        $"DELETE FROM contract WHERE starts_with(name, '{Prefix}')",
        $"DELETE FROM absence WHERE id IN ({PrefixedAbsences})",
        $"DELETE FROM client WHERE id IN ({PrefixedClients})",
    };

    /// <summary>
    /// Runs the purge against the integration-test database. Never throws: a purge failure must not
    /// turn a green suite red, it only removes leftovers.
    /// </summary>
    /// <param name="phase">Label written to the progress log, so a leak can be attributed to the run before or after it.</param>
    public static async Task RunAsync(string phase)
    {
        try
        {
            var options = new DbContextOptionsBuilder<DataBaseContext>()
                .UseNpgsql(TestHostDatabase.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options;

            await using var context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

            var removed = 0;
            foreach (var statement in Statements)
            {
                removed += await context.Database.ExecuteSqlRawAsync(statement);
            }

            if (removed > 0)
            {
                await TestContext.Progress.WriteLineAsync(
                    $"IntegrationTestFixturePurge ({phase}): removed {removed} leftover fixture row(s).");
            }
        }
        catch (Exception ex)
        {
            await TestContext.Progress.WriteLineAsync(
                $"IntegrationTestFixturePurge ({phase}) failed: {ex.Message}");
        }
    }
}
