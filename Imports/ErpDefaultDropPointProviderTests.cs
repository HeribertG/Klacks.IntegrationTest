// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the default ERP drop point's concurrent-first-call race: two
/// simultaneous callers with no existing row both try to create the shared "default" row.
/// Runs against the real test database (port 5434) because the bug this guards against
/// (UnitOfWork classifying the unique-violation via a localized error message) only
/// reproduces against a genuine Npgsql/PostgresException, not the InMemory provider.
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Imports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Imports;

[TestFixture]
[Category("RealDatabase")]
public class ErpDefaultDropPointProviderTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        await using var context = CreateContext();
        await CleanupAsync(context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await using var context = CreateContext();
        await CleanupAsync(context);
    }

    [Test]
    public async Task GetOrCreateDefaultAsync_ConcurrentFirstCalls_BothReturnTheSameSingleRow()
    {
        await using var contextA = CreateContext();
        await using var contextB = CreateContext();

        // Pre-open both connections so connection-establishment latency doesn't
        // accidentally serialize the two calls below and mask the race.
        await contextA.Database.OpenConnectionAsync();
        await contextB.Database.OpenConnectionAsync();

        var providerA = CreateProvider(contextA);
        var providerB = CreateProvider(contextB);

        // Simulates two near-simultaneous requests hitting a fresh database, both finding
        // no existing default drop point and both trying to create it. Before UnitOfWork
        // recognized the unique-violation via SqlState instead of parsing ex.Message for
        // the English word "duplicate", this threw straight through to the caller on a
        // German-locale server ("doppelter Schlüsselwert...") instead of the loser cleanly
        // re-reading the winner's row.
        var taskA = providerA.GetOrCreateDefaultAsync();
        var taskB = providerB.GetOrCreateDefaultAsync();
        var results = await Task.WhenAll(taskA, taskB);

        results[0].Id.ShouldBe(results[1].Id);

        await using var verifyContext = CreateContext();
        var count = await verifyContext.ErpDropPoints
            .CountAsync(d => d.SourceSystemId == ErpDropPointDefaults.SourceSystemId);
        count.ShouldBe(1);
    }

    private static ErpDefaultDropPointProvider CreateProvider(DataBaseContext context)
    {
        var repository = new ErpDropPointRepository(context, Substitute.For<ILogger<Klacks.Api.Domain.Models.Imports.ErpDropPoint>>());
        var unitOfWork = new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>());
        return new ErpDefaultDropPointProvider(repository, unitOfWork);
    }

    private DataBaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM erp_drop_points WHERE source_system_id = {ErpDropPointDefaults.SourceSystemId};");
    }
}
