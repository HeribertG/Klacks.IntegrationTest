// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Raw-SQL helpers for the shift reference index tests: inserts a minimal valid shift row (so the same code runs
/// against the shared database and a throwaway migration database) and reads scalars.
/// </summary>
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Imports;

public static class ShiftReferenceIndexSql
{
    public const string Prefix = "INTEGRATION_TEST_SHIFTREF_";

    public const string OtherLookupIndex = ShiftIndexNames.ExternalOrderReferenceLookup;

    public const string UniqueViolationSqlState = "23505";

    private const string InsertTemplate =
        "INSERT INTO shift (id, cutting_after_midnight, abbreviation, description, name, status, after_shift, "
        + "before_shift, end_shift, from_date, start_shift, briefing_time, debriefing_time, travel_time_after, "
        + "travel_time_before, is_friday, is_holiday, is_monday, is_saturday, is_sunday, is_thursday, is_tuesday, "
        + "is_wednesday, is_weekday_and_holiday, is_sporadic, sporadic_scope, is_time_range, quantity, "
        + "sum_employees, work_time, shift_type, is_deleted, source_system_id, external_order_reference) "
        + "VALUES (@id, false, 'T', 'T', 'INTEGRATION_TEST_SHIFTREF', @status, TIME '00:00', TIME '00:00', "
        + "TIME '01:00', DATE '2026-01-01', TIME '00:00', TIME '00:00', TIME '00:00', TIME '00:00', TIME '00:00', "
        + "false, false, false, false, false, false, false, false, false, false, 0, false, 1, 1, 1, 0, @deleted, "
        + "@system, @reference)";

    public static async Task InsertShiftAsync(
        NpgsqlConnection connection,
        ShiftStatus status,
        string? sourceSystemId,
        string? reference,
        bool isDeleted = false)
    {
        await using var command = new NpgsqlCommand(InsertTemplate, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("status", (int)status);
        command.Parameters.AddWithValue("deleted", isDeleted);
        command.Parameters.AddWithValue("system", (object?)sourceSystemId ?? DBNull.Value);
        command.Parameters.AddWithValue("reference", (object?)reference ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
