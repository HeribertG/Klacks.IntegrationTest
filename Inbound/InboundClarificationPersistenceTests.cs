// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the inbound_clarifications table against the real PostgreSQL schema: the
/// partial unique index allows at most one Open clarification per client (resolved or soft-deleted
/// rows do not count), the conditional status transition only moves an Open row, the due query only
/// returns overdue Open rows, the rate-limit window counts a round by either its start (AskedAt) or its
/// end (ResolvedAt) and never Suggested rows, and the two new inbound_analyses columns round-trip. Also
/// covers InboundAnalysisRepository.ExistsBySourceAsync against the real unique index on
/// inbound_analyses(source_kind, source_id), which has no is_deleted filter: a soft-deleted row still
/// counts, unlike GetBySourceAsync which applies the global query filter. Rows are scoped by the
/// INTEGRATION_TEST_ prefix on Recipient (clarifications) and Channel (analyses).
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Inbound;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Inbound;

[TestFixture]
[Category("RealDatabase")]
public class InboundClarificationPersistenceTests
{
    private const string TestPrefix = "INTEGRATION_TEST_CLARIFICATION_";
    private const string DefaultConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

    private DataBaseContext _context = null!;
    private InboundClarificationRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? DefaultConnectionString;
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new InboundClarificationRepository(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.InboundClarifications.IgnoreQueryFilters()
            .Where(c => c.Recipient.StartsWith(TestPrefix))
            .ExecuteDeleteAsync();
        await _context.InboundAnalyses.IgnoreQueryFilters()
            .Where(a => a.Channel.StartsWith(TestPrefix))
            .ExecuteDeleteAsync();
        await _context.DisposeAsync();
    }

