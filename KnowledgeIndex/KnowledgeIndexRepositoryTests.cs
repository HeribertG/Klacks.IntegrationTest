// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
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
    private KnowledgeIndexRepository _repo = null!;
    private NpgsqlConnection _connection = null!;

    [SetUp]
    public async Task Setup()
    {
        _connection = new NpgsqlConnection(ConnectionString);
        await _connection.OpenAsync();

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE knowledge_index;";
        await cmd.ExecuteNonQueryAsync();

        _repo = new KnowledgeIndexRepository(_connection);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task UpsertThenFindNearest_ReturnsInsertedEntryByEmbedding()
    {
        var embedding = Enumerable.Range(0, 384).Select(i => i % 2 == 0 ? 1.0f : 0.0f).ToArray();
        var norm = Math.Sqrt(embedding.Sum(x => (double)x * x));
        embedding = embedding.Select(x => (float)(x / norm)).ToArray();

        var entry = new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            Kind = KnowledgeEntryKind.Skill,
            SourceId = "ListOpenShifts",
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

        result.ShouldHaveSingleItem().SourceId.ShouldBe("ListOpenShifts");
    }

    [Test]
    public async Task FindNearestAsync_RespectsPermissionFilter()
    {
        var embedding = Enumerable.Range(0, 384).Select(_ => 1.0f / (float)Math.Sqrt(384)).ToArray();

        var restrictedEntry = new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            Kind = KnowledgeEntryKind.Skill,
            SourceId = "RestrictedSkill",
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
            SourceId = "PublicSkill",
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

        result.ShouldHaveSingleItem().SourceId.ShouldBe("PublicSkill");
    }

    [Test]
    public async Task FindNearestAsync_AdminBypassReturnsAllEntries()
    {
        var embedding = Enumerable.Range(0, 384).Select(_ => 1.0f / (float)Math.Sqrt(384)).ToArray();

        await _repo.UpsertAsync(
        [
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = "A", Text = "A", TextHash = [1], Embedding = embedding, RequiredPermission = "admin.only", UpdatedAt = DateTime.UtcNow },
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = "B", Text = "B", TextHash = [2], Embedding = embedding, RequiredPermission = null, UpdatedAt = DateTime.UtcNow }
        ], CancellationToken.None);

        var result = await _repo.FindNearestAsync(embedding, [], adminBypass: true, topN: 10, CancellationToken.None);

        result.Count.ShouldBe(2);
    }

    [Test]
    public async Task GetAllHashesAsync_ReturnsInsertedHashes()
    {
        var embedding = Enumerable.Range(0, 384).Select(_ => 1.0f / (float)Math.Sqrt(384)).ToArray();
        var hash = new byte[] { 9, 8, 7 };

        await _repo.UpsertAsync(
        [
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = "HashSkill", Text = "Txt", TextHash = hash, Embedding = embedding, UpdatedAt = DateTime.UtcNow }
        ], CancellationToken.None);

        var hashes = await _repo.GetAllHashesAsync(CancellationToken.None);

        hashes.ContainsKey((KnowledgeEntryKind.Skill, "HashSkill")).ShouldBeTrue();
        hashes[(KnowledgeEntryKind.Skill, "HashSkill")].ShouldBeEquivalentTo(hash);
    }

    [Test]
    public async Task DeleteAsync_RemovesSpecifiedEntries()
    {
        var embedding = Enumerable.Range(0, 384).Select(_ => 1.0f / (float)Math.Sqrt(384)).ToArray();

        await _repo.UpsertAsync(
        [
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = "ToDelete", Text = "x", TextHash = [1], Embedding = embedding, UpdatedAt = DateTime.UtcNow },
            new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = "ToKeep", Text = "y", TextHash = [2], Embedding = embedding, UpdatedAt = DateTime.UtcNow }
        ], CancellationToken.None);

        await _repo.DeleteAsync([(KnowledgeEntryKind.Skill, "ToDelete")], CancellationToken.None);

        var hashes = await _repo.GetAllHashesAsync(CancellationToken.None);
        hashes.Keys.ShouldNotContain((KnowledgeEntryKind.Skill, "ToDelete"));
        hashes.Keys.ShouldContain((KnowledgeEntryKind.Skill, "ToKeep"));
    }
}
