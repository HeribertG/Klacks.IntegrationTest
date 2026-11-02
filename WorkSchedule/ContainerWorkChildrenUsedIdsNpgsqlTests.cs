// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proof for the query that tells a container save which ids of new children are taken already: rows of the
/// work, break and work change tables answer, deleted rows too, and an id is never reported from the wrong table. The unit
/// tests run on the in-memory provider only. Every row carries an INTEGRATION_TEST_ prefixed key, the cleanup deletes
/// nothing else.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
[Category("RealDatabase")]
public class ContainerWorkChildrenUsedIdsNpgsqlTests
{
    private const string TestPrefix = "INTEGRATION_TEST_USEDIDS_";
    private static readonly DateOnly Day = new(2099, 8, 12);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ContainerWorkChildrenReadRepository _repository = null!;
    private Guid _workId;
    private Guid _deletedWorkId;
    private Guid _breakId;
    private Guid _workChangeId;

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
        _repository = new ContainerWorkChildrenReadRepository(_context);
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var absenceId = Guid.NewGuid();
        _workId = Guid.NewGuid();
        _deletedWorkId = Guid.NewGuid();
        _breakId = Guid.NewGuid();
        _workChangeId = Guid.NewGuid();

        _context.Client.Add(new Client { Id = clientId, Name = TestPrefix + "A", FirstName = "Test", Company = string.Empty });
        _context.Shift.Add(new Shift
        {
            Id = shiftId, Name = TestPrefix + "SHIFT", Abbreviation = "UID", Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1), StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0),
            ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 8m,
        });
        _context.Absence.Add(new Absence
        {
            Id = absenceId,
            Name = new Klacks.Api.Domain.Common.MultiLanguage { De = TestPrefix + "ABSENCE" },
            Abbreviation = new Klacks.Api.Domain.Common.MultiLanguage { De = "UID" },
            Description = new Klacks.Api.Domain.Common.MultiLanguage { De = "used ids" },
            Color = "#cccccc",
        });
        await _context.SaveChangesAsync();
        _context.Work.AddRange(
            NewWork(_workId, clientId, shiftId, false),
            NewWork(_deletedWorkId, clientId, shiftId, true));
        _context.Break.Add(new Break
        {
            Id = _breakId, ClientId = clientId, AbsenceId = absenceId, CurrentDate = Day, WorkTime = 1m,
            StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(13, 0), Information = TestPrefix + "BREAK",
        });
        await _context.SaveChangesAsync();
        _context.WorkChange.Add(new WorkChange
        {
            Id = _workChangeId, WorkId = _workId, Type = WorkChangeType.Debriefing, ChangeTime = 1m,
            Description = TestPrefix + "CHANGE",
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task TakenIds_AreReportedPerTable_DeletedRowsIncluded()
    {
        var free = Guid.NewGuid();

        var used = await _repository.GetUsedEntryIds(
            [_workId, _deletedWorkId, free], [_breakId, free], [_workChangeId, free], CancellationToken.None);

        used.Works.ShouldBe([_workId, _deletedWorkId], ignoreOrder: true);
        used.Breaks.ShouldBe([_breakId]);
        used.WorkChanges.ShouldBe([_workChangeId]);
    }

    [Test]
    public async Task AnIdOfAnotherTable_IsNotReported()
    {
        var used = await _repository.GetUsedEntryIds([_breakId], [_workId], [_workId], CancellationToken.None);

        used.Works.ShouldBeEmpty();
        used.Breaks.ShouldBeEmpty();
        used.WorkChanges.ShouldBeEmpty();
    }

    [Test]
    public async Task NoIds_AskNothing()
    {
        var used = await _repository.GetUsedEntryIds([], [], [], CancellationToken.None);

        used.Works.ShouldBeEmpty();
        used.Breaks.ShouldBeEmpty();
        used.WorkChanges.ShouldBeEmpty();
    }

    private static Work NewWork(Guid id, Guid clientId, Guid shiftId, bool isDeleted) => new()
    {
        Id = id, ClientId = clientId, ShiftId = shiftId, CurrentDate = Day, WorkTime = 8m,
        StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), Information = TestPrefix + "WORK", IsDeleted = isDeleted,
    };

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
            DELETE FROM work WHERE information LIKE '{TestPrefix}%';
            DELETE FROM absence WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
