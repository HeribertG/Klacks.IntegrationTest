// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Update;

/// <summary>
/// Verifies the DB-enforced single-active-operation guard (partial unique index
/// ix_update_history_single_active) against a real PostgreSQL: at most one Pending/Running row may
/// exist at a time, while terminal rows are unconstrained.
/// Operational precondition: ix_update_history_single_active is a global partial unique index
/// (WHERE status IN (0,1) AND is_deleted=false) with no per-fixture scope. This suite cannot isolate
/// itself via the INTEGRATION_TEST_ requester prefix — any other Pending/Running row left behind in
/// the shared dev database (e.g. by a stuck real update operation) makes both tests below fail with
/// a DbUpdateException on the test's own first insert, before the assertion under test even runs.
/// If these tests fail unexpectedly, first check for stray active rows:
/// SELECT * FROM update_history WHERE status IN (0,1) AND is_deleted=false;
/// and move any genuinely stale row to a terminal status (do not delete) before re-running.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class UpdateHistorySingleActiveGuardTests
{
    private const string TestRequester = "INTEGRATION_TEST_SINGLEACTIVE";

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.UpdateHistory
            .IgnoreQueryFilters()
            .Where(h => h.RequestedBy == TestRequester)
            .ExecuteDeleteAsync();
        await _context.DisposeAsync();
    }

    private static UpdateHistory Row(UpdateOperationStatus status) => new()
    {
        Id = Guid.NewGuid(),
        OperationType = UpdateOperationType.Update,
        Status = status,
        Channel = UpdateChannel.Stable,
        FromVersion = "1.0.0",
        TargetVersion = "1.1.0",
        RequestedBy = TestRequester,
        RequestedAt = DateTime.UtcNow,
    };

    [Test]
    public async Task Second_active_operation_is_rejected_by_the_database()
    {
        _context.UpdateHistory.Add(Row(UpdateOperationStatus.Pending));
        await _context.SaveChangesAsync();

        _context.UpdateHistory.Add(Row(UpdateOperationStatus.Running));

        await Should.ThrowAsync<DbUpdateException>(async () => await _context.SaveChangesAsync());
    }

    [Test]
    public async Task Terminal_operations_do_not_count_as_active()
    {
        _context.UpdateHistory.Add(Row(UpdateOperationStatus.Succeeded));
        _context.UpdateHistory.Add(Row(UpdateOperationStatus.RolledBack));
        await _context.SaveChangesAsync();

        _context.UpdateHistory.Add(Row(UpdateOperationStatus.Pending));

        await Should.NotThrowAsync(async () => await _context.SaveChangesAsync());
    }
}
