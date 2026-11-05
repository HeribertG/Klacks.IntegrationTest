// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests of the refresh token sessions on the real test database (port 5434): a refresh does not rotate,
/// the same token keeps working and its expiry slides to now + the lifetime; concurrent refreshes of one session all
/// succeed; unknown, revoked, expired and foreign tokens are not extended; new logins are additive, so concurrent devices
/// are not logged out by each other; expired tokens are pruned; RemoveRefreshTokenAsync removes only the presented token,
/// RemoveAllUserRefreshTokensAsync only the tokens of that user.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Security;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Authentication;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Authentication;

[TestFixture]
[Category("RealDatabase")]
public class RefreshTokenSessionTests
{
    private const string TestUserPrefix = "INTEGRATION_TEST_RT_";
    private const int ConcurrentRefreshCount = 2;
    private static readonly TimeSpan ExpiryTolerance = TimeSpan.FromMinutes(1);

    private DataBaseContext _context = null!;
    private RefreshTokenService _service = null!;
    private SettableTimeProvider _clock = null!;
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
        _clock = new SettableTimeProvider(DateTime.UtcNow);
        _service = new RefreshTokenService(_context, _clock, Substitute.For<ILogger<RefreshTokenService>>());
        _userId = TestUserPrefix + Guid.NewGuid();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ExtendRefreshToken_KeepsTheSameTokenValid_AcrossRepeatedRefreshes()
    {
        var token = await _service.CreateRefreshTokenAsync(_userId);

        (await _service.ExtendRefreshTokenAsync(_userId, token)).ShouldBeTrue();
        (await _service.ExtendRefreshTokenAsync(_userId, token)).ShouldBeTrue();

        (await IsValidAsync(_userId, token)).ShouldBeTrue();
    }

    [Test]
    public async Task ExtendRefreshToken_SlidesTheExpiryToNowPlusTheLifetime()
    {
        var token = await _service.CreateRefreshTokenAsync(_userId);
        var hashed = RefreshTokenHasher.Hash(token);
        await _context.RefreshToken.Where(rt => rt.Token == hashed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.ExpiryDate, _clock.Now.AddHours(1)));

        (await _service.ExtendRefreshTokenAsync(_userId, token)).ShouldBeTrue();

