using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class MidnightWorkChangeEntryDateTests
{
    private DataBaseContext _context = null!;
    private ScheduleEntriesService _service = null!;
    private string _connectionString = null!;

    private Guid _clientId;
    private Guid _shiftId;
    private Guid _workId;
    private readonly List<Guid> _workChangeIds = new();
    private static readonly DateOnly WorkDate = new(2025, 1, 15);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
        var logger = Substitute.For<ILogger<ScheduleEntriesService>>();
        _service = new ScheduleEntriesService(_context, logger);

        _clientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _workId = Guid.NewGuid();
        _workChangeIds.Clear();

        _context.Client.Add(new Klacks.Api.Domain.Models.Staffs.Client
        {
            Id = _clientId,
            Name = "TEST_MidnightWorkChange",
            FirstName = "Integration",
            IsDeleted = false
        });

        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = "TEST_NightShift",
            StartShift = new TimeOnly(22, 0, 0),
            EndShift = new TimeOnly(6, 0, 0),
            IsDeleted = false
        });

        await _context.SaveChangesAsync();

        _context.Work.Add(new Work
        {
            Id = _workId,
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = WorkDate,
            WorkTime = 480,
            StartTime = new TimeOnly(22, 0, 0),
            EndTime = new TimeOnly(6, 0, 0),
            IsDeleted = false
        });

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var id in _workChangeIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM work_change WHERE id = {0}", id);
        }
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM work WHERE id = {0}", _workId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE id = {0}", _shiftId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client WHERE id = {0}", _clientId);
        _context.Dispose();
    }

    [Test]
    public async Task CorrectionStart_BeforeShiftStart_StaysOnWorkDate()
    {
        await AddWorkChange(WorkChangeType.CorrectionStart, new TimeOnly(21, 45, 0), new TimeOnly(22, 0, 0));

        var result = await LoadEntry();

        result.EntryDate.Date.Should().Be(WorkDate.ToDateTime(TimeOnly.MinValue), "correction-start before shift start belongs to the work date, not the day after");
    }

    [Test]
    [Ignore("Superseded by WorkChange Phase 2 (DevKnowledge 7769a32c): CorrectionStart always stays on the work date; wc.start_time is no longer a discriminator because duration-only types persist 00:00.")]
    public async Task CorrectionStart_AfterMidnight_MovesToNextDay()
    {
        await AddWorkChange(WorkChangeType.CorrectionStart, new TimeOnly(1, 0, 0), new TimeOnly(1, 30, 0));

        var result = await LoadEntry();

        result.EntryDate.Date.Should().Be(WorkDate.AddDays(1).ToDateTime(TimeOnly.MinValue), "correction-start between midnight and shift end is on the next calendar day");
    }

    [Test]
    public async Task CorrectionEnd_AfterShiftEnd_StaysOnNextDay()
    {
        await AddWorkChange(WorkChangeType.CorrectionEnd, new TimeOnly(6, 0, 0), new TimeOnly(6, 15, 0));

        var result = await LoadEntry();

        result.EntryDate.Date.Should().Be(WorkDate.AddDays(1).ToDateTime(TimeOnly.MinValue), "correction-end after shift end stays on the day the shift terminates");
    }

    private async Task AddWorkChange(WorkChangeType type, TimeOnly start, TimeOnly end)
    {
        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = _workId,
            Type = type,
            StartTime = start,
            EndTime = end,
            ChangeTime = 15,
            ToInvoice = false,
            Description = $"TEST {type} {start:HH:mm}-{end:HH:mm}",
            IsDeleted = false
        };
        _context.WorkChange.Add(workChange);
        await _context.SaveChangesAsync();
        _workChangeIds.Add(workChange.Id);
    }

    private async Task<ScheduleCell> LoadEntry()
    {
        var start = WorkDate.AddDays(-1);
        var end = WorkDate.AddDays(2);
        var entries = await _service.GetScheduleEntriesQuery(start, end)
            .Where(e => e.ClientId == _clientId && e.EntryType == 1)
            .ToListAsync();
        entries.Should().HaveCount(1);
        return entries[0];
    }
}
