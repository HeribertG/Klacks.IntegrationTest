// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The narrow unique index on shift (source_system_id, external_order_reference) WHERE reference IS NOT NULL AND
/// is_deleted = false AND status = 0 (one open draft per ERP order reference), against real Postgres.
/// Two fixtures: the shared integration database proves the index's behaviour (a second open draft with the same
/// reference is refused; a sealed order, a deleted draft, another source system, a null source system and a
/// different reference are all allowed -- supersession depends on the sealed/draft pair coexisting), and a
/// throwaway database proves the migration itself: with duplicate open drafts present it fails with a message
/// naming them and deletes nothing, once they are resolved it creates the index, and Down removes it again.
/// Shared-database cleanup deletes ONLY shifts whose source system carries the INTEGRATION_TEST_SHIFTREF_ prefix.
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

[TestFixture]
[Category("RealDatabase")]
public class ShiftOpenDraftReferenceIndexBehaviourTests
{
    private const string SourceSystem = ShiftReferenceIndexSql.Prefix + "A";
    private const string OtherSystem = ShiftReferenceIndexSql.Prefix + "B";
    private const string Reference = "ORD-1";

    private NpgsqlConnection _connection = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connection = new NpgsqlConnection(TestHostDatabase.ConnectionString);
        await _connection.OpenAsync();
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task TheIndex_ExistsWithTheNarrowPredicate_NextToTheNonUniqueLookupIndex()
    {
        var definition = (string?)await ShiftReferenceIndexSql.ScalarAsync(
            _connection, $"SELECT indexdef FROM pg_indexes WHERE indexname = '{ShiftIndexNames.OpenDraftReferenceUnique}'");

        definition.ShouldNotBeNull();
        definition.ShouldContain("UNIQUE");
        definition.ShouldContain("source_system_id, external_order_reference");
        definition.ShouldContain("external_order_reference IS NOT NULL");
        definition.ShouldContain("is_deleted = false");
        definition.ShouldContain("status = 0");

        var lookup = await ShiftReferenceIndexSql.ScalarAsync(
            _connection, $"SELECT indexdef FROM pg_indexes WHERE indexname = '{ShiftReferenceIndexSql.OtherLookupIndex}'");
        ((string?)lookup).ShouldNotBeNull();
        ((string)lookup!).ShouldNotContain("UNIQUE");
    }

    [Test]
    public async Task SecondOpenDraftWithTheSameReference_IsRefusedByTheUniqueIndex()
    {
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, Reference);

        var violation = await Should.ThrowAsync<PostgresException>(
            () => ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, Reference));

        violation.SqlState.ShouldBe(ShiftReferenceIndexSql.UniqueViolationSqlState);
        violation.ConstraintName.ShouldBe(ShiftIndexNames.OpenDraftReferenceUnique);
    }

    [Test]
    public async Task SealedOrderNextToTheOpenDraft_IsAllowed_ThisIsTheSupersessionShape()
    {
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.SealedOrder, SourceSystem, Reference);
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.SealedOrder, SourceSystem, Reference);
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, Reference);

        (await CountAsync(SourceSystem, Reference)).ShouldBe(3L);
    }

    [Test]
    public async Task DeletedDraftNextToTheOpenDraft_IsAllowed_AndADeletedOneDoesNotBlockANewDraft()
    {
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, Reference, isDeleted: true);
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, Reference, isDeleted: true);
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, Reference);

        (await CountAsync(SourceSystem, Reference)).ShouldBe(3L);
    }

    [Test]
    public async Task SameReferenceInAnotherSourceSystem_NullSourceSystem_NullReferenceAndOtherReference_AreAllowed()
    {
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, Reference);
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, OtherSystem, Reference);
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, "ORD-2");
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, null);
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, null);

        (await CountAsync(SourceSystem, Reference)).ShouldBe(1L);
        (await CountAsync(OtherSystem, Reference)).ShouldBe(1L);
    }

    [Test]
    public async Task ReopeningASealedOrderAsADraft_WhileADraftExists_IsRefused()
    {
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.SealedOrder, SourceSystem, Reference);
        await ShiftReferenceIndexSql.InsertShiftAsync(_connection, ShiftStatus.OriginalOrder, SourceSystem, Reference);

        var violation = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(
                "UPDATE shift SET status = 0 WHERE source_system_id = @system AND external_order_reference = @reference AND status = 1",
                _connection);
            command.Parameters.AddWithValue("system", SourceSystem);
            command.Parameters.AddWithValue("reference", Reference);
            await command.ExecuteNonQueryAsync();
        });

        violation.SqlState.ShouldBe(ShiftReferenceIndexSql.UniqueViolationSqlState);
    }

    private async Task<long> CountAsync(string system, string reference)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM shift WHERE source_system_id = @system AND external_order_reference = @reference", _connection);
        command.Parameters.AddWithValue("system", system);
        command.Parameters.AddWithValue("reference", reference);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task CleanupAsync()
    {
        await using var command = new NpgsqlCommand(
            "DELETE FROM shift WHERE source_system_id LIKE @prefix", _connection);
        command.Parameters.AddWithValue("prefix", ShiftReferenceIndexSql.Prefix + "%");
        await command.ExecuteNonQueryAsync();
    }
}
