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
/// Verifies against a real PostgreSQL that Status acts as a concurrency token on update_history. The
/// out-of-process updater claims and completes rows through raw SQL, so an in-process write that read
/// the row beforehand must fail instead of overwriting a status the updater changed in between. The
/// rows use a terminal status on purpose: the partial unique index allows only one active row in the
/// whole database, and the shared dev database may already hold one.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class UpdateHistoryConcurrencyTokenTests
{
    private const string TestRequester = "INTEGRATION_TEST_CONCURRENCY";

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

    private async Task<UpdateHistory> GivenTrackedRowWhoseStatusMovedUnderneath()
    {
        var row = Row(UpdateOperationStatus.Failed);
        _context.UpdateHistory.Add(row);
        await _context.SaveChangesAsync();

        await _context.UpdateHistory
            .IgnoreQueryFilters()
            .Where(h => h.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, UpdateOperationStatus.Succeeded));

        return row;
    }

    [Test]
    public async Task Updating_a_row_whose_status_moved_underneath_is_rejected()
    {
        var row = await GivenTrackedRowWhoseStatusMovedUnderneath();
        row.Message = "written after the status moved";

        await Should.ThrowAsync<DbUpdateConcurrencyException>(async () => await _context.SaveChangesAsync());
    }

    [Test]
    public async Task Soft_deleting_a_row_whose_status_moved_underneath_is_rejected()
    {
        var row = await GivenTrackedRowWhoseStatusMovedUnderneath();
        _context.UpdateHistory.Remove(row);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(async () => await _context.SaveChangesAsync());
    }

    [Test]
    public async Task Writing_an_untouched_row_still_succeeds()
    {
        var row = Row(UpdateOperationStatus.Failed);
        _context.UpdateHistory.Add(row);
        await _context.SaveChangesAsync();

        row.Message = "nobody else touched this row";

        await Should.NotThrowAsync(async () => await _context.SaveChangesAsync());
    }
}