        var expiry = await _context.RefreshToken.AsNoTracking()
            .Where(rt => rt.Token == hashed)
            .Select(rt => rt.ExpiryDate)
            .SingleAsync();
        expiry.ShouldBe(_clock.Now.Add(RefreshTokenLifetime.Duration), ExpiryTolerance);
    }

    [Test]
    public async Task ExtendRefreshToken_ConcurrentCallsWithTheSameToken_AllSucceed()
    {
        var token = await _service.CreateRefreshTokenAsync(_userId);
        await using var contextB = CreateContext();
        var serviceB = new RefreshTokenService(contextB, _clock, Substitute.For<ILogger<RefreshTokenService>>());
        await _context.Database.OpenConnectionAsync();
        await contextB.Database.OpenConnectionAsync();

        var results = await Task.WhenAll(
            _service.ExtendRefreshTokenAsync(_userId, token),
            serviceB.ExtendRefreshTokenAsync(_userId, token));

        results.Count(r => r).ShouldBe(ConcurrentRefreshCount);
        (await _context.RefreshToken.CountAsync(rt => rt.AspNetUsersId == _userId)).ShouldBe(1);
    }

    [Test]
    public async Task ExtendRefreshToken_WithUnknownRevokedOrExpiredToken_ReturnsFalse()
    {
        var revoked = await _service.CreateRefreshTokenAsync(_userId);
        await _service.RemoveRefreshTokenAsync(revoked);
        var expired = await _service.CreateRefreshTokenAsync(_userId);
        var hashedExpired = RefreshTokenHasher.Hash(expired);
        await _context.RefreshToken.Where(rt => rt.Token == hashedExpired)
            .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.ExpiryDate, _clock.Now.AddMinutes(-1)));

        (await _service.ExtendRefreshTokenAsync(_userId, "never-issued-token")).ShouldBeFalse();
        (await _service.ExtendRefreshTokenAsync(_userId, revoked)).ShouldBeFalse();
        (await _service.ExtendRefreshTokenAsync(_userId, expired)).ShouldBeFalse();
    }

    [Test]
    public async Task ExtendRefreshToken_OfAnotherUser_ReturnsFalse()
    {
        var token = await _service.CreateRefreshTokenAsync(_userId);

        (await _service.ExtendRefreshTokenAsync(TestUserPrefix + Guid.NewGuid(), token)).ShouldBeFalse();
    }

    [Test]
    public async Task ReplaceRefreshToken_IssuesANewToken_AndKeepsTheLegacyOneOnlyForTheGrace()
    {
        var legacy = await _service.CreateRefreshTokenAsync(_userId);

        var replacement = await _service.ReplaceRefreshTokenAsync(_userId, legacy);

        replacement.ShouldNotBeNull();
        replacement.ShouldNotBe(legacy);
        (await IsValidAsync(replacement!)).ShouldBeTrue();
        (await IsValidAsync(legacy)).ShouldBeTrue();

        _clock.Now = _clock.Now.Add(RefreshTokenLifetime.LegacyTokenGrace).AddSeconds(1);

        (await IsValidAsync(legacy)).ShouldBeFalse();
        (await _service.ReplaceRefreshTokenAsync(_userId, legacy)).ShouldBeNull();
        (await IsValidAsync(replacement!)).ShouldBeTrue();
    }

    [Test]
    public async Task ReplaceRefreshToken_ConcurrentMigrationsWithTheSameLegacyToken_AllSucceed()
    {
        var legacy = await _service.CreateRefreshTokenAsync(_userId);
        await using var contextB = CreateContext();
        var serviceB = new RefreshTokenService(contextB, _clock, Substitute.For<ILogger<RefreshTokenService>>());
        await _context.Database.OpenConnectionAsync();
        await contextB.Database.OpenConnectionAsync();

        var results = await Task.WhenAll(
            _service.ReplaceRefreshTokenAsync(_userId, legacy),
            serviceB.ReplaceRefreshTokenAsync(_userId, legacy));

        results.ShouldAllBe(token => token != null);
        results.Distinct().Count().ShouldBe(ConcurrentRefreshCount);
    }

    [Test]
    public async Task ReplaceRefreshToken_NeverLengthensAnAlmostExpiredLegacyToken()
    {
        var legacy = await _service.CreateRefreshTokenAsync(_userId);
        var hashed = RefreshTokenHasher.Hash(legacy);
        var almostExpired = _clock.Now.AddSeconds(30);
        await _context.RefreshToken.Where(rt => rt.Token == hashed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.ExpiryDate, almostExpired));

        (await _service.ReplaceRefreshTokenAsync(_userId, legacy)).ShouldNotBeNull();

        var expiry = await _context.RefreshToken.AsNoTracking().Where(rt => rt.Token == hashed).Select(rt => rt.ExpiryDate).SingleAsync();
        expiry.ShouldBe(almostExpired, TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task CreateRefreshToken_IsAdditive_KeepsExistingSessions()
    {
        var sessionA = await _service.CreateRefreshTokenAsync(_userId);
        var sessionB = await _service.CreateRefreshTokenAsync(_userId);

        sessionA.ShouldNotBe(sessionB);
        (await IsValidAsync(_userId, sessionA)).ShouldBeTrue();
        (await IsValidAsync(_userId, sessionB)).ShouldBeTrue();
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

        (await IsValidAsync(_userId, tabA)).ShouldBeFalse();
        (await IsValidAsync(_userId, tabB)).ShouldBeFalse();
        (await IsValidAsync(otherUserId, otherUserToken)).ShouldBeTrue();
    }

    [Test]
    public async Task RemoveRefreshTokenAsync_RemovesOnlyThePresentedToken()
    {
        var tabA = await _service.CreateRefreshTokenAsync(_userId);
        var tabB = await _service.CreateRefreshTokenAsync(_userId);

        await _service.RemoveRefreshTokenAsync(tabA);

        (await IsValidAsync(_userId, tabA)).ShouldBeFalse();
        (await IsValidAsync(_userId, tabB)).ShouldBeTrue();
    }

    private async Task<bool> IsValidAsync(string refreshToken) => await IsValidAsync(_userId, refreshToken);

    private async Task<bool> IsValidAsync(string userId, string refreshToken)
    {
        var hashed = RefreshTokenHasher.Hash(refreshToken);
        var now = _clock.Now;
        return await _context.RefreshToken.AsNoTracking()
            .AnyAsync(rt => rt.AspNetUsersId == userId && rt.Token == hashed && rt.ExpiryDate > now);
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
