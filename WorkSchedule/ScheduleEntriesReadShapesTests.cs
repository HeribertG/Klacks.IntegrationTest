// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

/// <summary>
/// Pins, against real PostgreSQL, every read shape the Application layer builds on get_schedule_entries:
/// the three-day refresh window of one or two clients, a client set over a range (empty set = no rows),
/// the unfiltered range read, the single-day Work slots ordered by start time and the single-day breaks.
/// Each shape is checked twice: against hand-pinned rows (incl. midnight-crossing night shifts at both
/// window edges, group-restricted flags and a scenario token) and against the raw function composed exactly
/// like the callers did before the reads moved behind IScheduleEntriesService (same filter, same order).
/// </summary>
[TestFixture]
public class ScheduleEntriesReadShapesTests
{
    private const string Prefix = "INTEGRATION_TEST_ReadShapes_";
    private static readonly DateOnly Day = new(2019, 3, 13);

    private DataBaseContext _context = null!;
    private IScheduleEntriesService _service = null!;
    private string _connectionString = null!;

    private readonly Dictionary<Guid, string> _alias = new();
    private readonly List<Guid> _workIds = new();
    private readonly List<Guid> _workChangeIds = new();
    private readonly List<Guid> _breakIds = new();
    private readonly List<Guid> _expenseIds = new();
    private readonly List<Guid> _groupItemIds = new();

