using FluentAssertions;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class ExportLogRepositoryTests
{
    private DataBaseContext _context = null!;
    private ExportLogRepository _repo = null!;
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
        _repo = new ExportLogRepository(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_insertedIds.Count > 0)
        {
            await _context.ExportLog
                .Where(e => _insertedIds.Contains(e.Id))
                .ExecuteDeleteAsync();
        }

        _context.Dispose();
        _insertedIds.Clear();
    }

    [Test]
    public async Task AddAsync_PersistsEntry()
    {
        var entry = new ExportLog
        {
            Format = "CSV",
            StartDate = new DateOnly(2026, 2, 1),
            EndDate = new DateOnly(2026, 2, 28),
            Language = "de",
            CurrencyCode = "CHF",
            FileName = "export_feb.csv",
            FileSize = 1024,
            RecordCount = 50,
            ExportedAt = DateTime.UtcNow,
            ExportedBy = "integration-test"
        };

        await _repo.AddAsync(entry);
        await _context.SaveChangesAsync();
        _insertedIds.Add(entry.Id);

        entry.Id.Should().NotBe(Guid.Empty);
        _context.ChangeTracker.Clear();
        var reloaded = await _context.ExportLog.FindAsync(entry.Id);
        reloaded.Should().NotBeNull();
        reloaded!.RecordCount.Should().Be(50);
    }

    [Test]
    public async Task HasExportForPeriodAsync_ReturnsTrue_WhenOverlapping()
    {
        var entry = new ExportLog
        {
            Format = "CSV",
            StartDate = new DateOnly(2026, 3, 1),
            EndDate = new DateOnly(2026, 3, 31),
            Language = "de",
            CurrencyCode = "EUR",
            FileName = "export_mar.csv",
            FileSize = 512,
            RecordCount = 20,
            ExportedAt = DateTime.UtcNow,
            ExportedBy = "integration-test"
        };

        await _repo.AddAsync(entry);
        await _context.SaveChangesAsync();
        _insertedIds.Add(entry.Id);

        _context.ChangeTracker.Clear();
        var result = await _repo.HasExportForPeriodAsync(
            new DateOnly(2026, 3, 10),
            new DateOnly(2026, 3, 20),
            null);

        result.Should().BeTrue();
    }

    [Test]
    public async Task HasExportForPeriodAsync_GlobalExport_MatchesAnyGroupQuery()
    {
        var globalEntry = new ExportLog
        {
            Format = "csv",
            StartDate = new DateOnly(2026, 6, 1),
            EndDate = new DateOnly(2026, 6, 30),
            GroupId = null,
            FileName = "global.csv",
            ExportedAt = DateTime.UtcNow,
            ExportedBy = "integration-test"
        };
        var groupAEntry = new ExportLog
        {
            Format = "datev",
            StartDate = new DateOnly(2026, 6, 1),
            EndDate = new DateOnly(2026, 6, 30),
            GroupId = Guid.NewGuid(),
            FileName = "group-a.csv",
            ExportedAt = DateTime.UtcNow,
            ExportedBy = "integration-test"
        };
        await _repo.AddAsync(globalEntry);
        await _context.SaveChangesAsync();
        await _repo.AddAsync(groupAEntry);
        await _context.SaveChangesAsync();
        _insertedIds.Add(globalEntry.Id);
        _insertedIds.Add(groupAEntry.Id);

        var randomGroupId = Guid.NewGuid();
        (await _repo.HasExportForPeriodAsync(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 15), randomGroupId)).Should().BeTrue();
        (await _repo.HasExportForPeriodAsync(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 15), null)).Should().BeTrue();
    }

    [Test]
    public async Task HasExportForPeriodAsync_ReturnsFalse_WhenDisjoint()
    {
        var entry = new ExportLog
        {
            Format = "CSV",
            StartDate = new DateOnly(2026, 4, 1),
            EndDate = new DateOnly(2026, 4, 30),
            Language = "de",
            CurrencyCode = "EUR",
            FileName = "export_apr.csv",
            FileSize = 256,
            RecordCount = 10,
            ExportedAt = DateTime.UtcNow,
            ExportedBy = "integration-test"
        };

        await _repo.AddAsync(entry);
        await _context.SaveChangesAsync();
        _insertedIds.Add(entry.Id);

        _context.ChangeTracker.Clear();
        var result = await _repo.HasExportForPeriodAsync(
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 31),
            null);

        result.Should().BeFalse();
    }
}
