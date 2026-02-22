using FluentAssertions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class AgentMemoryHybridSearchIntegrationTests
{
    private DataBaseContext _context = null!;
    private AgentMemoryRepository _repository = null!;
    private string _connectionString = null!;
    private Guid _testAgentId;
    private const string TestPrefix = "HYBRID_SEARCH_TEST_";
    private const int EmbeddingDimensions = 1536;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        using var context = new DataBaseContext(options, mockHttpContextAccessor);
        await CleanupTestDataWithContext(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        var logger = Substitute.For<ILogger<AgentMemoryRepository>>();
        _repository = new AgentMemoryRepository(_context, logger);

        _testAgentId = Guid.NewGuid();
        await CreateTestAgentAsync(_testAgentId);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataWithContext(_context);
        _context?.Dispose();
    }

    [Test]
    public async Task HybridSearch_WithVectorEmbedding_ReturnsRankedResults()
    {
        // Arrange
        var queryEmbedding = GenerateEmbedding(seed: 1);
        var similarEmbedding = GenerateSimilarEmbedding(queryEmbedding, similarity: 0.95f);
        var differentEmbedding = GenerateEmbedding(seed: 999);

        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}similar", "Urlaub in den Alpen", similarEmbedding, importance: 5);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}different", "Datenbank Performance Tuning", differentEmbedding, importance: 5);

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Urlaub", queryEmbedding, limit: 10);

        // Assert
        results.Should().NotBeEmpty();
        var similarResult = results.FirstOrDefault(r => r.Key.Contains("similar"));
        var differentResult = results.FirstOrDefault(r => r.Key.Contains("different"));

        similarResult.Should().NotBeNull("similar memory should be found");

        if (similarResult != null && differentResult != null)
        {
            similarResult.Score.Should().BeGreaterThan(differentResult.Score,
                "semantically similar memory should rank higher");
        }
    }

    [Test]
    public async Task HybridSearch_WithoutEmbedding_FallsBackToTextSearch()
    {
        // Arrange
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}text_match", "PostgreSQL Datenbank Optimierung", embedding: null, importance: 7);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}no_match", "Angular Frontend Komponenten", embedding: null, importance: 5);

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Datenbank", queryEmbedding: null, limit: 10);

        // Assert
        results.Should().NotBeEmpty("text search should find matches");
        results.First().Key.Should().Contain("text_match");
    }

    [Test]
    public async Task HybridSearch_ExcludesPinnedMemories()
    {
        // Arrange
        var embedding = GenerateEmbedding(seed: 10);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}pinned", "Wichtige gepinnte Info", embedding, importance: 10, isPinned: true);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}normal", "Normale Info zum Suchen", embedding, importance: 5, isPinned: false);

        // Act
        var searchResults = await _repository.HybridSearchAsync(_testAgentId, "Info", embedding, limit: 10);

        // Assert
        searchResults.Should().NotContain(r => r.Key.Contains("pinned"),
            "pinned memories should be excluded from hybrid search (loaded separately via GetPinnedAsync)");
        searchResults.Should().Contain(r => r.Key.Contains("normal"));
    }

    [Test]
    public async Task HybridSearch_ExcludesExpiredMemories()
    {
        // Arrange
        var embedding = GenerateEmbedding(seed: 20);
        var pastDate = DateTime.UtcNow.AddDays(-1);
        var futureDate = DateTime.UtcNow.AddDays(30);

        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}expired", "Abgelaufener Termin", embedding, importance: 8, expiresAt: pastDate);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}valid", "Kommender Termin", embedding, importance: 5, expiresAt: futureDate);

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Termin", embedding, limit: 10);

        // Assert
        results.Should().NotContain(r => r.Key.Contains("expired"),
            "expired memories should be excluded");
        results.Should().Contain(r => r.Key.Contains("valid"),
            "non-expired memories should be included");
    }

    [Test]
    public async Task HybridSearch_ImportanceAffectsRanking()
    {
        // Arrange
        var embedding = GenerateEmbedding(seed: 30);

        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}low_imp", "C# Projekt Struktur", embedding, importance: 1);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}high_imp", "C# Projekt Architektur", embedding, importance: 10);

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Projekt", embedding, limit: 10);

        // Assert
        results.Should().HaveCountGreaterThanOrEqualTo(2);
        var highImp = results.First(r => r.Key.Contains("high_imp"));
        var lowImp = results.First(r => r.Key.Contains("low_imp"));

        highImp.Score.Should().BeGreaterThan(lowImp.Score,
            "higher importance should contribute to higher score");
    }

    [Test]
    public async Task HybridSearch_HighImportanceMemories_IncludedEvenWithLowVectorScore()
    {
        // Arrange
        var queryEmbedding = GenerateEmbedding(seed: 40);
        var veryDifferentEmbedding = GenerateEmbedding(seed: 9999);

        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}critical", "Kritische Entscheidung Architektur", veryDifferentEmbedding, importance: 9);

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Kritische Entscheidung", queryEmbedding, limit: 10);

        // Assert
        results.Should().Contain(r => r.Key.Contains("critical"),
            "high importance (>=7) memories should be included even with low vector score");
    }

    [Test]
    public async Task HybridSearch_LimitParameter_RespectsLimit()
    {
        // Arrange
        var embedding = GenerateEmbedding(seed: 50);
        for (var i = 0; i < 5; i++)
        {
            await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}bulk_{i}", $"Bulk Eintrag Nummer {i}", embedding, importance: 5);
        }

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Eintrag", embedding, limit: 3);

        // Assert
        results.Should().HaveCountLessThanOrEqualTo(3);
    }

    [Test]
    public async Task HybridSearch_ReturnsOnlyForSpecificAgent()
    {
        // Arrange
        var otherAgentId = Guid.NewGuid();
        await CreateTestAgentAsync(otherAgentId);

        var embedding = GenerateEmbedding(seed: 60);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}my_agent", "Mein Agent Eintrag", embedding, importance: 5);
        await InsertMemoryWithEmbeddingAsync(otherAgentId, $"{TestPrefix}other_agent", "Anderer Agent Eintrag", embedding, importance: 5);

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Agent", embedding, limit: 10);

        // Assert
        results.Should().Contain(r => r.Key.Contains("my_agent"));
        results.Should().NotContain(r => r.Key.Contains("other_agent"),
            "search should only return memories for the specified agent");
    }

    [Test]
    public async Task GetPinned_ReturnsOnlyPinnedAndNotExpired()
    {
        // Arrange
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}pinned1", "Gepinnt aktiv", null, importance: 8, isPinned: true);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}pinned_expired", "Gepinnt abgelaufen", null, importance: 8, isPinned: true, expiresAt: DateTime.UtcNow.AddDays(-1));
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}not_pinned", "Nicht gepinnt", null, importance: 5, isPinned: false);

        // Act
        var pinned = await _repository.GetPinnedAsync(_testAgentId);

        // Assert
        pinned.Should().Contain(m => m.Key.Contains("pinned1"));
        pinned.Should().NotContain(m => m.Key.Contains("pinned_expired"),
            "expired pinned memories should be excluded");
        pinned.Should().NotContain(m => m.Key.Contains("not_pinned"),
            "non-pinned memories should be excluded");
    }

    [Test]
    public async Task CleanupExpired_SoftDeletesExpiredMemories()
    {
        // Arrange
        var expiredTime = DateTime.UtcNow.AddHours(-2);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}old_expired", "Soll gelöscht werden", null, importance: 3, expiresAt: expiredTime);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}still_valid", "Soll bleiben", null, importance: 5);

        // Act
        await _repository.CleanupExpiredAsync();

        // Assert
        var likePattern = $"{TestPrefix}old_expired%";
        var allIncludingDeleted = await _context.Database
            .SqlQuery<CountResult>($"""
                SELECT COUNT(*)::int AS count FROM agent_memories
                WHERE agent_id = {_testAgentId} AND key LIKE {likePattern} AND is_deleted = true
                """)
            .ToListAsync();

        allIncludingDeleted.First().Count.Should().Be(1, "expired memory should be soft-deleted");

        var validMemories = await _repository.GetAllAsync(_testAgentId);
        validMemories.Should().Contain(m => m.Key.Contains("still_valid"),
            "non-expired memory should remain");
    }

    [Test]
    public async Task UpdateAccessCounts_IncrementsCountAndSetsTimestamp()
    {
        // Arrange
        var memoryId = Guid.NewGuid();
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}access_test", "Access Count Test", null, importance: 5, memoryId: memoryId);

        // Act
        await _repository.UpdateAccessCountsAsync([memoryId]);

        // Assert
        var memory = await _repository.GetByIdAsync(memoryId);
        memory.Should().NotBeNull();
        memory!.AccessCount.Should().Be(1);
        memory.LastAccessedAt.Should().NotBeNull();
        memory.LastAccessedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task GetPendingEmbeddings_ReturnsMemoriesWithoutEmbedding()
    {
        // Arrange
        var embedding = GenerateEmbedding(seed: 70);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}has_embedding", "Mit Embedding", embedding, importance: 5);
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}no_embedding", "Ohne Embedding", null, importance: 5);

        // Act
        var pending = await _repository.GetPendingEmbeddingsAsync();

        // Assert
        pending.Should().Contain(m => m.Key.Contains("no_embedding"),
            "memories without embedding should be returned");
        pending.Should().NotContain(m => m.Key.Contains("has_embedding"),
            "memories with embedding should not be returned");
    }

    [Test]
    public async Task HybridSearch_GermanFullTextSearch_HandlesUmlauts()
    {
        // Arrange
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}umlaut", "Mitarbeiterübersicht der Geschäftsführung", null, importance: 7);

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Mitarbeiter", queryEmbedding: null, limit: 10);

        // Assert
        results.Should().NotBeEmpty("German FTS should match compound words with umlauts");
    }

    [Test]
    public async Task HybridSearch_TextAndVectorCombined_ProducesValidScores()
    {
        // Arrange
        var queryEmbedding = GenerateEmbedding(seed: 80);
        var similarEmbedding = GenerateSimilarEmbedding(queryEmbedding, similarity: 0.9f);

        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}combined", "Projektplanung Sprint Review", similarEmbedding, importance: 7);

        // Act
        var results = await _repository.HybridSearchAsync(_testAgentId, "Sprint", queryEmbedding, limit: 10);

        // Assert
        results.Should().NotBeEmpty();
        var result = results.First(r => r.Key.Contains("combined"));
        result.Score.Should().BeGreaterThan(0, "combined score should be positive");
        result.Score.Should().BeLessThanOrEqualTo(1.0f, "score should not exceed 1.0");
    }

    [Test]
    public async Task AddAndSearch_FullWorkflow_RoundTrip()
    {
        // Arrange
        var memory = new AgentMemory
        {
            Id = Guid.NewGuid(),
            AgentId = _testAgentId,
            Key = $"{TestPrefix}roundtrip",
            Content = "Der Mitarbeiter bevorzugt TypeScript gegenüber JavaScript",
            Category = "preference",
            Importance = 6,
            Source = "test"
        };

        // Act
        await _repository.AddAsync(memory);
        var searchResults = await _repository.HybridSearchAsync(_testAgentId, "TypeScript", queryEmbedding: null, limit: 10);

        // Assert
        searchResults.Should().Contain(r => r.Key.Contains("roundtrip"),
            "newly added memory should be findable via text search");
    }

    [Test]
    public async Task SearchAsync_SimpleTextSearch_FindsByKeyAndContent()
    {
        // Arrange
        var memory = new AgentMemory
        {
            Id = Guid.NewGuid(),
            AgentId = _testAgentId,
            Key = $"{TestPrefix}simple_search",
            Content = "Angular Signals sind reaktiv",
            Category = "fact",
            Importance = 5,
            Source = "test"
        };
        await _repository.AddAsync(memory);

        // Act
        var results = await _repository.SearchAsync(_testAgentId, "Angular");

        // Assert
        results.Should().Contain(m => m.Key.Contains("simple_search"));
    }

    [Test]
    public async Task GetByCategoryAsync_FiltersCorrectly()
    {
        // Arrange
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}cat_fact", "Ein Fakt", null, importance: 5, category: "fact");
        await InsertMemoryWithEmbeddingAsync(_testAgentId, $"{TestPrefix}cat_pref", "Eine Präferenz", null, importance: 5, category: "preference");

        // Act
        var facts = await _repository.GetByCategoryAsync(_testAgentId, "fact");

        // Assert
        facts.Should().Contain(m => m.Key.Contains("cat_fact"));
        facts.Should().NotContain(m => m.Key.Contains("cat_pref"));
    }

    #region Helper Methods

    private async Task CreateTestAgentAsync(Guid agentId)
    {
        await _context.Database.ExecuteSqlRawAsync($@"
            INSERT INTO agents (id, name, display_name, is_active, is_default, create_time, is_deleted)
            VALUES ('{agentId}', '{TestPrefix}Agent', '{TestPrefix}Test Agent', true, false, NOW(), false)
            ON CONFLICT (id) DO NOTHING");
    }

    private async Task InsertMemoryWithEmbeddingAsync(
        Guid agentId, string key, string content, float[]? embedding,
        int importance, bool isPinned = false, DateTime? expiresAt = null,
        Guid? memoryId = null, string category = "fact")
    {
        var id = memoryId ?? Guid.NewGuid();
        var embeddingClause = embedding != null
            ? $"'{FormatEmbeddingForSql(embedding)}'::vector"
            : "NULL";
        var expiresClause = expiresAt.HasValue
            ? $"'{expiresAt.Value:yyyy-MM-dd HH:mm:ss}'::timestamptz"
            : "NULL";

        var sql = $@"
            INSERT INTO agent_memories (id, agent_id, category, key, content, importance, embedding,
                is_pinned, expires_at, access_count, source, metadata, create_time, is_deleted)
            VALUES ('{id}', '{agentId}', '{category}', '{key.Replace("'", "''")}', '{content.Replace("'", "''")}',
                {importance}, {embeddingClause}, {isPinned.ToString().ToLower()}, {expiresClause},
                0, 'test', '{{{{}}}}', NOW(), false)";

        await _context.Database.ExecuteSqlRawAsync(sql);
    }

    private static string FormatEmbeddingForSql(float[] embedding)
    {
        return $"[{string.Join(",", embedding.Select(f => f.ToString("G", System.Globalization.CultureInfo.InvariantCulture)))}]";
    }

    private static float[] GenerateEmbedding(int seed)
    {
        var rng = new Random(seed);
        var vec = new float[EmbeddingDimensions];
        for (var i = 0; i < EmbeddingDimensions; i++)
            vec[i] = (float)(rng.NextDouble() * 2 - 1);

        var norm = (float)Math.Sqrt(vec.Sum(v => (double)(v * v)));
        for (var i = 0; i < EmbeddingDimensions; i++)
            vec[i] /= norm;

        return vec;
    }

    private static float[] GenerateSimilarEmbedding(float[] reference, float similarity)
    {
        var rng = new Random(42);
        var vec = new float[reference.Length];
        for (var i = 0; i < reference.Length; i++)
            vec[i] = reference[i] * similarity + (float)(rng.NextDouble() * 2 - 1) * (1 - similarity);

        var norm = (float)Math.Sqrt(vec.Sum(v => (double)(v * v)));
        for (var i = 0; i < reference.Length; i++)
            vec[i] /= norm;

        return vec;
    }

    private static async Task CleanupTestDataWithContext(DataBaseContext context)
    {
        await context.Database.ExecuteSqlRawAsync($@"
            DELETE FROM agent_memory_tags WHERE memory_id IN (
                SELECT id FROM agent_memories WHERE key LIKE '{TestPrefix}%'
            );
            DELETE FROM agent_memories WHERE key LIKE '{TestPrefix}%';
            DELETE FROM agents WHERE name LIKE '{TestPrefix}%';");
    }

    private class CountResult
    {
        public int Count { get; set; }
    }

    #endregion
}