    private Guid _visibleGroupId;
    private Guid _foreignGroupId;
    private Guid _clientA;
    private Guid _clientB;
    private Guid _dayShiftId;
    private Guid _nightShiftId;
    private Guid _lateShiftId;
    private Guid _absenceId;
    private Guid _token;

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
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = new ScheduleEntriesService(_context, Substitute.For<ILogger<ScheduleEntriesService>>());

        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var id in _expenseIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM expenses WHERE id = {0}", id);
        }
        foreach (var id in _workChangeIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM work_change WHERE id = {0}", id);
        }
        foreach (var id in _breakIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM \"break\" WHERE id = {0}", id);
        }
        foreach (var id in _workIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM work WHERE id = {0}", id);
        }
        foreach (var id in _groupItemIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM group_item WHERE id = {0}", id);
        }
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id IN ({0}, {1}, {2})", _dayShiftId, _nightShiftId, _lateShiftId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM absence WHERE id = {0}", _absenceId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client WHERE id IN ({0}, {1})", _clientA, _clientB);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE id IN ({0}, {1})", _visibleGroupId, _foreignGroupId);
        _context.Dispose();
        _alias.Clear();
        _workIds.Clear();
        _workChangeIds.Clear();
        _breakIds.Clear();
        _expenseIds.Clear();
        _groupItemIds.Clear();
    }

    [Test]
    public async Task ThreeDayWindow_OneClient_SelectsByWorkDay_SoTheNightBeforeIsOutAndTheNightAfterBringsItsNextDayCorrection()
    {
        var rows = await ReadThreeDayWindowAsync([_clientA], [_visibleGroupId], null);

        Signatures(rows).ShouldBe(
        [
            "0|A|2019-03-13|08:00|12:00|False|wA_d_early",
            "2|A|2019-03-13|08:00|08:01|False|exA",
            "3|A|2019-03-13|10:00|11:00|False|brA",
            "0|A|2019-03-13|14:00|18:00|False|wA_d_late",
            "0|A|2019-03-14|22:00|06:00|True|wA_p1",
            "1|A|2019-03-15|06:00|12:00|True|wcA_p1",
        ]);
        await ShouldEqualLegacyAsync(rows, Day.AddDays(-1), Day.AddDays(1), [_visibleGroupId], null,
            c => c.ClientId == _clientA);
    }

    [Test]
    public async Task ThreeDayWindow_WithScenarioToken_ReturnsOnlyTheScenarioRows()
    {
        var rows = await ReadThreeDayWindowAsync([_clientA], [_visibleGroupId], _token);

        Signatures(rows).ShouldBe(["0|A|2019-03-13|08:00|16:00|False|wA_tok"]);
        await ShouldEqualLegacyAsync(rows, Day.AddDays(-1), Day.AddDays(1), [_visibleGroupId], _token,
            c => c.ClientId == _clientA);
    }

    [Test]
    public async Task ThreeDayWindow_TwoClients_ReturnsBothClientsInFunctionOrder()
    {
        var rows = await ReadThreeDayWindowAsync([_clientA, _clientB], [_visibleGroupId], null);

        var expected = new List<string>
        {
            "0|A|2019-03-13|08:00|12:00|False|wA_d_early",
            "2|A|2019-03-13|08:00|08:01|False|exA",
            "3|A|2019-03-13|10:00|11:00|False|brA",
            "0|A|2019-03-13|14:00|18:00|False|wA_d_late",
            "0|A|2019-03-14|22:00|06:00|True|wA_p1",
            "1|A|2019-03-15|06:00|12:00|True|wcA_p1",
            "0|B|2019-03-12|14:00|18:00|False|wB_m1",
            "0|B|2019-03-13|08:00|16:00|False|wB_d",
            "3|B|2019-03-14|09:00|10:00|False|brB",
        };
        SignaturesInClientOrder(rows).ShouldBe(expected);
        await ShouldEqualLegacyAsync(rows, Day.AddDays(-1), Day.AddDays(1), [_visibleGroupId], null,
            c => c.ClientId == _clientA || c.ClientId == _clientB);
    }

    [Test]
    public async Task Range_ClientSet_WithoutGroups_ReturnsBothClientsOverTheWholeRange()
    {
        var clientIds = new List<Guid> { _clientA, _clientB };
        var rows = await ReadRangeForClientsAsync(Day.AddDays(-2), Day.AddDays(2), clientIds, null);

        SignaturesInClientOrder(rows).ShouldBe(
        [
            "0|A|2019-03-11|22:00|06:00|False|wA_m2",
            "1|A|2019-03-12|06:00|12:00|False|wcA_m2",
            "0|A|2019-03-13|08:00|12:00|False|wA_d_early",
            "2|A|2019-03-13|08:00|08:01|False|exA",
            "3|A|2019-03-13|10:00|11:00|False|brA",
            "0|A|2019-03-13|14:00|18:00|False|wA_d_late",
            "0|A|2019-03-14|22:00|06:00|False|wA_p1",
            "1|A|2019-03-15|06:00|12:00|False|wcA_p1",
            "0|A|2019-03-15|08:00|16:00|False|wA_p2",
            "0|B|2019-03-12|14:00|18:00|False|wB_m1",
            "0|B|2019-03-13|08:00|16:00|False|wB_d",
            "3|B|2019-03-14|09:00|10:00|False|brB",
        ]);
        await ShouldEqualLegacyAsync(rows, Day.AddDays(-2), Day.AddDays(2), null, null,
            c => clientIds.Contains(c.ClientId));
    }

    [Test]
    public async Task Range_EmptyClientSet_ReturnsNoRows()
    {
        var rows = await ReadRangeForClientsAsync(Day.AddDays(-2), Day.AddDays(2), new List<Guid>(), null);

        rows.ShouldBeEmpty();
    }

    [Test]
    public async Task Range_Unfiltered_ReturnsEveryClientOfTheRange()
    {
        var rows = await ReadRangeAsync(Day.AddDays(-2), Day.AddDays(2), [_visibleGroupId], null);

        SignaturesInClientOrder(rows.Where(c => c.ClientId == _clientA || c.ClientId == _clientB).ToList()).ShouldBe(
        [
            "0|A|2019-03-11|22:00|06:00|True|wA_m2",
            "1|A|2019-03-12|06:00|12:00|True|wcA_m2",
            "0|A|2019-03-13|08:00|12:00|False|wA_d_early",
            "2|A|2019-03-13|08:00|08:01|False|exA",
            "3|A|2019-03-13|10:00|11:00|False|brA",
            "0|A|2019-03-13|14:00|18:00|False|wA_d_late",
            "0|A|2019-03-14|22:00|06:00|True|wA_p1",
            "1|A|2019-03-15|06:00|12:00|True|wcA_p1",
            "0|A|2019-03-15|08:00|16:00|False|wA_p2",
            "0|B|2019-03-12|14:00|18:00|False|wB_m1",
            "0|B|2019-03-13|08:00|16:00|False|wB_d",
            "3|B|2019-03-14|09:00|10:00|False|brB",
        ]);
        await ShouldEqualLegacyAsync(rows, Day.AddDays(-2), Day.AddDays(2), [_visibleGroupId], null, _ => true);
    }

    [Test]
    public async Task SingleDay_WorkSlotsOfOneClient_AreOrderedByStartTime()
    {
        var rows = await ReadWorkSlotsAsync(Day, _visibleGroupId, _clientA, null);

        Signatures(rows).ShouldBe(
        [
            "0|A|2019-03-13|08:00|12:00|False|wA_d_early",
            "0|A|2019-03-13|14:00|18:00|False|wA_d_late",
        ]);
        var legacy = await LegacyQuery(Day, Day, [_visibleGroupId], null)
            .Where(c => c.EntryType == (int)ScheduleEntryType.Work && c.ClientId == _clientA)
            .OrderBy(c => c.StartTime)
            .ToListAsync();
        Signatures(rows).ShouldBe(Signatures(legacy));
    }

    [Test]
    public async Task SingleDay_Breaks_ReturnOnlyTheBreakRowsOfTheDay()
    {
        var rows = await ReadBreaksAsync(Day, _visibleGroupId, null);

        Signatures(rows.Where(c => c.ClientId == _clientA || c.ClientId == _clientB).ToList())
            .ShouldBe(["3|A|2019-03-13|10:00|11:00|False|brA"]);
        await ShouldEqualLegacyAsync(rows, Day, Day, [_visibleGroupId], null,
            c => c.EntryType == (int)ScheduleEntryType.Break);
    }

    private async Task<List<ScheduleCell>> ReadThreeDayWindowAsync(
        List<Guid> clientIds, List<Guid> visibleGroupIds, Guid? analyseToken)
    {
        if (clientIds.Count == 1)
        {
            var clientId = clientIds[0];
            return await _service
                .GetScheduleEntriesQuery(Day.AddDays(-1), Day.AddDays(1), visibleGroupIds, analyseToken)
                .Where(e => e.ClientId == clientId)
                .ToListAsync();
        }

        var first = clientIds[0];
        var second = clientIds[1];
        return await _service
            .GetScheduleEntriesQuery(Day.AddDays(-1), Day.AddDays(1), visibleGroupIds, analyseToken)
            .Where(e => e.ClientId == first || e.ClientId == second)
            .ToListAsync();
    }

    private async Task<List<ScheduleCell>> ReadRangeForClientsAsync(
        DateOnly from, DateOnly until, List<Guid> clientIds, List<Guid>? visibleGroupIds)
    {
        return await _service
            .GetScheduleEntriesQuery(from, until, visibleGroupIds, null)
            .Where(e => clientIds.Contains(e.ClientId))
            .ToListAsync();
    }

    private async Task<List<ScheduleCell>> ReadRangeAsync(
        DateOnly from, DateOnly until, List<Guid> visibleGroupIds, Guid? analyseToken)
    {
        return await _service.GetScheduleEntriesQuery(from, until, visibleGroupIds, analyseToken).ToListAsync();
    }

    private async Task<List<ScheduleCell>> ReadWorkSlotsAsync(DateOnly day, Guid groupId, Guid clientId, Guid? analyseToken)
    {
        return await _service
            .GetScheduleEntriesQuery(day, day, [groupId], analyseToken)
            .Where(c => c.EntryType == (int)ScheduleEntryType.Work && c.ClientId == clientId)
            .OrderBy(c => c.StartTime)
            .ToListAsync();
    }

    private async Task<List<ScheduleCell>> ReadBreaksAsync(DateOnly day, Guid groupId, Guid? analyseToken)
    {
        return await _service
            .GetScheduleEntriesQuery(day, day, new List<Guid> { groupId }, analyseToken)
            .Where(c => c.EntryType == (int)ScheduleEntryType.Break)
            .ToListAsync();
    }

    private IQueryable<ScheduleCell> LegacyQuery(
        DateOnly from, DateOnly until, List<Guid>? visibleGroupIds, Guid? analyseToken)
    {
        var start = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(until.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var groups = visibleGroupIds?.ToArray() ?? [];
        if (analyseToken.HasValue)
        {
            return _context.ScheduleCells.FromSqlInterpolated(
                $"SELECT * FROM get_schedule_entries({start}::DATE, {end}::DATE, {groups}::UUID[], {analyseToken.Value}::UUID)");
        }

        return _context.ScheduleCells.FromSqlInterpolated(
            $"SELECT * FROM get_schedule_entries({start}::DATE, {end}::DATE, {groups}::UUID[])");
    }

    private async Task ShouldEqualLegacyAsync(
        IReadOnlyList<ScheduleCell> actual,
        DateOnly from,
        DateOnly until,
        List<Guid>? visibleGroupIds,
        Guid? analyseToken,
        System.Linq.Expressions.Expression<Func<ScheduleCell, bool>> filter)
    {
        var legacy = await LegacyQuery(from, until, visibleGroupIds, analyseToken).Where(filter).ToListAsync();
        actual.Select(FullSignature).ToList().ShouldBe(legacy.Select(FullSignature).ToList());
    }

    private static string FullSignature(ScheduleCell c) =>
        string.Join('|', c.Id, c.EntryType, c.SourceId, c.ClientId, c.EntryDate.ToString("O"), c.StartTime, c.EndTime,
            c.ChangeTime, c.Surcharges, c.WorkChangeType, c.Description, c.Information, c.Amount, c.ToInvoice,
            c.Taxable, c.EntryId, c.EntryName, c.Abbreviation, c.ReplaceClientId, c.IsReplacementEntry, c.LockLevel,
            c.IsGroupRestricted);

    private List<string> Signatures(IReadOnlyList<ScheduleCell> rows) => rows.Select(Signature).ToList();

    private List<string> SignaturesInClientOrder(IReadOnlyList<ScheduleCell> rows) =>
        rows.Where(c => c.ClientId == _clientA).Concat(rows.Where(c => c.ClientId == _clientB)).Select(Signature).ToList();

    private string Signature(ScheduleCell c) =>
        string.Join('|',
            c.EntryType,
            Alias(c.ClientId),
            c.EntryDate.ToString("yyyy-MM-dd"),
            c.StartTime.ToString(@"hh\:mm"),
            c.EndTime.ToString(@"hh\:mm"),
            c.IsGroupRestricted,
            Alias(c.Id));

    private string Alias(Guid id) => _alias.TryGetValue(id, out var name) ? name : id.ToString();

    private async Task SeedAsync()
    {
        _visibleGroupId = Guid.NewGuid();
        _foreignGroupId = Guid.NewGuid();
        _clientA = Guid.NewGuid();
        _clientB = Guid.NewGuid();
        _dayShiftId = Guid.NewGuid();
        _nightShiftId = Guid.NewGuid();
        _lateShiftId = Guid.NewGuid();
        _absenceId = Guid.NewGuid();
        _token = Guid.NewGuid();
        _alias[_clientA] = "A";
        _alias[_clientB] = "B";

        _context.Group.Add(new Group { Id = _visibleGroupId, Name = Prefix + "Visible", IsDeleted = false });
        _context.Group.Add(new Group { Id = _foreignGroupId, Name = Prefix + "Foreign", IsDeleted = false });
        _context.Client.Add(new Client { Id = _clientA, Name = Prefix + "A", FirstName = "Integration", IsDeleted = false });
        _context.Client.Add(new Client { Id = _clientB, Name = Prefix + "B", FirstName = "Integration", IsDeleted = false });
        _context.Shift.Add(NewShift(_dayShiftId, "Day", new TimeOnly(8, 0), new TimeOnly(16, 0)));
        _context.Shift.Add(NewShift(_nightShiftId, "Night", new TimeOnly(22, 0), new TimeOnly(6, 0)));
        _context.Shift.Add(NewShift(_lateShiftId, "Late", new TimeOnly(14, 0), new TimeOnly(18, 0)));
        _context.Absence.Add(new Absence
        {
            Id = _absenceId,
            Name = new MultiLanguage { De = Prefix + "Absence", En = Prefix + "Absence" },
            Abbreviation = new MultiLanguage { De = "T", En = "T" },
            Description = new MultiLanguage { De = Prefix + "Absence", En = Prefix + "Absence" },
            Color = "#000000",
            IsDeleted = false
        });
        await _context.SaveChangesAsync();

        AddGroupItem(new GroupItem { Id = Guid.NewGuid(), ClientId = _clientA, GroupId = _visibleGroupId, IsDeleted = false });
        AddGroupItem(new GroupItem { Id = Guid.NewGuid(), ShiftId = _dayShiftId, GroupId = _visibleGroupId, IsDeleted = false });
        AddGroupItem(new GroupItem { Id = Guid.NewGuid(), ShiftId = _nightShiftId, GroupId = _foreignGroupId, IsDeleted = false });

        var nightBefore = AddWork("wA_m2", _clientA, _nightShiftId, Day.AddDays(-2), new TimeOnly(22, 0), new TimeOnly(6, 0), null);
        var dayLate = AddWork("wA_d_late", _clientA, _lateShiftId, Day, new TimeOnly(14, 0), new TimeOnly(18, 0), null);
        var dayEarly = AddWork("wA_d_early", _clientA, _dayShiftId, Day, new TimeOnly(8, 0), new TimeOnly(12, 0), null);
        var nightAfter = AddWork("wA_p1", _clientA, _nightShiftId, Day.AddDays(1), new TimeOnly(22, 0), new TimeOnly(6, 0), null);
        AddWork("wA_p2", _clientA, _dayShiftId, Day.AddDays(2), new TimeOnly(8, 0), new TimeOnly(16, 0), null);
        AddWork("wA_tok", _clientA, _dayShiftId, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), _token);
        AddWork("wB_d", _clientB, _dayShiftId, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), null);
        AddWork("wB_m1", _clientB, _lateShiftId, Day.AddDays(-1), new TimeOnly(14, 0), new TimeOnly(18, 0), null);
        await _context.SaveChangesAsync();

        AddCorrectionEnd("wcA_m2", nightBefore);
        AddCorrectionEnd("wcA_p1", nightAfter);
        AddBreak("brA", _clientA, Day, new TimeOnly(10, 0), new TimeOnly(11, 0));
        AddBreak("brB", _clientB, Day.AddDays(1), new TimeOnly(9, 0), new TimeOnly(10, 0));
        var expenseId = Guid.NewGuid();
        _alias[expenseId] = "exA";
        _expenseIds.Add(expenseId);
        _context.Expenses.Add(new Expenses
        {
            Id = expenseId,
            WorkId = dayEarly,
            Amount = 10m,
            Description = Prefix + "Expense",
            IsDeleted = false
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        _ = dayLate;
    }

    private static Shift NewShift(Guid id, string name, TimeOnly start, TimeOnly end) => new()
    {
        Id = id,
        Name = Prefix + name,
        StartShift = start,
        EndShift = end,
        IsDeleted = false
    };

    private void AddGroupItem(GroupItem item)
    {
        _groupItemIds.Add(item.Id);
        _context.GroupItem.Add(item);
    }

    private Guid AddWork(string alias, Guid clientId, Guid shiftId, DateOnly day, TimeOnly start, TimeOnly end, Guid? token)
    {
        var id = Guid.NewGuid();
        _alias[id] = alias;
        _workIds.Add(id);
        _context.Work.Add(new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = day,
            WorkTime = (decimal)(end - start).TotalMinutes,
            StartTime = start,
            EndTime = end,
            AnalyseToken = token,
            IsDeleted = false
        });
        return id;
    }

    private void AddCorrectionEnd(string alias, Guid workId)
    {
        var id = Guid.NewGuid();
        _alias[id] = alias;
        _workChangeIds.Add(id);
        _context.WorkChange.Add(new WorkChange
        {
            Id = id,
            WorkId = workId,
            Type = WorkChangeType.CorrectionEnd,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(6, 30),
            ChangeTime = 30,
            ToInvoice = false,
            Description = Prefix + "CorrectionEnd",
            IsDeleted = false
        });
    }

    private void AddBreak(string alias, Guid clientId, DateOnly day, TimeOnly start, TimeOnly end)
    {
        var id = Guid.NewGuid();
        _alias[id] = alias;
        _breakIds.Add(id);
        _context.Break.Add(new Break
        {
            Id = id,
            ClientId = clientId,
            AbsenceId = _absenceId,
            CurrentDate = day,
            WorkTime = (decimal)(end - start).TotalMinutes,
            StartTime = start,
            EndTime = end,
            IsDeleted = false
        });
    }
}
