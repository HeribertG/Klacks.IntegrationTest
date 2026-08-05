// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the C4 apply re-point (2026-06-19). The real-plan accept path of the
/// harmonizer/W4 (and standalone W2/W3 on a real plan) re-points cloned works in place instead of
/// delete+recreate, so a work's WorkChange/Expense/sub-Break children survive accept. These tests drive
/// the real CloneScenarioDataWithMapsAsync and RepointClonedWorksAsync against the test DB (5434):
/// the work id map must cover the real works, the work must move to the bitmap's agent, and a sub-break
/// (which carries its OWN ClientId) must follow the work — no stale per-child agent. Far-future dates
/// (2099) isolate the group-less clone to the seeded rows. Each test cleans up after itself.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Break = Klacks.Api.Domain.Models.Schedules.Break;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using Work = Klacks.Api.Domain.Models.Schedules.Work;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class HarmonizerApplyRepointCloneTests
{
    private const string TestPrefix = "INTEGRATION_TEST_REPOINT_";
    private static readonly DateOnly PeriodFrom = new(2099, 8, 6);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 10);
    private static readonly DateOnly InPeriodDate = new(2099, 8, 8);

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
        var sql = $@"
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var c = new Client { Id = Guid.NewGuid(), Name = TestPrefix + suffix, FirstName = "Test", Company = string.Empty, LegalEntity = false };
        await _context.Set<Client>().AddAsync(c);
        await _context.SaveChangesAsync();
        return c;
    }

    private async Task<Shift> CreateShiftAsync(string suffix)
    {
        var s = new Shift
        {
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, Abbreviation = "TST", Description = "Re-point test",
            Status = ShiftStatus.OriginalShift, FromDate = new DateOnly(2099, 1, 1), UntilDate = null,
            StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            IsSaturday = true, IsSunday = true,
            ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 8m,
            AnalyseToken = null, ScenarioSourceShiftId = null
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
            LockLevel = WorkLockLevel.None, AnalyseToken = null
        };
        await _context.Work.AddAsync(w);
        await _context.SaveChangesAsync();
        return w;
    }

    [Test, Explicit("Read/write C4 re-point clone against the real test DB (port 5434); cleans up after itself.")]
    public async Task CloneWithMaps_ReturnsWorkIdMap_AndInPlaceRepointPersists()
    {
        var clientA = await CreateClientAsync("A");
        var clientB = await CreateClientAsync("B");
        var shift = await CreateShiftAsync("S");
        var work = await CreateWorkAsync(clientA.Id, shift.Id, InPeriodDate);

        var token = Guid.NewGuid();
        var (_, workIdMap) = await _service.CloneScenarioDataWithMapsAsync(
            null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        workIdMap.ShouldContainKey(work.Id);
        var cloneId = workIdMap[work.Id];
        cloneId.ShouldNotBe(work.Id);

        var clone = await _context.Work.IgnoreQueryFilters().FirstAsync(w => w.Id == cloneId);
        clone.AnalyseToken.ShouldBe(token);
        clone.ClientId.ShouldBe(clientA.Id);
        clone.IsDeleted.ShouldBeFalse();
    }

    [Test, Explicit("Read/write C4 re-point (A->B) + sub-break cascade against the real test DB (5434); cleans up.")]
    public async Task Repoint_MovesWorkAndItsSubBreak_ToTheNewAgent()
    {
        var clientA = await CreateClientAsync("RA");
        var clientB = await CreateClientAsync("RB");
        var shift = await CreateShiftAsync("RS");
        var work = await CreateWorkAsync(clientA.Id, shift.Id, InPeriodDate);

        // A sub-break (work pause) carries its OWN ClientId (ScheduleEntryBase) — the re-point must move
        // it with the work. Anchor it on any existing absence to satisfy the FK.
        var absenceId = await _context.Set<Absence>().IgnoreQueryFilters().Select(a => a.Id).FirstOrDefaultAsync();
        if (absenceId == default)
        {
            Assert.Ignore("No absence rows in the test DB to anchor a sub-break.");
        }
        await _context.Break.AddAsync(new Break
        {
            Id = Guid.NewGuid(), ClientId = clientA.Id, CurrentDate = InPeriodDate,
            ParentWorkId = work.Id, AbsenceId = absenceId, AnalyseToken = null
        });
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        var (_, workIdMap) = await _service.CloneScenarioDataWithMapsAsync(
            null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();
        var cloneId = workIdMap[work.Id];

        var clonedBreakBefore = await _context.Break.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(b => b.AnalyseToken == token && b.ParentWorkId == cloneId);
        clonedBreakBefore.ClientId.ShouldBe(clientA.Id, "the cloned sub-break starts on agent A");

        // Bitmap re-assigns the work from A to B (B's row holds the work, A's cell is free).
        var rows = new List<BitmapAgent>
        {
            new(clientA.Id.ToString(), "A", 0m, new HashSet<CellSymbol>()),
            new(clientB.Id.ToString(), "B", 0m, new HashSet<CellSymbol>())
        };
        var days = new List<DateOnly> { InPeriodDate };
        var cells = new Cell[2, 1];
        cells[0, 0] = Cell.Free();
        cells[1, 0] = new Cell(CellSymbol.Early, shift.Id, new List<Guid> { work.Id }, false);
        var bitmap = new HarmonyBitmap(rows, days, cells);

        var apply = new HarmonizerApplyService(
            new HarmonizerResultCache(),
            Substitute.For<IMediator>(),
            Substitute.For<IAnalyseScenarioRepository>(),
            _service,
            Substitute.For<IUnitOfWork>(),
            _context,
            Substitute.For<IWizardRunCaptureRepository>(),
            Substitute.For<IScenarioComplianceService>(),
            Substitute.For<IScheduleTimelineService>(),
            Substitute.For<IScheduleSnapshotMarkerService>(),
            Substitute.For<ILogger<HarmonizerApplyService>>());

        var repointed = await apply.RepointClonedWorksAsync(
            token, PeriodFrom, PeriodUntil, bitmap, workIdMap, CancellationToken.None);
        await _context.SaveChangesAsync();

        repointed.ShouldContain(cloneId);

        var reloadedWork = await _context.Work.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.Id == cloneId);
        reloadedWork.ClientId.ShouldBe(clientB.Id, "the cloned work must be re-pointed to agent B");
        reloadedWork.IsDeleted.ShouldBeFalse();

        var reloadedBreak = await _context.Break.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(b => b.AnalyseToken == token && b.ParentWorkId == cloneId);
        reloadedBreak.IsDeleted.ShouldBeFalse("the sub-break must survive the re-point, not be dropped");
        reloadedBreak.ClientId.ShouldBe(clientB.Id, "the sub-break must follow its work to agent B (no stale client_id)");
    }

    [Test, Explicit("Read/write C4 re-point soft-delete branch against the real test DB (5434); cleans up.")]
    public async Task Repoint_SoftDeletes_AWorkTheBitmapNoLongerAssigns_WithItsSubBreak()
    {
        var clientA = await CreateClientAsync("DA");
        var clientB = await CreateClientAsync("DB");
        var shift = await CreateShiftAsync("DS");
        var keepDay = InPeriodDate;
        var dropDay = InPeriodDate.AddDays(1);
        var keep = await CreateWorkAsync(clientA.Id, shift.Id, keepDay);
        var drop = await CreateWorkAsync(clientA.Id, shift.Id, dropDay);

        var absenceId = await _context.Set<Absence>().IgnoreQueryFilters().Select(a => a.Id).FirstOrDefaultAsync();
        if (absenceId == default)
        {
            Assert.Ignore("No absence rows in the test DB to anchor a sub-break.");
        }
        await _context.Break.AddAsync(new Break
        {
            Id = Guid.NewGuid(), ClientId = clientA.Id, CurrentDate = dropDay,
            ParentWorkId = drop.Id, AbsenceId = absenceId, AnalyseToken = null
        });
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        var (_, workIdMap) = await _service.CloneScenarioDataWithMapsAsync(
            null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();
        var keepClone = workIdMap[keep.Id];
        var dropClone = workIdMap[drop.Id];

        // Bitmap keeps `keep` (re-pointed to B on its day) and assigns nothing on the drop day, so the
        // `drop` clone is unreferenced and must be soft-deleted with its sub-break.
        var rows = new List<BitmapAgent>
        {
            new(clientA.Id.ToString(), "A", 0m, new HashSet<CellSymbol>()),
            new(clientB.Id.ToString(), "B", 0m, new HashSet<CellSymbol>())
        };
        var days = new List<DateOnly> { keepDay, dropDay };
        var cells = new Cell[2, 2];
        cells[0, 0] = Cell.Free();
        cells[1, 0] = new Cell(CellSymbol.Early, shift.Id, new List<Guid> { keep.Id }, false);
        cells[0, 1] = Cell.Free();
        cells[1, 1] = Cell.Free();
        var bitmap = new HarmonyBitmap(rows, days, cells);

        var apply = new HarmonizerApplyService(
            new HarmonizerResultCache(),
            Substitute.For<IMediator>(),
            Substitute.For<IAnalyseScenarioRepository>(),
            _service,
            Substitute.For<IUnitOfWork>(),
            _context,
            Substitute.For<IWizardRunCaptureRepository>(),
            Substitute.For<IScenarioComplianceService>(),
            Substitute.For<IScheduleTimelineService>(),
            Substitute.For<IScheduleSnapshotMarkerService>(),
            Substitute.For<ILogger<HarmonizerApplyService>>());

        var repointed = await apply.RepointClonedWorksAsync(
            token, PeriodFrom, PeriodUntil, bitmap, workIdMap, CancellationToken.None);
        await _context.SaveChangesAsync();

        repointed.ShouldContain(keepClone);
        repointed.ShouldNotContain(dropClone);

        var keptWork = await _context.Work.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.Id == keepClone);
        keptWork.IsDeleted.ShouldBeFalse("the assigned work survives");
        keptWork.ClientId.ShouldBe(clientB.Id);

        var droppedWork = await _context.Work.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.Id == dropClone);
        droppedWork.IsDeleted.ShouldBeTrue("the unassigned work must be soft-deleted");

        var droppedBreak = await _context.Break.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(b => b.AnalyseToken == token && b.ParentWorkId == dropClone);
        droppedBreak.IsDeleted.ShouldBeTrue("the dropped work's sub-break must be soft-deleted with it");
    }
}
