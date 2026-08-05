// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the apply transaction (P6, 2026-08-05). Creating the scenario row, cloning the
/// schedule into it and materialising the result used to be separate saves: a failure in between left a
/// scenario whose schedule was half-written and indistinguishable from a finished one. These tests drive
/// the real apply core against the test DB (5434) with a mediator that throws during the bulk-add, and
/// assert that nothing survives the rollback. Far-future dates (2099) isolate the clone to the seeded
/// rows; each test cleans up after itself.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using Work = Klacks.Api.Domain.Models.Schedules.Work;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class HarmonizerApplyTransactionTests
{
    private const string TestPrefix = "INTEGRATION_TEST_APPLYTX_";
    private static readonly DateOnly PeriodFrom = new(2099, 9, 6);
    private static readonly DateOnly PeriodUntil = new(2099, 9, 10);
    private static readonly DateOnly InPeriodDate = new(2099, 9, 8);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        _context = NewContext();
        _service = new AnalyseScenarioService(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
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
        // Prefix-only deletes: this database is shared with the dev app (incident 2026-07-03).
        var sql = $@"
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var c = new Client
        {
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, FirstName = "Test",
            Company = string.Empty, LegalEntity = false,
        };
        await _context.Set<Client>().AddAsync(c);
        await _context.SaveChangesAsync();
        return c;
    }

    private async Task<Shift> CreateShiftAsync(string suffix)
    {
        var s = new Shift
        {
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, Abbreviation = "TXT", Description = "Apply tx test",
            Status = ShiftStatus.OriginalShift, FromDate = new DateOnly(2099, 1, 1), UntilDate = null,
            StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            IsSaturday = true, IsSunday = true,
            ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 8m,
            AnalyseToken = null, ScenarioSourceShiftId = null,
        };
        await _context.Shift.AddAsync(s);
        await _context.SaveChangesAsync();
        return s;
    }

    private async Task<Work> CreateWorkAsync(Guid clientId, Guid shiftId, DateOnly date)
    {
        var w = new Work
        {
            Id = Guid.NewGuid(), ClientId = clientId, ShiftId = shiftId, CurrentDate = date,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), WorkTime = 8m,
            LockLevel = WorkLockLevel.None, AnalyseToken = null,
        };
        await _context.Work.AddAsync(w);
        await _context.SaveChangesAsync();
        return w;
    }

    private HarmonizerApplyService BuildApply(HarmonizerResultCache cache, IMediator mediator)
        => new(
            cache,
            mediator,
            new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>()),
            _service,
            new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>()),
            _context,
            Substitute.For<IWizardRunCaptureRepository>(),
            Substitute.For<IScenarioComplianceService>(),
            Substitute.For<IScheduleTimelineService>(),
            Substitute.For<IScheduleSnapshotMarkerService>(),
            Substitute.For<ILogger<HarmonizerApplyService>>());

    private static HarmonyBitmap BuildBitmap(Guid agentId, Guid shiftId, Guid workId)
    {
        var rows = new List<BitmapAgent> { new(agentId.ToString(), "A", 0m, new HashSet<CellSymbol>()) };
        var days = new List<DateOnly> { InPeriodDate };
        var cells = new Cell[1, 1];
        cells[0, 0] = new Cell(CellSymbol.Early, shiftId, new List<Guid> { workId }, false);
        return new HarmonyBitmap(rows, days, cells);
    }

    [Test, Explicit("Read/write apply-transaction rollback against the real test DB (port 5434); cleans up after itself.")]
    public async Task ApplyAsScenario_BulkAddThrows_RollsBackTheScenarioAndItsClone()
    {
        var client = await CreateClientAsync("RB");
        var shift = await CreateShiftAsync("RS");
        var work = await CreateWorkAsync(client.Id, shift.Id, InPeriodDate);

        var scenarioTokenSource = Guid.NewGuid();
        var cache = new HarmonizerResultCache();
        var jobId = Guid.NewGuid();
        // A scenario-sourced run takes the delete+recreate branch, which goes through the bulk-add.
        cache.Store(jobId, BuildBitmap(client.Id, shift.Id, work.Id), BuildBitmap(client.Id, shift.Id, work.Id),
            sourceAnalyseToken: scenarioTokenSource);

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkAddWorksCommand>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("bulk add blew up"));

        var scenariosBefore = await _context.Set<AnalyseScenario>().IgnoreQueryFilters()
            .CountAsync(s => s.Name.StartsWith(TestPrefix));

        await Should.ThrowAsync<InvalidOperationException>(
            () => BuildApply(cache, mediator).ApplyAsScenarioAsync(jobId, null, CancellationToken.None, TestPrefix + "Run"));

        await using var verifyContext = NewContext();
        var scenariosAfter = await verifyContext.Set<AnalyseScenario>().IgnoreQueryFilters()
            .CountAsync(s => s.Name.StartsWith(TestPrefix));
        scenariosAfter.ShouldBe(scenariosBefore, "the scenario row must not survive the rollback");

        var clonedWorks = await verifyContext.Work.IgnoreQueryFilters()
            .CountAsync(w => w.AnalyseToken != null && w.ShiftId == shift.Id);
        clonedWorks.ShouldBe(0, "the cloned schedule must not survive the rollback");

        var original = await verifyContext.Work.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(w => w.Id == work.Id);
        original.IsDeleted.ShouldBeFalse("the source plan must be untouched");
        original.ClientId.ShouldBe(client.Id);
    }

    [Test, Explicit("Read/write apply-transaction success path against the real test DB (port 5434); cleans up after itself.")]
    public async Task ApplyAsScenario_Succeeds_PersistsScenarioAndClone()
    {
        var client = await CreateClientAsync("SB");
        var shift = await CreateShiftAsync("SS");
        var work = await CreateWorkAsync(client.Id, shift.Id, InPeriodDate);

        var cache = new HarmonizerResultCache();
        var jobId = Guid.NewGuid();
        // A real-plan source takes the re-point branch, which needs no bulk-add.
        cache.Store(jobId, BuildBitmap(client.Id, shift.Id, work.Id), BuildBitmap(client.Id, shift.Id, work.Id),
            sourceAnalyseToken: null);

        var (scenario, _, _) = await BuildApply(cache, Substitute.For<IMediator>())
            .ApplyAsScenarioAsync(jobId, null, CancellationToken.None, TestPrefix + "Run");

        await using var verifyContext = NewContext();
        var stored = await verifyContext.Set<AnalyseScenario>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scenario.Id);
        stored.ShouldNotBeNull("the committed scenario must be readable from a fresh context");

        var clonedWorks = await verifyContext.Work.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(w => w.AnalyseToken == stored!.Token);
        clonedWorks.ShouldBeGreaterThan(0, "the scenario must carry its cloned schedule");
    }
}
