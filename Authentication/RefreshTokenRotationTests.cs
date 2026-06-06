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
