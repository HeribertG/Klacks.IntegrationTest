// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The periodic memory cleanup (AdjustImportanceByUsageAsync and CleanupLowValueMemoriesAsync, run by
/// MemoryCleanupBackgroundService) against real PostgreSQL: an old, never-read explanation-depth override keeps its
/// importance and is never cleaned up as low value, while an otherwise identical memory with another key decays and is
/// deleted. Both statements are global UPDATEs, so every test runs inside a transaction that is rolled back.
/// </summary>

using Klacks.Api.Domain.Constants;
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
public class MemoryCleanupExplainDepthOverrideNpgsqlTests
{
    private const string ControlKey = "INTEGRATION_TEST_MEMORY_CLEANUP_control";
    private const int AgeDays = 200;
    private const int DecayableImportance = 8;
    private const int LowValueImportance = 2;

    private string _connectionString = null!;
    private Guid _agentId;

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
    }

    private async Task<Guid> InsertOldAsync(DataBaseContext ctx, string key, string content, int importance)
    {
        var id = Guid.NewGuid();
        ctx.AgentMemories.Add(new AgentMemory
        {
            Id = id,
            AgentId = _agentId,
            UserId = Guid.NewGuid(),
            Key = key,
            Category = MemoryCategories.UserPreference,
            Content = content,
            Importance = importance
        });
        await ctx.SaveChangesAsync();

        var old = DateTime.UtcNow.AddDays(-AgeDays);
        await ctx.AgentMemories.IgnoreQueryFilters().Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.CreateTime, old)
            .SetProperty(m => m.UpdateTime, old)
            .SetProperty(m => m.LastAccessedAt, (DateTime?)null)
            .SetProperty(m => m.AccessCount, 0));
        return id;
    }

    private static Task<AgentMemory> ReadAsync(DataBaseContext ctx, Guid id) =>
        ctx.AgentMemories.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == id);

    [Test]
    public async Task ImportanceDecay_SkipsTheOverride_ButDecaysAnOtherwiseIdenticalMemory()
    {
        await using var ctx = NewContext();
        await using var transaction = await ctx.Database.BeginTransactionAsync();
        var overrideId = await InsertOldAsync(
            ctx, ExplainDepthDefaults.OverrideMemoryKey, ExplainDepthDefaults.OverrideValueAdvanced, DecayableImportance);
        var controlId = await InsertOldAsync(ctx, ControlKey, ExplainDepthDefaults.OverrideValueAdvanced, DecayableImportance);

        await new AgentMemoryRepository(ctx, NullLogger<AgentMemoryRepository>.Instance).AdjustImportanceByUsageAsync();

        (await ReadAsync(ctx, overrideId)).Importance.ShouldBe(DecayableImportance);
        (await ReadAsync(ctx, controlId)).Importance.ShouldBe(DecayableImportance - 1);
        await transaction.RollbackAsync();
    }

    [Test]
    public async Task LowValueCleanup_KeepsTheOverride_ButDeletesAnOtherwiseIdenticalMemory()
    {
        await using var ctx = NewContext();
        await using var transaction = await ctx.Database.BeginTransactionAsync();
        var overrideId = await InsertOldAsync(
            ctx, ExplainDepthDefaults.OverrideMemoryKey, ExplainDepthDefaults.OverrideValueBeginner, LowValueImportance);
        var controlId = await InsertOldAsync(ctx, ControlKey, ExplainDepthDefaults.OverrideValueBeginner, LowValueImportance);

        await new AgentMemoryRepository(ctx, NullLogger<AgentMemoryRepository>.Instance).CleanupLowValueMemoriesAsync();

        (await ReadAsync(ctx, overrideId)).IsDeleted.ShouldBeFalse();
        (await ReadAsync(ctx, controlId)).IsDeleted.ShouldBeTrue();
        await transaction.RollbackAsync();
    }
}
