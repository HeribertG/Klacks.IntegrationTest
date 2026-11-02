// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proof for the neighbour-day queries of the live single check: the works of the day before and the
/// breaks of the two days before and the day after the checked day, with the changes of those works, are loaded for the same client
/// and scenario only;
/// deleted rows, container children and rows two days away stay out. The unit tests run on the in-memory provider only.
/// Every row carries an INTEGRATION_TEST_ prefixed key, the cleanup deletes nothing else.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.ScheduleTimeline;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
[Category("RealDatabase")]
public class ScheduleTimelineCheckLoaderNeighbourNpgsqlTests
{
    private const string TestPrefix = "INTEGRATION_TEST_CHKNEIGH_";
    private const decimal ShiftHours = 8m;
    private const decimal CorrectionHours = 1m;

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ScheduleTimelineCheckLoader _loader = null!;
    private Guid _clientId;
    private Guid _otherClientId;
    private Guid _shiftId;
    private Guid _absenceId;
    private Guid _scenarioToken;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _loader = new ScheduleTimelineCheckLoader(_context, Substitute.For<ITimelineCalculationService>());
        _clientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _absenceId = Guid.NewGuid();
        _scenarioToken = Guid.NewGuid();

        _context.Client.AddRange(
            new Client { Id = _clientId, Name = TestPrefix + "A", FirstName = "Test", Company = string.Empty },
            new Client { Id = _otherClientId, Name = TestPrefix + "B", FirstName = "Test", Company = string.Empty });
        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TCN",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(22, 0),
            EndShift = new TimeOnly(6, 0),
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = ShiftHours,
        });
        _context.Absence.Add(new Absence
        {
            Id = _absenceId,
            Name = new Klacks.Api.Domain.Common.MultiLanguage { De = TestPrefix + "ABSENCE" },
            Abbreviation = new Klacks.Api.Domain.Common.MultiLanguage { De = "TCN" },
            Description = new Klacks.Api.Domain.Common.MultiLanguage { De = "neighbour absence" },
            Color = "#cccccc",
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [TestCase(2099, 8, 12)]
    [TestCase(2099, 11, 1)]
    [TestCase(2099, 10, 31)]
    [TestCase(2099, 12, 31)]
    [TestCase(2100, 1, 1)]
    public async Task Neighbourhood_AddsTheWorksOfTheDayBeforeAndTheBreaksOfTheDaysAround(int year, int month, int day)
    {
        var date = new DateOnly(year, month, day);
        var ownWork = await AddWorkAsync(_clientId, date);
        var previousWork = await AddWorkAsync(_clientId, date.AddDays(-1));
        var previousBreak = await AddBreakAsync(_clientId, date.AddDays(-1));
        var nextBreak = await AddBreakAsync(_clientId, date.AddDays(1));
        var previousChange = await AddChangeAsync(previousWork);

        var rows = await LoadAsync(date, null);

        rows.OwnWorks.Select(w => w.Id).ShouldBe([ownWork]);
        rows.Works.Select(w => w.Id).ShouldBe([ownWork, previousWork], ignoreOrder: true);
        rows.Breaks.Select(b => b.Id).ShouldBe([previousBreak, nextBreak], ignoreOrder: true);
        rows.WorkChanges.Select(c => c.Id).ShouldBe([previousChange]);
    }

    [Test]
    public async Task Neighbourhood_KeepsTheRowsOfTheCheckedDay()
    {
        var date = new DateOnly(2099, 8, 12);
        var ownBreak = await AddBreakAsync(_clientId, date);
        var ownWork = await AddWorkAsync(_clientId, date);
        var ownChange = await AddChangeAsync(ownWork);

        var rows = await LoadAsync(date, null);

        rows.Works.Select(w => w.Id).ShouldContain(ownWork);
        rows.Breaks.Select(b => b.Id).ShouldContain(ownBreak);
        rows.WorkChanges.Select(c => c.Id).ShouldContain(ownChange);
    }

    [Test]
    public async Task Neighbourhood_AddsTheBreakTwoDaysBefore()
    {
        var date = new DateOnly(2099, 8, 12);
        var breakTwoDaysBefore = await AddBreakAsync(_clientId, date.AddDays(-2));

        var rows = await LoadAsync(date, null);

        rows.Breaks.Select(b => b.Id).ShouldBe([breakTwoDaysBefore]);
    }

    [Test]
    public async Task Neighbourhood_LeavesOutRowsFurtherAwayAndWorksOfTheDayAfterAndTwoDaysBefore()
    {
        var date = new DateOnly(2099, 8, 12);
        await AddWorkAsync(_clientId, date.AddDays(-2));
        await AddWorkAsync(_clientId, date.AddDays(1));
        await AddBreakAsync(_clientId, date.AddDays(2));
        await AddBreakAsync(_clientId, date.AddDays(-3));

        var rows = await LoadAsync(date, null);

        rows.Works.ShouldBeEmpty();
        rows.Breaks.ShouldBeEmpty();
    }

    [Test]
    public async Task Neighbourhood_DoesNotLoadTheCheckedDayTwice()
    {
        var date = new DateOnly(2099, 8, 12);
        var ownBreak = await AddBreakAsync(_clientId, date);

        var rows = await LoadAsync(date, null);

        rows.Breaks.Select(b => b.Id).ShouldBe([ownBreak]);
    }

    [Test]
    public async Task Neighbourhood_AddsTheWorkOfTheDayBeforeTheClientReplacesOn()
    {
        var date = new DateOnly(2099, 8, 12);
        var replacedWork = await AddWorkAsync(_otherClientId, date.AddDays(-1));
        var replacement = await AddChangeAsync(replacedWork, replaceClientId: _clientId);

        var rows = await LoadAsync(date, null);

        rows.Works.Select(w => w.Id).ShouldBe([replacedWork]);
        rows.WorkChanges.Select(c => c.Id).ShouldBe([replacement]);
        rows.OwnWorks.ShouldBeEmpty();
    }

    [Test]
    public async Task Neighbourhood_LeavesOutWorksReplacedByAnotherClientAndDeletedOrScenarioReplacements()
    {
        var date = new DateOnly(2099, 8, 12);
        var thirdClient = Guid.NewGuid();
        _context.Client.Add(new Client { Id = thirdClient, Name = TestPrefix + "C", FirstName = "Test", Company = string.Empty });
        await _context.SaveChangesAsync();
        var replacedByThird = await AddWorkAsync(_otherClientId, date.AddDays(-1));
        await AddChangeAsync(replacedByThird, replaceClientId: thirdClient);
        var deletedReplacement = await AddWorkAsync(_otherClientId, date.AddDays(-1));
        await AddChangeAsync(deletedReplacement, replaceClientId: _clientId, isDeleted: true);
        var scenarioWork = await AddWorkAsync(_otherClientId, date.AddDays(-1), analyseToken: _scenarioToken);
        await AddChangeAsync(scenarioWork, replaceClientId: _clientId);
        var dayTwoBefore = await AddWorkAsync(_otherClientId, date.AddDays(-2));
        await AddChangeAsync(dayTwoBefore, replaceClientId: _clientId);

        var rows = await LoadAsync(date, null);

        rows.Works.ShouldBeEmpty();
    }

    [Test]
    public async Task Neighbourhood_ListsAWorkOnceThatTheClientOwnsAndReplacesOn()
    {
        var date = new DateOnly(2099, 8, 12);
        var work = await AddWorkAsync(_clientId, date.AddDays(-1));
        await AddChangeAsync(work, replaceClientId: _clientId);

        var rows = await LoadAsync(date, null);

        rows.Works.Select(w => w.Id).ShouldBe([work]);
    }

    [Test]
    public async Task Neighbourhood_LeavesOutOtherClients()
    {
        var date = new DateOnly(2099, 8, 12);
        await AddWorkAsync(_otherClientId, date.AddDays(-1));
        await AddBreakAsync(_otherClientId, date.AddDays(1));

        var rows = await LoadAsync(date, null);

        rows.Works.ShouldBeEmpty();
        rows.Breaks.ShouldBeEmpty();
    }

    [Test]
    public async Task Neighbourhood_LeavesOutDeletedRowsAndContainerChildren()
    {
        var date = new DateOnly(2099, 8, 12);
        var previousWork = await AddWorkAsync(_clientId, date.AddDays(-1));
        await AddWorkAsync(_clientId, date.AddDays(-1), isDeleted: true);
        await AddWorkAsync(_clientId, date.AddDays(-1), parentWorkId: previousWork);
        var nextBreak = await AddBreakAsync(_clientId, date.AddDays(1));
        await AddBreakAsync(_clientId, date.AddDays(1), isDeleted: true);
        await AddBreakAsync(_clientId, date.AddDays(-1), isDeleted: true);
        await AddBreakAsync(_clientId, date.AddDays(1), parentWorkId: previousWork);
        var deletedChange = await AddChangeAsync(previousWork, isDeleted: true);

        var rows = await LoadAsync(date, null);

        rows.Works.Select(w => w.Id).ShouldBe([previousWork]);
        rows.Breaks.Select(b => b.Id).ShouldBe([nextBreak]);
        rows.WorkChanges.Select(c => c.Id).ShouldNotContain(deletedChange);
    }

    [Test]
    public async Task Neighbourhood_OfTheRealPlan_LeavesOutScenarioRows()
    {
        var date = new DateOnly(2099, 8, 12);
        var previousWork = await AddWorkAsync(_clientId, date.AddDays(-1));
        await AddWorkAsync(_clientId, date.AddDays(-1), analyseToken: _scenarioToken);
        var nextBreak = await AddBreakAsync(_clientId, date.AddDays(1));
        await AddBreakAsync(_clientId, date.AddDays(1), analyseToken: _scenarioToken);

        var rows = await LoadAsync(date, null);

        rows.Works.Select(w => w.Id).ShouldBe([previousWork]);
        rows.Breaks.Select(b => b.Id).ShouldBe([nextBreak]);
    }

    [Test]
    public async Task Neighbourhood_OfAScenario_LeavesOutRealRows()
    {
        var date = new DateOnly(2099, 8, 12);
        await AddWorkAsync(_clientId, date.AddDays(-1));
        var scenarioWork = await AddWorkAsync(_clientId, date.AddDays(-1), analyseToken: _scenarioToken);
        await AddBreakAsync(_clientId, date.AddDays(1));
        var scenarioBreak = await AddBreakAsync(_clientId, date.AddDays(1), analyseToken: _scenarioToken);

        var rows = await LoadAsync(date, _scenarioToken);

        rows.Works.Select(w => w.Id).ShouldBe([scenarioWork]);
        rows.Breaks.Select(b => b.Id).ShouldBe([scenarioBreak]);
    }

    private async Task<TimelineCheckRows> LoadAsync(DateOnly date, Guid? analyseToken)
    {
        var dayRows = await _loader.LoadDayAsync(_clientId, date, analyseToken, CancellationToken.None);
        return await _loader.LoadNeighbourhoodAsync(_clientId, date, dayRows, analyseToken, CancellationToken.None);
    }

    private async Task<Guid> AddWorkAsync(
        Guid clientId, DateOnly date, bool isDeleted = false, Guid? parentWorkId = null, Guid? analyseToken = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = _shiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(22, 0),
            EndTime = new TimeOnly(6, 0),
            WorkTime = ShiftHours,
            Information = TestPrefix + "WORK",
            IsDeleted = isDeleted,
            ParentWorkId = parentWorkId,
            AnalyseToken = analyseToken,
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        return work.Id;
    }

    private async Task<Guid> AddBreakAsync(
        Guid clientId, DateOnly date, bool isDeleted = false, Guid? parentWorkId = null, Guid? analyseToken = null)
    {
        var entry = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = _absenceId,
            CurrentDate = date,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            WorkTime = ShiftHours,
            Information = TestPrefix + "BREAK",
            IsDeleted = isDeleted,
            ParentWorkId = parentWorkId,
            AnalyseToken = analyseToken,
        };
        _context.Break.Add(entry);
        await _context.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<Guid> AddChangeAsync(Guid workId, bool isDeleted = false, Guid? replaceClientId = null)
    {
        var change = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = replaceClientId.HasValue ? WorkChangeType.ReplacementWithin : WorkChangeType.Debriefing,
            ReplaceClientId = replaceClientId,
            StartTime = new TimeOnly(22, 0),
            EndTime = new TimeOnly(6, 0),
            ChangeTime = CorrectionHours,
            Description = TestPrefix + "CHANGE",
            IsDeleted = isDeleted,
        };
        _context.WorkChange.Add(change);
        await _context.SaveChangesAsync();
        return change.Id;
    }

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM work_change WHERE description LIKE '{TestPrefix}%';
            DELETE FROM ""break"" WHERE information LIKE '{TestPrefix}%';
            DELETE FROM work WHERE parent_work_id IS NOT NULL AND information LIKE '{TestPrefix}%';
            DELETE FROM work WHERE information LIKE '{TestPrefix}%';
            DELETE FROM absence WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
