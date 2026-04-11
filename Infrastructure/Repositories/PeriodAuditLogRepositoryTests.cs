using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class PeriodAuditLogRepositoryTests
{
    private DataBaseContext _context = null!;
    private PeriodAuditLogRepository _repo = null!;
    private readonly List<Guid> _insertedIds = [];

    [SetUp]
    public void SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repo = new PeriodAuditLogRepository(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_insertedIds.Count > 0)
        {
            await _context.PeriodAuditLog
                .Where(e => _insertedIds.Contains(e.Id))
                .ExecuteDeleteAsync();
        }

        _context.Dispose();
        _insertedIds.Clear();
    }

    [Test]
    public async Task AddAsync_PersistsEntry_WithGeneratedId()
    {
        var entry = new PeriodAuditLog
        {
            Action = PeriodAuditAction.Seal,
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            PerformedAt = DateTime.UtcNow,
            PerformedBy = "integration-test",
            AffectedCount = 42
        };

        await _repo.AddAsync(entry);
        await _context.SaveChangesAsync();
        _insertedIds.Add(entry.Id);

        entry.Id.Should().NotBe(Guid.Empty);
        _context.ChangeTracker.Clear();
        var reloaded = await _context.PeriodAuditLog.FindAsync(entry.Id);
        reloaded.Should().NotBeNull();
        reloaded!.AffectedCount.Should().Be(42);
    }

    [Test]
    public async Task GetRangeAsync_ReturnsOverlappingEntries_OrderedByPerformedAtDesc()
    {
        var entry1 = new PeriodAuditLog
        {
            Action = PeriodAuditAction.Seal,
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            PerformedAt = DateTime.UtcNow.AddMinutes(-10),
            PerformedBy = "u1"
        };
        var entry2 = new PeriodAuditLog
        {
            Action = PeriodAuditAction.Unseal,
            StartDate = new DateOnly(2026, 1, 15),
            EndDate = new DateOnly(2026, 1, 15),
            Reason = "Correction",
            PerformedAt = DateTime.UtcNow,
            PerformedBy = "u2"
        };

        await _repo.AddAsync(entry1);
        await _context.SaveChangesAsync();
        _insertedIds.Add(entry1.Id);
        await _repo.AddAsync(entry2);
        await _context.SaveChangesAsync();
        _insertedIds.Add(entry2.Id);

        _context.ChangeTracker.Clear();
        var result = await _repo.GetRangeAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        result.Where(r => _insertedIds.Contains(r.Id)).Should().HaveCount(2);
        var ownResults = result.Where(r => _insertedIds.Contains(r.Id)).ToList();
        ownResults[0].Action.Should().Be(PeriodAuditAction.Unseal);
        ownResults[1].Action.Should().Be(PeriodAuditAction.Seal);
    }
}
