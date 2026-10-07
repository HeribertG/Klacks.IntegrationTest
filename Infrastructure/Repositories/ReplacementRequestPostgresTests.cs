// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// PostgreSQL proof for the replacement request book: the partial unique index on (scenario token, candidate,
/// shift, date) rejects a second live row but not one next to a soft-deleted row, real-plan rows (token null)
/// collide as well (NULLS NOT DISTINCT), a contact upsert that loses an insert race records its answer on the winning row,
/// the repository queries and the bulk retention delete translate on Npgsql, and accepting a
/// scenario stamps the row whose replacement WorkChange is promoted (clone shift mapped to its source). Applies
/// pending migrations first; every row it writes carries ids of its own and is removed by those ids.
/// </summary>

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class ReplacementRequestPostgresTests
{
    private const string NamePrefix = "INTEGRATION_TEST_ReplacementRequest_";
    private static readonly DateOnly Day = new(2026, 7, 14);

    private DataBaseContext _context = null!;
    private ReplacementRequestRepository _repository = null!;
    private readonly List<Guid> _rowIds = [];
    private readonly List<Guid> _clientIds = [];
    private readonly List<Guid> _shiftIds = [];
    private readonly List<Guid> _workIds = [];
    private readonly List<Guid> _workChangeIds = [];

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await _context.Database.MigrateAsync();
        _repository = new ReplacementRequestRepository(_context, Substitute.For<ILogger<ReplacementRequest>>());
    }

    [TearDown]
    public async Task TearDown()
    {
        _context.ChangeTracker.Clear();
        await _context.ReplacementRequests.IgnoreQueryFilters().Where(r => _rowIds.Contains(r.Id)).ExecuteDeleteAsync();
        await _context.WorkChange.IgnoreQueryFilters().Where(c => _workChangeIds.Contains(c.Id)).ExecuteDeleteAsync();
        await _context.Work.IgnoreQueryFilters().Where(w => _workIds.Contains(w.Id)).ExecuteDeleteAsync();
        await _context.Shift.IgnoreQueryFilters().Where(s => _shiftIds.Contains(s.Id)).ExecuteDeleteAsync();
        await _context.Client.IgnoreQueryFilters().Where(c => _clientIds.Contains(c.Id)).ExecuteDeleteAsync();
        _context.Dispose();
    }

    [Test]
    public async Task UniqueIndex_RejectsASecondLiveRowForTheSameScenarioSlot()
    {
        var token = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        await AddAndSaveAsync(Row(token, candidateId, shiftId));

        _context.ReplacementRequests.Add(Track(Row(token, candidateId, shiftId)));

        await Should.ThrowAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    [Test]
    public async Task UniqueIndex_AllowsANewRowNextToASoftDeletedOne()
    {
        var token = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var deleted = Row(token, candidateId, shiftId);
        deleted.IsDeleted = true;
        deleted.DeletedTime = DateTime.UtcNow;
        await AddAndSaveAsync(deleted);

        await AddAndSaveAsync(Row(token, candidateId, shiftId));

        (await _context.ReplacementRequests.CountAsync(r => _rowIds.Contains(r.Id))).ShouldBe(1);
    }

    [Test]
    public async Task UniqueIndex_RejectsADuplicateRealPlanRow_NullsAreNotDistinct()
    {
        var candidateId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        await AddAndSaveAsync(Row(null, candidateId, shiftId));

        _context.ReplacementRequests.Add(Track(Row(null, candidateId, shiftId)));

        await Should.ThrowAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    [Test]
    public async Task RecordContact_LosingAnInsertRace_RecordsTheAnswerOnTheWinningRow()
    {
        var absent = await AddClientAsync("RaceAbsent");
        var candidate = await AddClientAsync("RaceCandidate");
        var shiftId = await AddShiftAsync("RaceShift", null, null);
        var winner = Row(null, candidate, shiftId);
        winner.AbsentClientId = absent;
        await AddAndSaveAsync(winner);
        _context.ChangeTracker.Clear();

        var racingRepository = new FirstLookupMissesRepository(_repository);
        var handler = new RecordReplacementContactCommandHandler(
            racingRepository,
            Substitute.For<IReplacementContactValidator>(),
            new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>()),
            Substitute.For<ISettingsReader>(),
            UtcClock(),
            TimeProvider.System,
            Substitute.For<IHttpContextAccessor>());

        var result = await handler.Handle(
            new RecordReplacementContactCommand(new RecordReplacementContactRequest(
                absent, candidate, shiftId, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), null, null, null,
                ReplacementRequestOutcome.Declined)),
            CancellationToken.None);
        _context.ChangeTracker.Clear();

        result.Id.ShouldBe(winner.Id);
        (await _context.ReplacementRequests.SingleAsync(r => r.Id == winner.Id)).Outcome.ShouldBe(ReplacementRequestOutcome.Declined);
        (await _context.ReplacementRequests.CountAsync(r => r.CandidateClientId == candidate)).ShouldBe(1);
    }
    [Test]
    public async Task RepositoryQueries_TranslateOnPostgres()
    {
        var token = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var row = Row(token, candidateId, shiftId);
        await AddAndSaveAsync(row);
        _context.ChangeTracker.Clear();

        var found = await _repository.FindLiveAsync(token, candidateId, shiftId, Day);
        var listed = await _repository.ListAsync(new ReplacementRequestFilter(row.AbsentClientId, Day, Day, token), ReplacementRequestLimits.MaxListRows);

        found.ShouldNotBeNull().Id.ShouldBe(row.Id);
        listed.ShouldHaveSingleItem().Id.ShouldBe(row.Id);
    }

    [Test]
    public async Task DeleteReportedBefore_PhysicallyRemovesOnlyRowsReportedBeforeTheCutoff()
    {
        var now = new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);
        var old = Row(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        old.ReportedAtUtc = now.AddDays(-800);
        var recent = Row(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        recent.ReportedAtUtc = now.AddDays(-10);
        await AddAndSaveAsync(old);
        await AddAndSaveAsync(recent);

        await _repository.DeleteReportedBeforeAsync(now.AddDays(-730));
        _context.ChangeTracker.Clear();

        var remaining = await _context.ReplacementRequests.IgnoreQueryFilters()
            .Where(r => r.Id == old.Id || r.Id == recent.Id)
            .Select(r => r.Id)
            .ToListAsync();
        remaining.ShouldBe([recent.Id]);
    }

    [Test]
    public async Task Promote_StampsTheRowWhoseReplacementIsPromoted()
    {
        var token = Guid.NewGuid();
        var absent = await AddClientAsync("Absent");
        var candidate = await AddClientAsync("Candidate");
        var sourceShift = await AddShiftAsync("Source", null, null);
        var cloneShift = await AddShiftAsync("Clone", token, sourceShift);
        var workId = Guid.NewGuid();
        var workChangeId = Guid.NewGuid();
        _workIds.Add(workId);
        _workChangeIds.Add(workChangeId);
        _context.Work.Add(new Work
        {
            Id = workId,
            ClientId = absent,
            ShiftId = cloneShift,
            CurrentDate = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token
        });
        _context.WorkChange.Add(new WorkChange
        {
            Id = workChangeId,
            WorkId = workId,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = candidate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            ChangeTime = 8m,
            AnalyseToken = token
        });
        var row = Row(token, candidate, sourceShift);
        row.AbsentClientId = absent;
        _context.ReplacementRequests.Add(Track(row));
        await _context.SaveChangesAsync();

        await new AnalyseScenarioService(_context).PromoteScenarioWorksAsync(token, Day, Day, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var stamped = await _context.ReplacementRequests.SingleAsync(r => r.Id == row.Id);
        stamped.AppliedAtUtc.ShouldNotBeNull();
        stamped.WorkChangeId.ShouldBe(workChangeId);
    }

    [Test]
    public async Task ManualReplacementInScenario_StoresTheSourceShift_AndIsStampedOnAccept()
    {
        var token = Guid.NewGuid();
        var absent = await AddClientAsync("ManualAbsent");
        var candidate = await AddClientAsync("ManualCandidate");
        var sourceShift = await AddShiftAsync("ManualSource", null, null);
        var cloneShift = await AddShiftAsync("ManualClone", token, sourceShift);
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = absent,
            ShiftId = cloneShift,
            CurrentDate = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token
        };
        _workIds.Add(work.Id);
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        var change = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementStart,
            ReplaceClientId = candidate,
            ChangeTime = 4m,
            AnalyseToken = token
        };
        _workChangeIds.Add(change.Id);
        var recorder = NewRecorder();

        _context.WorkChange.Add(change);
        await recorder.RecordManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();
        _rowIds.AddRange(await _context.ReplacementRequests.Where(r => r.AnalyseToken == token).Select(r => r.Id).ToListAsync());

        await new AnalyseScenarioService(_context).PromoteScenarioWorksAsync(token, Day, Day, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var row = await _context.ReplacementRequests.SingleAsync(r => r.AnalyseToken == token);
        row.ShiftId.ShouldBe(sourceShift);
        row.Source.ShouldBe(ReplacementRequestSource.ManualReplacement);
        row.Outcome.ShouldBe(ReplacementRequestOutcome.Accepted);
        row.StartTime.ShouldBe(new TimeOnly(8, 0));
        row.EndTime.ShouldBe(new TimeOnly(12, 0));
        row.AppliedAtUtc.ShouldNotBeNull();
        row.WorkChangeId.ShouldBe(change.Id);
    }

    private static ICompanyClock UtcClock()
    {
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTimeZoneAsync(Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        return clock;
    }

    private ReplacementRequestRecorder NewRecorder()
    {
        var shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            Substitute.For<IShiftQueryPipelineService>(),
            Substitute.For<IShiftGroupManagementService>(),
            new EntityCollectionUpdateService(_context),
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper(),
            Substitute.For<ICompanyClock>());
        var clock = UtcClock();

        return new ReplacementRequestRecorder(
            _repository,
            shiftRepository,
            Substitute.For<IReplacementContactPhoneResolver>(),
            clock,
            TimeProvider.System,
            Substitute.For<IHttpContextAccessor>());
    }
    private ReplacementRequest Track(ReplacementRequest row)
    {
        _rowIds.Add(row.Id);
        return row;
    }

    private async Task AddAndSaveAsync(ReplacementRequest row)
    {
        _context.ReplacementRequests.Add(Track(row));
        await _context.SaveChangesAsync();
    }

    private async Task<Guid> AddClientAsync(string suffix)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = NamePrefix + suffix, FirstName = "Replacement" };
        _clientIds.Add(client.Id);
        _context.Client.Add(client);
        await _context.SaveChangesAsync();
        return client.Id;
    }

    private async Task<Guid> AddShiftAsync(string suffix, Guid? token, Guid? sourceShiftId)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = NamePrefix + suffix,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AnalyseToken = token,
            ScenarioSourceShiftId = sourceShiftId
        };
        _shiftIds.Add(shift.Id);
        _context.Shift.Add(shift);
        await _context.SaveChangesAsync();
        return shift.Id;
    }

    private static ReplacementRequest Row(Guid? token, Guid candidateId, Guid shiftId) => new()
    {
        Id = Guid.NewGuid(),
        AbsentClientId = Guid.NewGuid(),
        CandidateClientId = candidateId,
        ShiftId = shiftId,
        Date = Day,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        Source = ReplacementRequestSource.RecoveryEngine,
        Outcome = ReplacementRequestOutcome.Proposed,
        ReportedAtUtc = new DateTime(2026, 7, 14, 4, 0, 0, DateTimeKind.Utc),
        ShiftStartUtc = new DateTime(2026, 7, 14, 6, 0, 0, DateTimeKind.Utc),
        AnalyseToken = token
    };
}
