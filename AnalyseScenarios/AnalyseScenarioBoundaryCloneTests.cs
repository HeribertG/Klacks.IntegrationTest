// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the AnalyseScenario boundary-clone feature: cloning
/// extends ±BoundaryDays around the period so cross-period works/breaks/notes
/// are visible in scenario mode, WorkChange + Expenses children carry the
/// AnalyseToken, and Accept discards boundary clones instead of promoting
/// them (which would otherwise duplicate the unchanged real boundary entries).
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class AnalyseScenarioBoundaryCloneTests
{
    private const string TestPrefix = "INTEGRATION_TEST_BOUNDARY_";
    private static readonly DateOnly PeriodFrom = new DateOnly(2026, 6, 1);
    private static readonly DateOnly PeriodUntil = new DateOnly(2026, 6, 30);

    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private AnalyseScenarioService _service = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
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
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _service = new AnalyseScenarioService(_context);
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
            DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM client_shift_preference WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
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

    private async Task<Shift> CreateRealShiftAsync(string suffix)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Boundary-clone test",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
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

    private async Task<Work> CreateRealWorkAsync(Guid clientId, Guid shiftId, DateOnly date)
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
            AnalyseToken = null
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
        return work;
    }

    [Test]
    public async Task Clone_Includes_Work_On_Boundary_Day_Minus_BoundaryDays()
    {
        var shift = await CreateRealShiftAsync("BoundaryStart");
        var client = await CreateTestClientAsync("BoundaryStart");
        var boundaryStartDate = PeriodFrom.AddDays(-ScenarioConstants.BoundaryDays);
        var realWork = await CreateRealWorkAsync(client.Id, shift.Id, boundaryStartDate);

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneCount = await _context.Work.IgnoreQueryFilters()
            .CountAsync(w => w.AnalyseToken == token && w.CurrentDate == boundaryStartDate && w.ClientId == client.Id);
        cloneCount.ShouldBe(1, $"work on fromDate-{ScenarioConstants.BoundaryDays} must be cloned for context");
    }

    [Test]
    public async Task Clone_Includes_Work_On_Boundary_Day_Plus_BoundaryDays()
    {
        var shift = await CreateRealShiftAsync("BoundaryEnd");
        var client = await CreateTestClientAsync("BoundaryEnd");
        var boundaryEndDate = PeriodUntil.AddDays(ScenarioConstants.BoundaryDays);
        await CreateRealWorkAsync(client.Id, shift.Id, boundaryEndDate);

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneCount = await _context.Work.IgnoreQueryFilters()
            .CountAsync(w => w.AnalyseToken == token && w.CurrentDate == boundaryEndDate && w.ClientId == client.Id);
        cloneCount.ShouldBe(1, $"work on untilDate+{ScenarioConstants.BoundaryDays} must be cloned for context");
    }

    [Test]
    public async Task Clone_Excludes_Work_Beyond_BoundaryDays()
    {
        var shift = await CreateRealShiftAsync("Beyond");
        var client = await CreateTestClientAsync("Beyond");
        var beyondDate = PeriodFrom.AddDays(-(ScenarioConstants.BoundaryDays + 1));
        await CreateRealWorkAsync(client.Id, shift.Id, beyondDate);

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneCount = await _context.Work.IgnoreQueryFilters()
            .CountAsync(w => w.AnalyseToken == token && w.CurrentDate == beyondDate && w.ClientId == client.Id);
        cloneCount.ShouldBe(0, $"work on fromDate-{ScenarioConstants.BoundaryDays + 1} is outside the boundary and must NOT be cloned");
    }

    [Test]
    public async Task Clone_Sets_AnalyseToken_On_WorkChange_Children()
    {
        var shift = await CreateRealShiftAsync("WC");
        var client = await CreateTestClientAsync("WC");
        var work = await CreateRealWorkAsync(client.Id, shift.Id, PeriodFrom.AddDays(5));
        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            ChangeTime = 1m,
            Type = WorkChangeType.CorrectionStart,
            Description = "Test correction",
            AnalyseToken = null
        };
        await _context.WorkChange.AddAsync(workChange);
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clonedChanges = await _context.WorkChange.IgnoreQueryFilters()
            .Where(wc => wc.AnalyseToken == token && wc.Description == "Test correction")
            .AsNoTracking()
            .ToListAsync();
        clonedChanges.Count.ShouldBe(1, "WorkChange clone must exist with the scenario token");
    }

    [Test]
    public async Task Clone_Sets_AnalyseToken_On_Expenses_Children()
    {
        var shift = await CreateRealShiftAsync("EX");
        var client = await CreateTestClientAsync("EX");
        var work = await CreateRealWorkAsync(client.Id, shift.Id, PeriodFrom.AddDays(5));
        var expense = new Expenses
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Amount = 42.50m,
            Description = "Test expense",
            Taxable = true,
            AnalyseToken = null
        };
        await _context.Expenses.AddAsync(expense);
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clonedExpenses = await _context.Expenses.IgnoreQueryFilters()
            .Where(e => e.AnalyseToken == token && e.Description == "Test expense")
            .AsNoTracking()
            .ToListAsync();
        clonedExpenses.Count.ShouldBe(1, "Expenses clone must exist with the scenario token");
    }

    [Test]
    public async Task Promote_Discards_Boundary_Works_And_Keeps_Period_Works()
    {
        var shift = await CreateRealShiftAsync("Promote");
        var client = await CreateTestClientAsync("Promote");
        await CreateRealWorkAsync(client.Id, shift.Id, PeriodFrom.AddDays(5));
        await CreateRealWorkAsync(client.Id, shift.Id, PeriodFrom.AddDays(-3));

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        await _service.PromoteScenarioWorksAsync(token, PeriodFrom, PeriodUntil, CancellationToken.None);
        await _context.SaveChangesAsync();

        var promotedClonesInPeriod = await _context.Work.IgnoreQueryFilters()
            .CountAsync(w => w.AnalyseToken == null
                && !w.IsDeleted
                && w.ClientId == client.Id
                && w.CurrentDate == PeriodFrom.AddDays(5));
        promotedClonesInPeriod.ShouldBe(2, "period entries must end up real: original + promoted clone");

        var promotedClonesInBoundary = await _context.Work.IgnoreQueryFilters()
            .CountAsync(w => w.AnalyseToken == null
                && !w.IsDeleted
                && w.ClientId == client.Id
                && w.CurrentDate == PeriodFrom.AddDays(-3));
        promotedClonesInBoundary.ShouldBe(1, "boundary entries: only the original real work survives, the boundary clone was discarded");

        var leftoverBoundaryClones = await _context.Work.IgnoreQueryFilters()
            .CountAsync(w => w.AnalyseToken == token && !w.IsDeleted);
        leftoverBoundaryClones.ShouldBe(0, "no scenario-tagged works must remain after promote");
    }

    [Test]
    public async Task Promote_Discards_Boundary_WorkChange_And_Expenses_Children()
    {
        var shift = await CreateRealShiftAsync("Children");
        var client = await CreateTestClientAsync("Children");
        var boundaryWork = await CreateRealWorkAsync(client.Id, shift.Id, PeriodFrom.AddDays(-5));
        var workChange = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = boundaryWork.Id,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            ChangeTime = 1m,
            Type = WorkChangeType.CorrectionStart,
            Description = "Boundary correction",
            AnalyseToken = null
        };
        var expense = new Expenses
        {
            Id = Guid.NewGuid(),
            WorkId = boundaryWork.Id,
            Amount = 10m,
            Description = "Boundary expense",
            Taxable = true,
            AnalyseToken = null
        };
        await _context.WorkChange.AddAsync(workChange);
        await _context.Expenses.AddAsync(expense);
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        await _service.PromoteScenarioWorksAsync(token, PeriodFrom, PeriodUntil, CancellationToken.None);
        await _context.SaveChangesAsync();

        var leftoverScenarioWorkChanges = await _context.WorkChange.IgnoreQueryFilters()
            .CountAsync(wc => wc.AnalyseToken == token && !wc.IsDeleted);
        leftoverScenarioWorkChanges.ShouldBe(0, "no scenario-tagged WorkChange may remain after promote");

        var leftoverScenarioExpenses = await _context.Expenses.IgnoreQueryFilters()
            .CountAsync(e => e.AnalyseToken == token && !e.IsDeleted);
        leftoverScenarioExpenses.ShouldBe(0, "no scenario-tagged Expenses may remain after promote");
    }
}
