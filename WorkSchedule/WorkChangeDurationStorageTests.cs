// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for WorkChange Phase 2 duration-based storage and
/// dynamic time-range computation via the get_schedule_entries stored procedure.
///
/// Spec (from DevKnowledge e08d1c32-ee6d-4fb0-9b14-1c38480727d5):
///   Duration-only types (0 CorrectionEnd, 1 CorrectionStart, 2 ReplacementStart,
///   3 ReplacementEnd, 4 TravelStart, 5 TravelEnd, 7 Briefing, 8 Debriefing)
///   persist ChangeTime (hours) and StartTime/EndTime = 00:00. The SP derives the
///   effective Von/Bis via window-function stacking.
///
///   Within types (6 TravelWithin, 9 ReplacementWithin) persist explicit
///   StartTime/EndTime. The SP echoes them unchanged.
/// </summary>

using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class WorkChangeDurationStorageTests
{
    private const int EntryTypeWorkChange = 1;
    private static readonly DateOnly WorkDate = new(2026, 6, 15);
    private static readonly TimeOnly ShiftStart = new(8, 0, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0, 0);
    private static readonly TimeSpan ShiftStartSpan = ShiftStart.ToTimeSpan();
    private static readonly TimeSpan ShiftEndSpan = ShiftEnd.ToTimeSpan();

    private static TimeSpan T(int h, int m) => new(h, m, 0);

    private DataBaseContext _context = null!;
    private ScheduleEntriesService _service = null!;
    private string _connectionString = null!;

    private Guid _clientId;
    private Guid _replaceClientId;
    private Guid _shiftId;
    private Guid _workId;
    private readonly List<Guid> _workChangeIds = new();

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
        _replaceClientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _workId = Guid.NewGuid();
        _workChangeIds.Clear();

        _context.Client.Add(new Klacks.Api.Domain.Models.Staffs.Client
        {
            Id = _clientId,
            Name = "TEST_WCDuration_Owner",
            FirstName = "Integration",
            IsDeleted = false
        });

        _context.Client.Add(new Klacks.Api.Domain.Models.Staffs.Client
        {
            Id = _replaceClientId,
            Name = "TEST_WCDuration_Replace",
            FirstName = "Integration",
            IsDeleted = false
        });

        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = "TEST_DayShift_0800_1600",
            StartShift = ShiftStart,
            EndShift = ShiftEnd,
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
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
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
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client WHERE id = {0}", _replaceClientId);
        _context.Dispose();
    }

    [TestCase(WorkChangeType.CorrectionEnd)]
    [TestCase(WorkChangeType.CorrectionStart)]
    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.TravelStart)]
    [TestCase(WorkChangeType.TravelEnd)]
    [TestCase(WorkChangeType.Briefing)]
    [TestCase(WorkChangeType.Debriefing)]
    public async Task DurationTypes_StoreZeroTimes_AndChangeTime(WorkChangeType type)
    {
        var requiresReplaceClient = type == WorkChangeType.ReplacementStart || type == WorkChangeType.ReplacementEnd;
        var id = await InsertWorkChange(
            type,
            TimeOnly.MinValue,
            TimeOnly.MinValue,
            changeTime: 0.5m,
            replaceClientId: requiresReplaceClient ? _replaceClientId : null);

        var stored = await _context.WorkChange.AsNoTracking().FirstAsync(wc => wc.Id == id);

        stored.StartTime.Should().Be(TimeOnly.MinValue, "duration-based types must persist StartTime as 00:00");
        stored.EndTime.Should().Be(TimeOnly.MinValue, "duration-based types must persist EndTime as 00:00");
        stored.ChangeTime.Should().Be(0.5m, "ChangeTime (hours) is the duration source of truth");
    }

    [TestCase(WorkChangeType.TravelWithin)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public async Task WithinTypes_StoreExplicitStartAndEnd(WorkChangeType type)
    {
        var start = new TimeOnly(12, 0, 0);
        var end = new TimeOnly(13, 0, 0);
        var replaceClientId = type == WorkChangeType.ReplacementWithin ? (Guid?)_replaceClientId : null;

        var id = await InsertWorkChange(type, start, end, changeTime: 1.0m, replaceClientId: replaceClientId);

        var stored = await _context.WorkChange.AsNoTracking().FirstAsync(wc => wc.Id == id);

        stored.StartTime.Should().Be(start);
        stored.EndTime.Should().Be(end);
        stored.ChangeTime.Should().Be(1.0m);
    }

    [Test]
    public async Task CorrectionEnd_DurationOnly_SpComputesEndOfShiftRange()
    {
        await InsertWorkChange(WorkChangeType.CorrectionEnd, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m);

        var entry = await LoadSingleWorkChangeEntry();

        entry.StartTime.Should().Be(T(16, 0));
        entry.EndTime.Should().Be(T(16, 30));
        entry.WorkChangeType.Should().Be((int)WorkChangeType.CorrectionEnd);
    }

    [Test]
    public async Task CorrectionStart_DurationOnly_SpComputesBeforeShiftRange()
    {
        await InsertWorkChange(WorkChangeType.CorrectionStart, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m);

        var entry = await LoadSingleWorkChangeEntry();

        entry.StartTime.Should().Be(T(7, 30));
        entry.EndTime.Should().Be(T(8, 0));
        entry.WorkChangeType.Should().Be((int)WorkChangeType.CorrectionStart);
    }

    [Test]
    public async Task Briefing_DurationOnly_SpComputesBeforeShiftRange()
    {
        await InsertWorkChange(WorkChangeType.Briefing, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m);

        var entry = await LoadSingleWorkChangeEntry();

        entry.StartTime.Should().Be(T(7, 30));
        entry.EndTime.Should().Be(T(8, 0));
    }

    [Test]
    public async Task Debriefing_DurationOnly_SpComputesAfterShiftRange()
    {
        await InsertWorkChange(WorkChangeType.Debriefing, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m);

        var entry = await LoadSingleWorkChangeEntry();

        entry.StartTime.Should().Be(T(16, 0));
        entry.EndTime.Should().Be(T(16, 30));
    }

    [Test]
    public async Task TravelStart_DurationOnly_SpComputesBeforeShiftRange()
    {
        await InsertWorkChange(WorkChangeType.TravelStart, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m);

        var entry = await LoadSingleWorkChangeEntry();

        entry.StartTime.Should().Be(T(7, 30));
        entry.EndTime.Should().Be(T(8, 0));
    }

    [Test]
    public async Task TravelEnd_DurationOnly_SpComputesAfterShiftRange()
    {
        await InsertWorkChange(WorkChangeType.TravelEnd, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m);

        var entry = await LoadSingleWorkChangeEntry();

        entry.StartTime.Should().Be(T(16, 0));
        entry.EndTime.Should().Be(T(16, 30));
    }

    [Test]
    public async Task TravelWithin_StoredTimes_SpEchoesStoredRange()
    {
        await InsertWorkChange(WorkChangeType.TravelWithin, new TimeOnly(12, 0), new TimeOnly(13, 0), changeTime: 1.0m);

        var entry = await LoadSingleWorkChangeEntry();

        entry.StartTime.Should().Be(T(12, 0));
        entry.EndTime.Should().Be(T(13, 0));
    }

    [Test]
    public async Task ReplacementWithin_StoredTimes_SpEchoesStoredRange_ForBothClients()
    {
        await InsertWorkChange(WorkChangeType.ReplacementWithin, new TimeOnly(12, 0), new TimeOnly(13, 0), changeTime: 1.0m, replaceClientId: _replaceClientId);

        var entries = await LoadWorkChangeEntries();

        entries.Should().HaveCount(2, "ReplacementWithin produces one row per involved client");
        entries.Should().Contain(e => e.ClientId == _clientId && e.StartTime == T(12, 0) && e.EndTime == T(13, 0));
        entries.Should().Contain(e => e.ClientId == _replaceClientId && e.StartTime == T(12, 0) && e.EndTime == T(13, 0));
    }

    [Test]
    public async Task ReplacementStart_DurationOnly_SpBuildsLosingBlockAtShiftStart()
    {
        await InsertWorkChange(WorkChangeType.ReplacementStart, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m, replaceClientId: _replaceClientId);

        var entries = await LoadWorkChangeEntries();

        entries.Should().HaveCount(2);
        var owner = entries.Single(e => e.ClientId == _clientId);
        var replace = entries.Single(e => e.ClientId == _replaceClientId);

        owner.StartTime.Should().Be(ShiftStartSpan);
        owner.EndTime.Should().Be(T(8, 30));
        replace.StartTime.Should().Be(ShiftStartSpan);
        replace.EndTime.Should().Be(T(8, 30));
    }

    [Test]
    public async Task ReplacementEnd_DurationOnly_SpBuildsLosingBlockBeforeShiftEnd()
    {
        await InsertWorkChange(WorkChangeType.ReplacementEnd, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m, replaceClientId: _replaceClientId);

        var entries = await LoadWorkChangeEntries();

        entries.Should().HaveCount(2);
        var owner = entries.Single(e => e.ClientId == _clientId);
        var replace = entries.Single(e => e.ClientId == _replaceClientId);

        owner.StartTime.Should().Be(T(15, 30));
        owner.EndTime.Should().Be(ShiftEndSpan);
        replace.StartTime.Should().Be(T(15, 30));
        replace.EndTime.Should().Be(ShiftEndSpan);
    }

    [Test]
    public async Task BeforeShiftStacking_CorrectionInner_BriefingMiddle_TravelOuter()
    {
        await InsertWorkChange(WorkChangeType.CorrectionStart, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.25m);
        await InsertWorkChange(WorkChangeType.Briefing, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m);
        await InsertWorkChange(WorkChangeType.TravelStart, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 2.0m);

        var entries = await LoadWorkChangeEntries();

        entries.Should().HaveCount(3);
        var correction = entries.Single(e => e.WorkChangeType == (int)WorkChangeType.CorrectionStart);
        var briefing = entries.Single(e => e.WorkChangeType == (int)WorkChangeType.Briefing);
        var travel = entries.Single(e => e.WorkChangeType == (int)WorkChangeType.TravelStart);

        correction.EndTime.Should().Be(ShiftStartSpan);
        correction.StartTime.Should().Be(T(7, 45));

        briefing.EndTime.Should().Be(T(7, 45));
        briefing.StartTime.Should().Be(T(7, 15));

        travel.EndTime.Should().Be(T(7, 15));
        travel.StartTime.Should().Be(T(5, 15));
    }

    [TestCase(WorkChangeType.CorrectionEnd)]
    [TestCase(WorkChangeType.CorrectionStart)]
    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.TravelStart)]
    [TestCase(WorkChangeType.TravelEnd)]
    [TestCase(WorkChangeType.Briefing)]
    [TestCase(WorkChangeType.Debriefing)]
    public async Task WorkMacroService_DurationTypes_PreservesChangeTime(WorkChangeType type)
    {
        var service = CreateWorkMacroService();
        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = _workId,
            Type = type,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            ChangeTime = 0.5m,
            IsDeleted = false
        };

        await service.ProcessWorkChangeMacroAsync(workChange);

        workChange.ChangeTime.Should().Be(0.5m,
            "duration-only types send ChangeTime directly; macro must not overwrite it from 00:00-00:00");
        workChange.StartTime.Should().Be(TimeOnly.MinValue);
        workChange.EndTime.Should().Be(TimeOnly.MinValue);
    }

    [TestCase(WorkChangeType.TravelWithin)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public async Task WorkMacroService_WithinTypes_RecalculatesChangeTimeFromRange(WorkChangeType type)
    {
        var service = CreateWorkMacroService();
        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = _workId,
            Type = type,
            StartTime = new TimeOnly(12, 0, 0),
            EndTime = new TimeOnly(13, 30, 0),
            ChangeTime = 0m,
            IsDeleted = false
        };

        await service.ProcessWorkChangeMacroAsync(workChange);

        workChange.ChangeTime.Should().Be(1.5m, "within types derive ChangeTime from the explicit Von/Bis span");
    }

    [Test]
    public async Task AfterShiftStacking_CorrectionInner_DebriefingMiddle_TravelOuter()
    {
        await InsertWorkChange(WorkChangeType.CorrectionEnd, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.25m);
        await InsertWorkChange(WorkChangeType.Debriefing, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 0.5m);
        await InsertWorkChange(WorkChangeType.TravelEnd, TimeOnly.MinValue, TimeOnly.MinValue, changeTime: 2.0m);

        var entries = await LoadWorkChangeEntries();

        entries.Should().HaveCount(3);
        var correction = entries.Single(e => e.WorkChangeType == (int)WorkChangeType.CorrectionEnd);
        var debriefing = entries.Single(e => e.WorkChangeType == (int)WorkChangeType.Debriefing);
        var travel = entries.Single(e => e.WorkChangeType == (int)WorkChangeType.TravelEnd);

        correction.StartTime.Should().Be(ShiftEndSpan);
        correction.EndTime.Should().Be(T(16, 15));

        debriefing.StartTime.Should().Be(T(16, 15));
        debriefing.EndTime.Should().Be(T(16, 45));

        travel.StartTime.Should().Be(T(16, 45));
        travel.EndTime.Should().Be(T(18, 45));
    }

    private WorkMacroService CreateWorkMacroService()
    {
        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Get(Arg.Any<Guid>()).Returns(ci => _context.Shift.AsNoTracking().FirstOrDefault(s => s.Id == (Guid)ci[0]));
        var macroDataProvider = Substitute.For<IMacroDataProvider>();
        var macroCompilationService = Substitute.For<IMacroCompilationService>();
        var logger = Substitute.For<ILogger<WorkMacroService>>();
        return new WorkMacroService(_context, shiftRepository, macroDataProvider, macroCompilationService, logger);
    }

    private async Task<Guid> InsertWorkChange(
        WorkChangeType type,
        TimeOnly startTime,
        TimeOnly endTime,
        decimal changeTime,
        Guid? replaceClientId = null)
    {
        var id = Guid.NewGuid();
        _context.WorkChange.Add(new WorkChange
        {
            Id = id,
            WorkId = _workId,
            Type = type,
            StartTime = startTime,
            EndTime = endTime,
            ChangeTime = changeTime,
            Surcharges = 0m,
            ReplaceClientId = replaceClientId,
            Description = $"TEST {type} ct={changeTime}",
            ToInvoice = false,
            IsDeleted = false
        });
        await _context.SaveChangesAsync();
        _workChangeIds.Add(id);
        return id;
    }

    private async Task<ScheduleCell> LoadSingleWorkChangeEntry()
    {
        var entries = await LoadWorkChangeEntries();
        entries.Should().HaveCount(1);
        return entries.Single();
    }

    private async Task<List<ScheduleCell>> LoadWorkChangeEntries()
    {
        var start = WorkDate.AddDays(-1);
        var end = WorkDate.AddDays(2);
        return await _service.GetScheduleEntriesQuery(start, end)
            .Where(e => (e.ClientId == _clientId || e.ClientId == _replaceClientId)
                        && e.EntryType == EntryTypeWorkChange)
            .ToListAsync();
    }
}
