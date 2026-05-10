// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests verifying that Work + Break Seal/Unseal lifecycle methods
/// never touch scenario clones (AnalyseToken != null) and that sealing-summary
/// read endpoints do not count clones in real-mode totals.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Break = Klacks.Api.Domain.Models.Schedules.Break;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class AnalyseScenarioWorkBreakSealingTests
{
    private const string TestPrefix = "INTEGRATION_TEST_SEALING_";

    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private IWorkRepository _workRepository = null!;
    private IBreakRepository _breakRepository = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        await using var context = new DataBaseContext(options, mockHttpContextAccessor);
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        var workLogger = Substitute.For<ILogger<Work>>();
        var breakLogger = Substitute.For<ILogger<Break>>();
        _workRepository = new WorkRepository(
            _context,
            workLogger,
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IClientContractDataProvider>());
        _breakRepository = new BreakRepository(_context, breakLogger);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE information LIKE '{TestPrefix}%');
            DELETE FROM break WHERE information LIKE '{TestPrefix}%';
            DELETE FROM work WHERE information LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM absence WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Shift> CreateShiftAsync(string suffix)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "sealing test",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2026, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            AnalyseToken = null,
            ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    private async Task<Absence> CreateAbsenceAsync(string suffix)
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new Klacks.Api.Domain.Common.MultiLanguage { De = TestPrefix + suffix },
            Abbreviation = new Klacks.Api.Domain.Common.MultiLanguage { De = "TST" },
            Description = new Klacks.Api.Domain.Common.MultiLanguage { De = "sealing absence" },
            Color = "#cccccc"
        };
        await _context.Set<Absence>().AddAsync(absence);
        await _context.SaveChangesAsync();
        return absence;
    }

    private async Task<Client> CreateTestClientAsync(string suffix)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Work> CreateWorkAsync(Guid clientId, Guid shiftId, DateOnly date, Guid? analyseToken, string suffix)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            Information = TestPrefix + suffix,
            AnalyseToken = analyseToken,
            LockLevel = WorkLockLevel.None,
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
        return work;
    }

    private async Task<Break> CreateBreakAsync(Guid clientId, Guid absenceId, DateOnly date, Guid? analyseToken, string suffix)
    {
        var brk = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = date,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            WorkTime = 1m,
            Information = TestPrefix + suffix,
            AnalyseToken = analyseToken,
            LockLevel = WorkLockLevel.None,
        };
        await _context.Break.AddAsync(brk);
        await _context.SaveChangesAsync();
        return brk;
    }

    [Test]
    public async Task Work_SealByPeriod_Skips_Scenario_Clones()
    {
        var client = await CreateTestClientAsync("SealClient1");
        var date = new DateOnly(2026, 6, 15);
        var shift = await CreateShiftAsync("Shift1");
        var realWork = await CreateWorkAsync(client.Id, shift.Id, date, analyseToken: null, "RealWork");
        var token = Guid.NewGuid();
        var cloneWork = await CreateWorkAsync(client.Id, shift.Id, date, analyseToken: token, "CloneWork");

        var affected = await _workRepository.SealByPeriod(
            startDate: new DateOnly(2026, 6, 1),
            endDate: new DateOnly(2026, 6, 30),
            level: WorkLockLevel.Closed,
            sealedBy: "test-user",
            CancellationToken.None);

        affected.ShouldBe(1, "only the real work must be sealed");

        var sealedReal = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == realWork.Id).AsNoTracking().SingleAsync();
        var untouchedClone = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == cloneWork.Id).AsNoTracking().SingleAsync();

        sealedReal.LockLevel.ShouldBe(WorkLockLevel.Closed);
        sealedReal.SealedBy.ShouldBe("test-user");
        untouchedClone.LockLevel.ShouldBe(WorkLockLevel.None, "clone must not be sealed");
        untouchedClone.SealedBy.ShouldBeNull("clone must remain unsealed");
        untouchedClone.AnalyseToken.ShouldBe(token, "clone token must be preserved");
    }

    [Test]
    public async Task Work_UnsealByPeriod_Skips_Scenario_Clones()
    {
        var client = await CreateTestClientAsync("UnsealClient1");
        var date = new DateOnly(2026, 6, 15);
        var token = Guid.NewGuid();

        var shift = await CreateShiftAsync("Shift2");
        var realSealed = await CreateWorkAsync(client.Id, shift.Id, date, null, "RealSealed");
        realSealed.LockLevel = WorkLockLevel.Closed;
        realSealed.SealedAt = DateTime.UtcNow;
        realSealed.SealedBy = "old-user";
        var cloneSealed = await CreateWorkAsync(client.Id, shift.Id, date, token, "CloneSealed");
        cloneSealed.LockLevel = WorkLockLevel.Closed;
        cloneSealed.SealedAt = DateTime.UtcNow;
        cloneSealed.SealedBy = "old-user";
        await _context.SaveChangesAsync();

        var affected = await _workRepository.UnsealByPeriod(
            startDate: new DateOnly(2026, 6, 1),
            endDate: new DateOnly(2026, 6, 30),
            level: WorkLockLevel.Closed,
            CancellationToken.None);

        affected.ShouldBe(1, "only the real work must be unsealed");

        var realAfter = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == realSealed.Id).AsNoTracking().SingleAsync();
        var cloneAfter = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == cloneSealed.Id).AsNoTracking().SingleAsync();

        realAfter.LockLevel.ShouldBe(WorkLockLevel.None);
        cloneAfter.LockLevel.ShouldBe(WorkLockLevel.Closed, "clone must remain sealed independent of real-mode unseal");
        cloneAfter.SealedBy.ShouldBe("old-user");
    }

    [Test]
    public async Task Break_SealByPeriod_Skips_Scenario_Clones()
    {
        var client = await CreateTestClientAsync("SealBreakClient");
        var date = new DateOnly(2026, 6, 15);
        var absence = await CreateAbsenceAsync("Absence1");
        var realBreak = await CreateBreakAsync(client.Id, absence.Id, date, null, "RealBreak");
        var token = Guid.NewGuid();
        var cloneBreak = await CreateBreakAsync(client.Id, absence.Id, date, token, "CloneBreak");

        var affected = await _breakRepository.SealByPeriod(
            startDate: new DateOnly(2026, 6, 1),
            endDate: new DateOnly(2026, 6, 30),
            level: WorkLockLevel.Closed,
            sealedBy: "test-user",
            CancellationToken.None);

        affected.ShouldBe(1, "only the real break must be sealed");

        var realAfter = await _context.Break.IgnoreQueryFilters()
            .Where(b => b.Id == realBreak.Id).AsNoTracking().SingleAsync();
        var cloneAfter = await _context.Break.IgnoreQueryFilters()
            .Where(b => b.Id == cloneBreak.Id).AsNoTracking().SingleAsync();

        realAfter.LockLevel.ShouldBe(WorkLockLevel.Closed);
        cloneAfter.LockLevel.ShouldBe(WorkLockLevel.None, "clone break must not be sealed");
        cloneAfter.AnalyseToken.ShouldBe(token);
    }

    [Test]
    public async Task Work_GetSealingSummary_Excludes_Clones_From_Totals()
    {
        var client = await CreateTestClientAsync("SummaryClient");
        var date = new DateOnly(2026, 6, 15);
        var token = Guid.NewGuid();

        var shift = await CreateShiftAsync("Shift3");
        await CreateWorkAsync(client.Id, shift.Id, date, null, "Real1");
        await CreateWorkAsync(client.Id, shift.Id, date, null, "Real2");
        await CreateWorkAsync(client.Id, shift.Id, date, token, "Clone1");

        var summary = await _workRepository.GetSealingSummaryAsync(
            from: new DateOnly(2026, 6, 1),
            to: new DateOnly(2026, 6, 30),
            groupId: null,
            CancellationToken.None);

        var entry = summary.SingleOrDefault(x => x.Date == date);
        entry.Total.ShouldBe(2, "summary must count the two real works only — clone is invisible");
        entry.Sealed.ShouldBe(0);
    }
}
