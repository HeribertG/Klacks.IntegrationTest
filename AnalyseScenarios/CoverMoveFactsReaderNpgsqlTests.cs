// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proof for CoverMoveFactsReader: the facts of a scenario work against Npgsql, a live replacement on the work
/// or a sub-work, a sealed sub-work or sub-break, deleted rows and works outside the scenario. Every row carries an
/// INTEGRATION_TEST_ prefixed key, the cleanup deletes nothing else.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class CoverMoveFactsReaderNpgsqlTests
{
    private const string TestPrefix = "INTEGRATION_TEST_COVERMOVE_";
    private static readonly DateOnly Day = new(2099, 9, 7);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private CoverMoveFactsReader _reader = null!;
    private readonly Guid _token = Guid.NewGuid();
    private Guid _clientId;
    private Guid _shiftId;
    private Guid _absenceId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        var migrationOptions = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var migrationContext = new DataBaseContext(migrationOptions, Substitute.For<IHttpContextAccessor>()))
        {
            await migrationContext.Database.MigrateAsync();
        }

        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _reader = new CoverMoveFactsReader(_context);
        _clientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TCM",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        });
        _context.Client.Add(new Client { Id = _clientId, Name = TestPrefix + "A", FirstName = "Test", Company = string.Empty });
        _absenceId = await _context.Absence.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Id).FirstAsync();
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task AnUntouchedWork_ReadsWithItsOwnFacts()
    {
        var work = await AddWorkAsync(_token);

        var facts = await LoadAsync(work.Id);

        facts.ClientId.ShouldBe(_clientId);
        facts.ParentWorkId.ShouldBeNull();
        facts.StartTime.ShouldBe(work.StartTime);
        facts.EndTime.ShouldBe(work.EndTime);
        facts.LockLevel.ShouldBe(WorkLockLevel.None);
        facts.HasReplacement.ShouldBeFalse();
        facts.HasLockedChild.ShouldBeFalse();
    }

    [Test]
    public async Task AWorkOutsideTheScenario_IsLeftOut()
    {
        var work = await AddWorkAsync(null);

        (await _reader.LoadAsync([work.Id], _token, CancellationToken.None)).ShouldBeEmpty();
    }

    [Test]
    public async Task AReplacementOnTheWork_Counts()
    {
        var work = await AddWorkAsync(_token);
        await AddReplacementAsync(work.Id);

        (await LoadAsync(work.Id)).HasReplacement.ShouldBeTrue();
    }

    [Test]
    public async Task AReplacementOnASubWork_Counts()
    {
        var work = await AddWorkAsync(_token);
        var subWork = await AddWorkAsync(_token, work.Id);
        await AddReplacementAsync(subWork.Id);

        (await LoadAsync(work.Id)).HasReplacement.ShouldBeTrue();
    }

    [Test]
    public async Task ADeletedReplacement_DoesNotCount()
    {
        var work = await AddWorkAsync(_token);
        var change = await AddReplacementAsync(work.Id);
        change.IsDeleted = true;
        await _context.SaveChangesAsync();

        (await LoadAsync(work.Id)).HasReplacement.ShouldBeFalse();
    }

    [Test]
    public async Task ASealedSubWork_Counts()
    {
        var work = await AddWorkAsync(_token);
        await AddWorkAsync(_token, work.Id, WorkLockLevel.Confirmed);

        (await LoadAsync(work.Id)).HasLockedChild.ShouldBeTrue();
    }

    [Test]
    public async Task ASealedSubBreak_Counts()
    {
        var work = await AddWorkAsync(_token);
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            AbsenceId = _absenceId,
            CurrentDate = Day,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            ParentWorkId = work.Id,
            AnalyseToken = _token,
            LockLevel = WorkLockLevel.Confirmed
        });
        await _context.SaveChangesAsync();

        (await LoadAsync(work.Id)).HasLockedChild.ShouldBeTrue();
    }

    private async Task<CoverMoveFacts> LoadAsync(Guid workId)
        => (await _reader.LoadAsync([workId], _token, CancellationToken.None))[workId];

    private async Task<Work> AddWorkAsync(Guid? token, Guid? parentId = null, WorkLockLevel lockLevel = WorkLockLevel.None)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token,
            ParentWorkId = parentId,
            LockLevel = lockLevel
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        return work;
    }

    private async Task<WorkChange> AddReplacementAsync(Guid workId)
    {
        var change = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = WorkChangeType.ReplacementWithin,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(12, 0),
            ChangeTime = 4m,
            ReplaceClientId = _clientId,
            Description = TestPrefix + "REPLACEMENT",
            AnalyseToken = _token
        };
        _context.WorkChange.Add(change);
        await _context.SaveChangesAsync();
        return change;
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
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%') AND parent_work_id IS NOT NULL;
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
