// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration test for the evaluate_scenario reader seam: the REAL CloneScenarioDataAsync tags the
/// cloned works/shifts with the scenario token, and the REAL ScheduleEntriesService (the
/// get_schedule_entries stored procedure) must project them so that the handler's effective-grid diff
/// (real token=null vs. scenario token) treats a cloned work as NO change while a scenario-only break
/// surfaces as added. This is the un-mocked clone&lt;-&gt;reader interaction the unit tests cannot cover:
/// the unit tests hand-build cells encoding assumptions about SP output, whereas this asserts the
/// real SP shape. Only the period validator is stubbed (the rule-compliance dimension is covered
/// separately); the diff dimension runs against the real database.
/// </summary>

using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class EvaluateScenarioReaderSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_EVALSEAM_";
    private static readonly DateOnly PeriodFrom = new(2099, 6, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 6, 30);
    private static readonly DateOnly WorkDate = new(2099, 6, 5);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _cloneService = null!;

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
        _cloneService = new AnalyseScenarioService(_context);
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
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
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

    private async Task<Shift> CreateShiftAsync()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TST",
            Description = "Evaluate-scenario reader seam test",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
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

    private async Task CreateRealWorkAsync(Guid clientId, Guid shiftId)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = null
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
    }

    private async Task<AnalyseScenario> CreateScenarioRowAsync(Guid token)
    {
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SCENARIO",
            Token = token,
            GroupId = null,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active
        };
        await _context.Set<AnalyseScenario>().AddAsync(scenario);
        await _context.SaveChangesAsync();
        return scenario;
    }

    private async Task InjectScenarioBreakAsync(Guid clientId, Guid token)
    {
        var absenceId = await _context.Set<Absence>()
            .Where(a => !a.IsDeleted)
            .Select(a => a.Id)
            .FirstOrDefaultAsync();
        if (absenceId == Guid.Empty)
        {
            Assert.Ignore("No absence type seeded on the test database.");
        }

        var absence = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AbsenceId = absenceId,
            ParentWorkId = null,
            AnalyseToken = token
        };
        await _context.Break.AddAsync(absence);
        await _context.SaveChangesAsync();
    }

    private EvaluateScenarioQueryHandler Handler()
    {
        var scenarioRepo = new AnalyseScenarioRepository(
            _context, Substitute.For<ILogger<AnalyseScenario>>());
        var entries = new ScheduleEntriesService(
            _context, Substitute.For<ILogger<ScheduleEntriesService>>());
        var loader = Substitute.For<IPeriodValidationLoader>();
        loader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto>());
        return new EvaluateScenarioQueryHandler(scenarioRepo, entries, loader);
    }

    [Test]
    public async Task ClonedWorkIsInvisibleToDiff_InjectedBreakSurfaces()
    {
        var anna = await CreateClientAsync("ANNA");
        var shift = await CreateShiftAsync();
        await CreateRealWorkAsync(anna.Id, shift.Id);

        var token = Guid.NewGuid();
        await CreateScenarioRowAsync(token);
        await _cloneService.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        await InjectScenarioBreakAsync(anna.Id, token);

        var result = await Handler().Handle(new EvaluateScenarioQuery(null, token), CancellationToken.None);

        result.Found.ShouldBeTrue();
        // The cloned (byte-identical) Anna work matches its real twin -> NOT a change.
        result.AddedWorkEntries.ShouldBe(0);
        result.AddedEntries.ShouldNotContain(e => e.EntryType == "Work");
        result.RemovedEntryCount.ShouldBe(0);
        // The scenario-only absence surfaces as an added break.
        result.AddedBreakEntries.ShouldBe(1);
    }
}
