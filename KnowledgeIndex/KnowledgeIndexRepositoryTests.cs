// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.KnowledgeIndex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Npgsql;
using NUnit.Framework;
using Klacks.Api.Infrastructure.Persistence;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Category("RealDatabase")]
public class KnowledgeIndexRepositoryTests
{
    private const string ConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    private const string TestPrefix = "INTEGRATION_TEST_KNOWLEDGEINDEX_";

    private KnowledgeIndexRepository _repo = null!;
    private NpgsqlConnection _connection = null!;

    private static string Prefixed(string sourceId) => TestPrefix + sourceId;

    [SetUp]
    public async Task Setup()
    {
        _connection = new NpgsqlConnection(ConnectionString);
        await _connection.OpenAsync();

        await CleanupAsync();

        _repo = new KnowledgeIndexRepository(_connection);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _connection.DisposeAsync();
    }

    private async Task CleanupAsync()
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM knowledge_index WHERE source_id LIKE @prefix;";
        cmd.Parameters.AddWithValue("prefix", TestPrefix + "%");
        await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task UpsertThenFindNearest_ReturnsInsertedEntryByEmbedding()
    {
        var embedding = Enumerable.Range(0, KnowledgeIndexConstants.EmbeddingDimension).Select(i => i % 2 == 0 ? 1.0f : 0.0f).ToArray();
        var norm = Math.Sqrt(embedding.Sum(x => (double)x * x));
        embedding = embedding.Select(x => (float)(x / norm)).ToArray();

        var sourceId = Prefixed("ListOpenShifts");
        var entry = new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            Kind = KnowledgeEntryKind.Skill,
            SourceId = sourceId,
            Text = "ListOpenShifts. Returns open shifts.",
            TextHash = new byte[] { 1, 2, 3 },
            Embedding = embedding,
            UpdatedAt = DateTime.UtcNow
        };

        await _repo.UpsertAsync([entry], CancellationToken.None);

        var result = await _repo.FindNearestAsync(
            embedding,
            userPermissions: [],
            adminBypass: false,
            topN: 5,
            CancellationToken.None);

        result.ShouldContain(r => r.SourceId == sourceId);
    }

    [Test]
    public async Task FindNearestAsync_RespectsPermissionFilter()
    {
        var embedding = Enumerable.Range(0, KnowledgeIndexConstants.EmbeddingDimension).Select(_ => 1.0f / (float)Math.Sqrt(KnowledgeIndexConstants.EmbeddingDimension)).ToArray();

        var restrictedSourceId = Prefixed("RestrictedSkill");
        var publicSourceId = Prefixed("PublicSkill");

        var restrictedEntry = new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            Kind = KnowledgeEntryKind.Skill,
            SourceId = restrictedSourceId,
            Text = "Requires permission.",
            TextHash = [1],
            Embedding = embedding,
            RequiredPermission = "shifts.read",
            UpdatedAt = DateTime.UtcNow
        };

        var publicEntry = new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            Kind = KnowledgeEntryKind.Skill,
            SourceId = publicSourceId,
            Text = "No permission required.",
            TextHash = [2],
            Embedding = embedding,
            RequiredPermission = null,
            UpdatedAt = DateTime.UtcNow
        };

        await _repo.UpsertAsync([restrictedEntry, publicEntry], CancellationToken.None);

        var result = await _repo.FindNearestAsync(
            embedding,
            userPermissions: [],
            adminBypass: false,
            topN: 10,
            CancellationToken.None);

        result.ShouldContain(r => r.SourceId == publicSourceId);
        result.ShouldNotContain(r => r.SourceId == restrictedSourceId);
    }

    [Test]
    public async Task FindNearestAsync_AdminBypassReturnsAllEntries()
    {
        var embedding = Enumerable.Range(0, KnowledgeIndexConstants.EmbeddingDimension).Select(_ => 1.0f / (float)Math.Sqrt(KnowledgeIndexConstants.EmbeddingDimension)).ToArray();

        var sourceIdA = Prefixed("A");
        var sourceIdB = Prefixed("B");

        await _repo.UpsertAsync(
        [
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = sourceIdA, Text = "A", TextHash = [1], Embedding = embedding, RequiredPermission = "admin.only", UpdatedAt = DateTime.UtcNow },
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = sourceIdB, Text = "B", TextHash = [2], Embedding = embedding, RequiredPermission = null, UpdatedAt = DateTime.UtcNow }
        ], CancellationToken.None);

        var result = await _repo.FindNearestAsync(embedding, [], adminBypass: true, topN: 10, CancellationToken.None);

        var testResults = result.Where(r => r.SourceId.StartsWith(TestPrefix)).ToList();
        testResults.Count.ShouldBe(2);
        testResults.ShouldContain(r => r.SourceId == sourceIdA);
        testResults.ShouldContain(r => r.SourceId == sourceIdB);
    }

    [Test]
    public async Task GetAllHashesAsync_ReturnsInsertedHashes()
    {
        var embedding = Enumerable.Range(0, KnowledgeIndexConstants.EmbeddingDimension).Select(_ => 1.0f / (float)Math.Sqrt(KnowledgeIndexConstants.EmbeddingDimension)).ToArray();
        var hash = new byte[] { 9, 8, 7 };
        var sourceId = Prefixed("HashSkill");

        await _repo.UpsertAsync(
        [
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = sourceId, Text = "Txt", TextHash = hash, Embedding = embedding, UpdatedAt = DateTime.UtcNow }
        ], CancellationToken.None);

        var hashes = await _repo.GetAllHashesAsync(CancellationToken.None);

        hashes.ContainsKey((KnowledgeEntryKind.Skill, sourceId)).ShouldBeTrue();
        hashes[(KnowledgeEntryKind.Skill, sourceId)].ShouldBeEquivalentTo(hash);
    }

    [Test]
    public async Task DeleteAsync_RemovesSpecifiedEntries()
    {
        var embedding = Enumerable.Range(0, KnowledgeIndexConstants.EmbeddingDimension).Select(_ => 1.0f / (float)Math.Sqrt(KnowledgeIndexConstants.EmbeddingDimension)).ToArray();

        var toDeleteSourceId = Prefixed("ToDelete");
        var toKeepSourceId = Prefixed("ToKeep");

        await _repo.UpsertAsync(
        [
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = toDeleteSourceId, Text = "x", TextHash = [1], Embedding = embedding, UpdatedAt = DateTime.UtcNow },
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = toKeepSourceId, Text = "y", TextHash = [2], Embedding = embedding, UpdatedAt = DateTime.UtcNow }
        ], CancellationToken.None);

        await _repo.DeleteAsync([(KnowledgeEntryKind.Skill, toDeleteSourceId)], CancellationToken.None);

        var hashes = await _repo.GetAllHashesAsync(CancellationToken.None);
        hashes.Keys.ShouldNotContain((KnowledgeEntryKind.Skill, toDeleteSourceId));
        hashes.Keys.ShouldContain((KnowledgeEntryKind.Skill, toKeepSourceId));
    }
}
