// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for PendingUserNoteRepository against the real database, verifying the
/// stash → deliver → recall semantics for personal notes and the deferred broadcast lifecycle
/// (first delivery starts the timer; the broadcast stays visible to other users until expiry).
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AI;

[TestFixture]
[Category("RealDatabase")]
public class PendingUserNoteRepositoryTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PENDINGNOTE_";

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private PendingUserNoteRepository _repository = null!;
    private Guid _agentId;
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();

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
    public void SetUp()
    {
        _context = NewContext();
        _repository = new PendingUserNoteRepository(_context);
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
            "DELETE FROM pending_user_notes WHERE content LIKE {0}", TestPrefix + "%");
    }

    private PendingUserNote NewNote(Guid? userId, string label) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = _agentId,
        UserId = userId,
        Content = TestPrefix + label
    };

    [Test]
    public async Task PersonalNote_AfterDelivery_LeavesPendingButRemainsRecallable()
    {
        var note = NewNote(_userA, "personal");
        await _repository.AddAsync(note);

        (await _repository.GetPendingAsync(_agentId, _userA)).ShouldContain(n => n.Id == note.Id);

        var marked = await _repository.MarkDeliveredAsync(_agentId, _userA, new[] { note.Id });
        marked.ShouldBe(1);

        (await _repository.GetPendingAsync(_agentId, _userA)).ShouldNotContain(n => n.Id == note.Id);
        (await _repository.GetDeliveredAsync(_agentId, _userA)).ShouldContain(n => n.Id == note.Id);
    }

    [Test]
    public async Task PersonalNote_IsNotVisibleOrMarkableByAnotherUser()
    {
        var note = NewNote(_userA, "personal-for-a");
        await _repository.AddAsync(note);

        (await _repository.GetPendingAsync(_agentId, _userB)).ShouldNotContain(n => n.Id == note.Id);

        var marked = await _repository.MarkDeliveredAsync(_agentId, _userB, new[] { note.Id });
        marked.ShouldBe(0);

        (await _repository.GetPendingAsync(_agentId, _userA)).ShouldContain(n => n.Id == note.Id);
    }

    [Test]
    public async Task Broadcast_IsVisibleToEveryUser()
    {
        var note = NewNote(null, "broadcast-visible");
        await _repository.AddAsync(note);

        (await _repository.GetPendingAsync(_agentId, _userA)).ShouldContain(n => n.Id == note.Id);
        (await _repository.GetPendingAsync(_agentId, _userB)).ShouldContain(n => n.Id == note.Id);
        (await _repository.CountPendingAsync(_agentId, _userB)).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Broadcast_FirstDelivery_StartsTimer_ButStaysVisibleToOthers()
    {
        var note = NewNote(null, "broadcast-deliver");
        await _repository.AddAsync(note);

        var marked = await _repository.MarkDeliveredAsync(_agentId, _userA, new[] { note.Id });
        marked.ShouldBe(1);

        (await _repository.GetPendingAsync(_agentId, _userB)).ShouldContain(n => n.Id == note.Id);

        var reloaded = await _repository.GetByIdAsync(note.Id);
        reloaded.ShouldNotBeNull();
        reloaded!.FirstDeliveredAt.ShouldNotBeNull();
        reloaded.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task Broadcast_ExpiresOnlyAfterFirstDeliveryPlusLifetime()
    {
        var note = NewNote(null, "broadcast-expire");
        await _repository.AddAsync(note);

        (await _repository.ExpireBroadcastsAsync(TimeSpan.Zero)).ShouldBe(0);
        (await _repository.GetPendingAsync(_agentId, _userA)).ShouldContain(n => n.Id == note.Id);

        await _repository.MarkDeliveredAsync(_agentId, _userA, new[] { note.Id });
        await Task.Delay(10);

        (await _repository.ExpireBroadcastsAsync(TimeSpan.Zero)).ShouldBeGreaterThanOrEqualTo(1);

        (await _repository.GetPendingAsync(_agentId, _userA)).ShouldNotContain(n => n.Id == note.Id);
        (await _repository.GetDeliveredAsync(_agentId, _userA)).ShouldContain(n => n.Id == note.Id);
    }
}