    private static InboundClarification Row(
        Guid clientId,
        InboundClarificationStatus status = InboundClarificationStatus.Open,
        DateTime? deadlineAt = null,
        DateTime? askedAt = null,
        DateTime? resolvedAt = null)
    {
        var now = DateTime.UtcNow;
        return new InboundClarification
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            SourceKind = InboundSourceKind.Email,
            Channel = "Email",
            Recipient = TestPrefix + Guid.NewGuid().ToString("N"),
            OriginalAnalysisId = Guid.NewGuid(),
            OriginalSourceId = Guid.NewGuid(),
            SenderDisplay = "Integration Test",
            OriginalText = "Ich fühle mich nicht gut.",
            OriginalReceivedAt = now,
            Question = "Heißt das, du kannst heute nicht arbeiten?",
            AskedAt = askedAt ?? now,
            DeadlineAt = deadlineAt ?? now.AddMinutes(60),
            Status = status,
            ResolvedAt = resolvedAt
        };
    }

    [Test]
    public async Task SecondOpenClarificationForTheSameClient_IsRejectedByThePartialUniqueIndex()
    {
        var clientId = Guid.NewGuid();

        (await _repository.TryAddOpenAsync(Row(clientId))).ShouldBeTrue();
        (await _repository.TryAddOpenAsync(Row(clientId))).ShouldBeFalse();

        (await _repository.GetOpenByClientAsync(clientId)).ShouldNotBeNull();
    }

    [Test]
    public async Task ResolvedClarification_DoesNotBlockANewOpenOne()
    {
        var clientId = Guid.NewGuid();
        await _repository.AddAsync(Row(clientId, InboundClarificationStatus.Answered));

        (await _repository.TryAddOpenAsync(Row(clientId))).ShouldBeTrue();
    }

    [Test]
    public async Task SoftDeletedOpenClarification_DoesNotBlockANewOpenOne()
    {
        var clientId = Guid.NewGuid();
        var deleted = Row(clientId);
        (await _repository.TryAddOpenAsync(deleted)).ShouldBeTrue();

        await _context.InboundClarifications
            .IgnoreQueryFilters()
            .Where(c => c.Id == deleted.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.IsDeleted, true));

        (await _repository.TryAddOpenAsync(Row(clientId))).ShouldBeTrue();
    }

    [Test]
    public async Task AddAsync_WithOpenStatus_Throws()
    {
        var clientId = Guid.NewGuid();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await _repository.AddAsync(Row(clientId, InboundClarificationStatus.Open)));
    }

    [Test]
    public async Task TryResolve_MovesOnlyAnOpenRow()
    {
        var row = Row(Guid.NewGuid());
        await _repository.TryAddOpenAsync(row);
        var resolvedAt = DateTime.UtcNow;

        (await _repository.TryResolveAsync(row.Id, InboundClarificationStatus.Expired, null, null, resolvedAt)).ShouldBeTrue();
        (await _repository.TryResolveAsync(row.Id, InboundClarificationStatus.Answered, Guid.NewGuid(), Guid.NewGuid(), resolvedAt)).ShouldBeFalse();

        var reread = await _repository.GetByIdAsync(row.Id);
        reread!.Status.ShouldBe(InboundClarificationStatus.Expired);
        reread.ResolvedAt.ShouldNotBeNull();
        reread.AnswerSourceId.ShouldBeNull();
        reread.ResultAnalysisId.ShouldBeNull();
    }

    [Test]
    public async Task GetOpenDue_ReturnsOnlyOverdueOpenRows()
    {
        var now = DateTime.UtcNow;
        var overdue = Row(Guid.NewGuid(), deadlineAt: now.AddMinutes(-5));
        var notDue = Row(Guid.NewGuid(), deadlineAt: now.AddMinutes(30));
        var answered = Row(Guid.NewGuid(), InboundClarificationStatus.Answered, now.AddMinutes(-5));
        await _repository.TryAddOpenAsync(overdue);
        await _repository.TryAddOpenAsync(notDue);
        await _repository.AddAsync(answered);

        var due = await _repository.GetOpenDueAsync(now);

        due.Select(c => c.Id).ShouldContain(overdue.Id);
        due.Select(c => c.Id).ShouldNotContain(notDue.Id);
        due.Select(c => c.Id).ShouldNotContain(answered.Id);
    }

    [Test]
    public async Task CountAskedSince_IgnoresSuggestedRows()
    {
        var clientId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var since = now.AddMinutes(-60);
        await _repository.AddAsync(Row(clientId, InboundClarificationStatus.Suggested, askedAt: now.AddMinutes(-90), resolvedAt: now.AddMinutes(-10)));
        await _repository.AddAsync(Row(clientId, InboundClarificationStatus.Unresolved));

        (await _repository.CountAskedSinceAsync(clientId, since)).ShouldBe(1);
    }

    [Test]
    public async Task CountAskedSince_CountsARoundThatEndedWithinTheWindow_EvenThoughItWasAskedBeforeIt()
    {
        var clientId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var since = now.AddMinutes(-60);
        await _repository.AddAsync(Row(clientId, InboundClarificationStatus.Unresolved, askedAt: now.AddMinutes(-90), resolvedAt: now.AddMinutes(-10)));

        (await _repository.CountAskedSinceAsync(clientId, since)).ShouldBe(1);
    }

    [Test]
    public async Task CountAskedSince_ExcludesARoundThatWasAskedAndEndedLongAgo()
    {
        var clientId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var since = now.AddMinutes(-60);
        await _repository.AddAsync(Row(clientId, InboundClarificationStatus.Expired, askedAt: now.AddMinutes(-200), resolvedAt: now.AddMinutes(-150)));

        (await _repository.CountAskedSinceAsync(clientId, since)).ShouldBe(0);
    }

    [Test]
    public async Task InboundAnalysisClarificationColumns_RoundTrip()
    {
        var analysis = new InboundAnalysis
        {
            Id = Guid.NewGuid(),
            SourceKind = InboundSourceKind.Messenger,
            SourceId = Guid.NewGuid(),
            Channel = TestPrefix + "Messenger",
            Intent = EmailIntent.Other,
            Confidence = EmailConfidence.Low,
            Summary = "Unclear message",
            AnalyzedAt = DateTime.UtcNow,
            NeedsClarification = true,
            ClarificationQuestion = "Kannst du heute arbeiten?"
        };
        _context.InboundAnalyses.Add(analysis);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var reread = await _context.InboundAnalyses.AsNoTracking().SingleAsync(a => a.Id == analysis.Id);

        reread.NeedsClarification.ShouldBeTrue();
        reread.ClarificationQuestion.ShouldBe("Kannst du heute arbeiten?");
    }

    [Test]
    public async Task ExistsBySource_SoftDeletedAnalysis_StillCounts()
    {
        var sourceId = Guid.NewGuid();
        var analysis = new InboundAnalysis
        {
            Id = Guid.NewGuid(),
            SourceKind = InboundSourceKind.Email,
            SourceId = sourceId,
            Channel = TestPrefix + "Email",
            Intent = EmailIntent.Other,
            Confidence = EmailConfidence.Low,
            Summary = "Soft-deleted analysis",
            AnalyzedAt = DateTime.UtcNow
        };
        _context.InboundAnalyses.Add(analysis);
        await _context.SaveChangesAsync();
        await _context.InboundAnalyses.IgnoreQueryFilters()
            .Where(a => a.Id == analysis.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsDeleted, true));
        _context.ChangeTracker.Clear();

        var analysisRepository = new InboundAnalysisRepository(_context);

        (await analysisRepository.ExistsBySourceAsync(InboundSourceKind.Email, sourceId)).ShouldBeTrue();
        (await analysisRepository.GetBySourceAsync(InboundSourceKind.Email, sourceId)).ShouldBeNull();
    }

    [Test]
    public async Task ExistsBySource_UnknownSourceId_ReturnsFalse()
    {
        var analysisRepository = new InboundAnalysisRepository(_context);

        (await analysisRepository.ExistsBySourceAsync(InboundSourceKind.Email, Guid.NewGuid())).ShouldBeFalse();
    }
}
