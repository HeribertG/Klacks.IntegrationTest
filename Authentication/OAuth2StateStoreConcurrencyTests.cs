// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the concurrency guarantee of the database-backed OAuth2 state store against
/// the real database (port 5434): when two API instances race to consume the same state, the loser
/// must report "already consumed" instead of letting EF's DbUpdateConcurrencyException escape.
/// The race is produced deterministically rather than with threads: a SaveChanges interceptor on the
/// losing context lets the winning instance delete the row after the loser has already read it but
/// before the loser's DELETE is issued, so that DELETE matches zero rows -- exactly the situation the
/// catch in ValidateAndConsumeAsync exists for. The interceptor doubles as the control that tells the
/// two ways of returning false apart: it can only fire when the row was found, so a fired probe
/// proves the concurrency path was taken and an unfired probe proves the missing-row path was.
/// Cleanup deletes only rows whose state begins with this fixture's freshly generated provider id -
/// the state value is produced by production code and cannot carry the INTEGRATION_TEST_ prefix, and
/// this database is shared with the dev app.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Authentication;

[TestFixture]
[Category("RealDatabase")]
public class OAuth2StateStoreConcurrencyTests
{
    private string _connectionString = null!;
    private DataBaseContext _contextA = null!;
    private OAuth2StateStore _storeA = null!;
    private Guid _providerId;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [SetUp]
    public void SetUp()
    {
        _providerId = Guid.NewGuid();
        _contextA = CreateContext();
        _storeA = new OAuth2StateStore(_contextA, TimeProvider.System);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_contextA);
        await _contextA.DisposeAsync();
    }

    private DataBaseContext CreateContext(SaveProbeInterceptor? probe = null)
    {
        var builder = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention();

        if (probe != null)
        {
            builder.AddInterceptors(probe);
        }

        return new DataBaseContext(builder.Options, Substitute.For<IHttpContextAccessor>());
    }

    private async Task CleanupAsync(DataBaseContext context)
    {
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM oauth2_states WHERE state LIKE {0}", _providerId + "%");
    }

    [Test]
    public async Task ValidateAndConsume_Reports_Already_Consumed_When_A_Racing_Instance_Won_The_Delete()
    {
        var state = await _storeA.CreateStateAsync(_providerId);

        var consumedByWinner = false;
        var probe = new SaveProbeInterceptor(async () =>
        {
            consumedByWinner = await _storeA.ValidateAndConsumeAsync(state);
        });

        await using var contextB = CreateContext(probe);
        var storeB = new OAuth2StateStore(contextB, TimeProvider.System);

        var consumedByLoser = await storeB.ValidateAndConsumeAsync(state);

        consumedByWinner.ShouldBeTrue();
        consumedByLoser.ShouldBeFalse();
        probe.Fired.ShouldBeTrue();

        await using var verify = CreateContext();
        (await verify.OAuth2States.CountAsync(row => row.State == state)).ShouldBe(0);
    }

    [Test]
    public async Task ValidateAndConsume_Takes_The_Missing_Row_Path_When_The_Winner_Deleted_Beforehand()
    {
        var state = await _storeA.CreateStateAsync(_providerId);

        var probe = new SaveProbeInterceptor(null);
        await using var contextB = CreateContext(probe);
        var storeB = new OAuth2StateStore(contextB, TimeProvider.System);

        (await contextB.OAuth2States.FirstOrDefaultAsync(row => row.State == state)).ShouldNotBeNull();

        (await _storeA.ValidateAndConsumeAsync(state)).ShouldBeTrue();

        (await storeB.ValidateAndConsumeAsync(state)).ShouldBeFalse();
        probe.Fired.ShouldBeFalse();
    }

    private sealed class SaveProbeInterceptor : SaveChangesInterceptor
    {
        private readonly Func<Task>? _onFirstSave;

        public SaveProbeInterceptor(Func<Task>? onFirstSave)
        {
            _onFirstSave = onFirstSave;
        }

        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                if (_onFirstSave != null)
                {
                    await _onFirstSave();
                }
            }

            return result;
        }
    }
}
