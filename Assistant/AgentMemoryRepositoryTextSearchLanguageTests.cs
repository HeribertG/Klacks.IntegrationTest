// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for AgentMemoryRepository's full text search against the real database, proving
/// that the search matches memories written in non-German languages (French, Polish). Before this fix
/// the PostgreSQL text search configuration was hardcoded to "german", so the German stemmer mis-parsed
/// other languages and lost matches; this now runs with a language-neutral configuration.
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
public class AgentMemoryRepositoryTextSearchLanguageTests
{
    private const string TestPrefix = "INTEGRATION_TEST_MEMORYFTS_";

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AgentMemoryRepository _repository = null!;
    private Guid _agentId;
    private Guid _frenchMemoryId;
    private Guid _polishMemoryId;

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
    public async Task SetUp()
    {
        _context = NewContext();
        _repository = new AgentMemoryRepository(_context, NullLogger<AgentMemoryRepository>.Instance);

        _frenchMemoryId = Guid.NewGuid();
        _polishMemoryId = Guid.NewGuid();

        await _repository.AddAsync(new AgentMemory
        {
            Id = _frenchMemoryId,
            AgentId = _agentId,
            Key = TestPrefix + "fr_chaussures",
            Content = "Les chaussures de securite sont obligatoires dans cet entrepot.",
            Category = "learned_fact",
            Importance = 3
        });

        await _repository.AddAsync(new AgentMemory
        {
            Id = _polishMemoryId,
            AgentId = _agentId,
            Key = TestPrefix + "pl_zamowienie",
            Content = "Zamowienie klienta zostalo zrealizowane w calosci w tym tygodniu.",
            Category = "learned_fact",
            Importance = 3
        });
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    private async Task CleanupAsync(DataBaseContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_memories WHERE key LIKE {0}", TestPrefix + "%");
    }

    [Test]
    public async Task HybridSearchAsync_TextOnlyFallback_MatchesFrenchContent()
    {
        var results = await _repository.HybridSearchAsync(
            _agentId, "chaussures securite entrepot", queryEmbedding: null, limit: 10);

        results.ShouldContain(r => r.Id == _frenchMemoryId);
    }

    [Test]
    public async Task HybridSearchAsync_TextOnlyFallback_MatchesPolishContent()
    {
        var results = await _repository.HybridSearchAsync(
            _agentId, "zamowienie klienta tydzien", queryEmbedding: null, limit: 10);

        results.ShouldContain(r => r.Id == _polishMemoryId);
    }
}
