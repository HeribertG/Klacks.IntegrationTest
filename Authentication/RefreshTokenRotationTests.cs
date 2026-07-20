// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for per-session refresh token rotation: a normal refresh
/// rotates only its own token (old rejected, new valid), new logins are additive
/// so concurrent tabs/devices are NOT logged out by each other, and expired
/// tokens are pruned. Runs against the real test database (port 5434).
/// </summary>

using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Authentication;

[TestFixture]
[Category("RealDatabase")]
public class RefreshTokenRotationTests
{
    private const string TestUserPrefix = "INTEGRATION_TEST_RT_";

    private DataBaseContext _context = null!;
    private RefreshTokenService _service = null!;
    private string _connectionString = null!;
    private string _userId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        await using var context = CreateContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        _context = CreateContext();
        _service = new RefreshTokenService(_context, Substitute.For<ILogger<RefreshTokenService>>());
        _userId = TestUserPrefix + Guid.NewGuid();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task RotateRefreshToken_NormalRefresh_RejectsOldTokenAndAcceptsNew()
    {
        var original = await _service.CreateRefreshTokenAsync(_userId);

        var rotated = await _service.RotateRefreshTokenAsync(_userId, original);

        rotated.ShouldNotBe(original);
        (await _service.ValidateRefreshTokenAsync(_userId, original)).ShouldBeFalse();
        (await _service.ValidateRefreshTokenAsync(_userId, rotated)).ShouldBeTrue();
    }

    [Test]
    public async Task CreateRefreshToken_IsAdditive_KeepsExistingSessions()
    {
        var sessionA = await _service.CreateRefreshTokenAsync(_userId);
        var sessionB = await _service.CreateRefreshTokenAsync(_userId);

        sessionA.ShouldNotBe(sessionB);
        (await _service.ValidateRefreshTokenAsync(_userId, sessionA)).ShouldBeTrue();
        (await _service.ValidateRefreshTokenAsync(_userId, sessionB)).ShouldBeTrue();
    }

    [Test]
    public async Task RotateRefreshToken_DoesNotInvalidateSiblingSessions()
    {
        var tabA = await _service.CreateRefreshTokenAsync(_userId);
        var tabB = await _service.CreateRefreshTokenAsync(_userId);

        var tabARotated = await _service.RotateRefreshTokenAsync(_userId, tabA);

        (await _service.ValidateRefreshTokenAsync(_userId, tabB)).ShouldBeTrue();
        (await _service.ValidateRefreshTokenAsync(_userId, tabARotated)).ShouldBeTrue();
        (await _service.ValidateRefreshTokenAsync(_userId, tabA)).ShouldBeFalse();
    }

    [Test]
    public async Task RotateRefreshToken_ConcurrentCallsWithSameOldToken_ExactlyOneWins()
    {
        var original = await _service.CreateRefreshTokenAsync(_userId);

        await using var contextB = CreateContext();
        var serviceB = new RefreshTokenService(contextB, Substitute.For<ILogger<RefreshTokenService>>());

        // Pre-open both connections so connection-establishment latency doesn't
        // accidentally serialize the two calls below and mask the race.
        await _context.Database.OpenConnectionAsync();
        await contextB.Database.OpenConnectionAsync();

        // Simulates the HTTP interceptor and SignalR (or two browser tabs) racing to
        // rotate the same refresh token at the same instant. Before the rotation was
        // made atomic (a single ExecuteDeleteAsync), both callers could pass a separate
        // read-then-write check and both mutate the table, producing an inconsistent
        // result instead of a clean winner/loser split.
        var taskA = TryRotateAsync(_service, _userId, original);
        var taskB = TryRotateAsync(serviceB, _userId, original);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.Success).ShouldBe(1);
        results.Count(r => !r.Success).ShouldBe(1);

        var remaining = await _context.RefreshToken
            .Where(rt => rt.AspNetUsersId == _userId)
            .ToListAsync();
        remaining.Count.ShouldBe(1);
        var winnerToken = results.Single(r => r.Success).Token!;
        (await _service.ValidateRefreshTokenAsync(_userId, winnerToken)).ShouldBeTrue();
    }

    [Test]
    public async Task RotateRefreshToken_WithUnknownToken_ThrowsAndIssuesNothing()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.RotateRefreshTokenAsync(_userId, "never-issued-token"));

        var count = await _context.RefreshToken
            .CountAsync(rt => rt.AspNetUsersId == _userId);
        count.ShouldBe(0);
    }

    [Test]
    public async Task CreateRefreshToken_PrunesExpiredTokens()
    {
        var expired = new RefreshToken
        {
            AspNetUsersId = _userId,
            Token = "expired-" + Guid.NewGuid(),
            ExpiryDate = DateTime.UtcNow.AddDays(-1),
        };
        _context.RefreshToken.Add(expired);
        await _context.SaveChangesAsync();

        await _service.CreateRefreshTokenAsync(_userId);

        var remaining = await _context.RefreshToken
            .Where(rt => rt.AspNetUsersId == _userId)
            .Select(rt => rt.Token)
            .ToListAsync();
        remaining.ShouldNotContain(expired.Token);
    }

    [Test]
    public async Task RemoveAllUserRefreshTokensAsync_RemovesAllTokensForUser_LeavesOtherUsersUntouched()
    {
        var otherUserId = TestUserPrefix + Guid.NewGuid();

        var tabA = await _service.CreateRefreshTokenAsync(_userId);
        var tabB = await _service.CreateRefreshTokenAsync(_userId);
        var otherUserToken = await _service.CreateRefreshTokenAsync(otherUserId);

        await _service.RemoveAllUserRefreshTokensAsync(_userId);

        (await _service.ValidateRefreshTokenAsync(_userId, tabA)).ShouldBeFalse();
        (await _service.ValidateRefreshTokenAsync(_userId, tabB)).ShouldBeFalse();
        (await _service.ValidateRefreshTokenAsync(otherUserId, otherUserToken)).ShouldBeTrue();
    }

    private static async Task<(bool Success, string? Token)> TryRotateAsync(
        RefreshTokenService service, string userId, string oldToken)
    {
        try
        {
            var token = await service.RotateRefreshTokenAsync(userId, oldToken);
            return (true, token);
        }
        catch (InvalidOperationException)
        {
            return (false, null);
        }
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
        await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM refresh_token WHERE asp_net_users_id LIKE '{TestUserPrefix}%';");
    }
}
