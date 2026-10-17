// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// AgentMemoryRepository.GetByUserAndKeyAsync against real PostgreSQL: returns the newest live memory of exactly
/// that user and key; shared memories, other users' memories, soft-deleted and expired rows never match.
/// SoftDeleteByUserAndKeyAsync retires exactly that user's rows of that key, except the one it is told to keep.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class AgentMemoryRepositoryUserKeyTests
{
    private const string TestKey = "INTEGRATION_TEST_EXPLAIN_DEPTH_override";
    private const string Category = "user_state";

    private string _connectionString = null!;
    private Guid _agentId;
    private Guid _userId;

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        await using var ctx = NewContext();
        _agentId = await ctx.Agents.IgnoreQueryFilters().Select(a => a.Id).FirstOrDefaultAsync();
        if (_agentId == Guid.Empty)
        {
            Assert.Ignore("No agent seeded in the test database.");
        }

        await CleanupAsync(ctx);
    }

    [SetUp]
    public void SetUp() => _userId = Guid.NewGuid();

    [TearDown]
    public async Task TearDown()
    {
        await using var ctx = NewContext();
        await CleanupAsync(ctx);
    }

    private static async Task CleanupAsync(DataBaseContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM agent_memories WHERE key = {0}", TestKey);
    }

    private async Task<Guid> InsertAsync(Guid? userId, string content, DateTime createTime, bool deleted = false, DateTime? expiresAt = null)
    {
        var id = Guid.NewGuid();
        await using var ctx = NewContext();
        ctx.AgentMemories.Add(new AgentMemory
        {
            Id = id,
            AgentId = _agentId,
            UserId = userId,
            Key = TestKey,
            Category = Category,
            Content = content,
            Importance = 8,
            ExpiresAt = expiresAt
        });
        await ctx.SaveChangesAsync();
        await ctx.AgentMemories.IgnoreQueryFilters().Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.CreateTime, createTime)
            .SetProperty(m => m.IsDeleted, deleted));
        return id;
    }

    private async Task<AgentMemory?> ReadAsync(Guid userId)
    {
        await using var ctx = NewContext();
        return await new AgentMemoryRepository(ctx, NullLogger<AgentMemoryRepository>.Instance)
            .GetByUserAndKeyAsync(userId, TestKey);
    }

    [Test]
    public async Task ReturnsTheNewestLiveMemoryOfThatUser()
    {
        var now = DateTime.UtcNow;
        await InsertAsync(_userId, "beginner", now.AddDays(-10));
        var newest = await InsertAsync(_userId, "advanced", now.AddDays(-1));

        var memory = await ReadAsync(_userId);

        memory.ShouldNotBeNull();
        memory.Id.ShouldBe(newest);
        memory.Content.ShouldBe("advanced");
    }

    [Test]
    public async Task IgnoresSharedAndOtherUsersMemories()
    {
        var now = DateTime.UtcNow;
        await InsertAsync(null, "advanced", now);
        await InsertAsync(Guid.NewGuid(), "advanced", now);

        (await ReadAsync(_userId)).ShouldBeNull();
    }

    [Test]
    public async Task IgnoresSoftDeletedAndExpiredMemories()
    {
        var now = DateTime.UtcNow;
        var live = await InsertAsync(_userId, "beginner", now.AddDays(-5));
        await InsertAsync(_userId, "advanced", now.AddDays(-1), deleted: true);
        await InsertAsync(_userId, "advanced", now.AddDays(-2), expiresAt: now.AddMinutes(-1));

        var memory = await ReadAsync(_userId);

        memory.ShouldNotBeNull();
        memory.Id.ShouldBe(live);
    }

    private async Task<bool> IsDeletedAsync(Guid id)
    {
        await using var ctx = NewContext();
        return await ctx.AgentMemories.IgnoreQueryFilters().Where(m => m.Id == id).Select(m => m.IsDeleted).SingleAsync();
    }

    [Test]
    public async Task SoftDeleteByUserAndKey_RetiresThatUsersRows_ExceptTheKeptOne()
    {
        var now = DateTime.UtcNow;
        var older = await InsertAsync(_userId, "beginner", now.AddDays(-3));
        var kept = await InsertAsync(_userId, "advanced", now);
        var foreign = await InsertAsync(Guid.NewGuid(), "advanced", now);
        var shared = await InsertAsync(null, "advanced", now);

        int deleted;
        await using (var ctx = NewContext())
        {
            deleted = await new AgentMemoryRepository(ctx, NullLogger<AgentMemoryRepository>.Instance)
                .SoftDeleteByUserAndKeyAsync(_userId, TestKey, kept);
        }

        deleted.ShouldBe(1);
        (await IsDeletedAsync(older)).ShouldBeTrue();
        (await IsDeletedAsync(kept)).ShouldBeFalse();
        (await IsDeletedAsync(foreign)).ShouldBeFalse();
        (await IsDeletedAsync(shared)).ShouldBeFalse();
    }

    [Test]
    public async Task SoftDeleteByUserAndKey_WithoutAKeptRow_RetiresAllOfThem()
    {
        var now = DateTime.UtcNow;
        var first = await InsertAsync(_userId, "beginner", now.AddDays(-3));
        var second = await InsertAsync(_userId, "advanced", now);

        await using (var ctx = NewContext())
        {
            await new AgentMemoryRepository(ctx, NullLogger<AgentMemoryRepository>.Instance)
                .SoftDeleteByUserAndKeyAsync(_userId, TestKey);
        }

        (await IsDeletedAsync(first)).ShouldBeTrue();
        (await IsDeletedAsync(second)).ShouldBeTrue();
        (await ReadAsync(_userId)).ShouldBeNull();
    }
}
