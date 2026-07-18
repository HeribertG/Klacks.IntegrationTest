// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration test for the propose_plan -> accept_scenario lifecycle seam: a propose_plan placement
/// is materialised as a scenario work on a CLONE shift (token-tagged), and the REAL
/// AcceptAnalyseScenarioCommandHandler must promote it to a real work (AnalyseToken == null) whose
/// ShiftId is remapped back to the ORIGINAL source shift. The unit tests mock the clone/promote
/// services, so this is the only coverage of PromoteScenarioWorksAsync's shift-id remapping against
/// the real database. Only the work-softening repository is stubbed (softenings are not part of the
/// promote correctness). Far-future dates keep SoftDeleteRealScheduleDataAsync from touching any other
/// real schedule data.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
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
public class ProposePlanAcceptLifecycleSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PROPOSEACCEPT_";
    private static readonly DateOnly PeriodFrom = new(2098, 6, 1);
    private static readonly DateOnly PeriodUntil = new(2098, 6, 30);
    private static readonly DateOnly WorkDate = new(2098, 6, 5);

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
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync()
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
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
            Description = "Propose-accept lifecycle seam test",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2098, 1, 1),
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

    private async Task InjectScenarioPlacementAsync(Guid clientId, Guid cloneShiftId, Guid token)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = cloneShiftId,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
    }

    private AcceptAnalyseScenarioCommandHandler Handler()
    {
        var repo = new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>());
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        var softening = Substitute.For<IWorkSofteningRepository>();
        return new AcceptAnalyseScenarioCommandHandler(
            repo, _cloneService, unitOfWork, softening,
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }

    [Test]
    public async Task AcceptPromotesScenarioPlacement_AsRealWork_RemappedToOriginalShift()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();

        var token = Guid.NewGuid();
        var scenario = await CreateScenarioRowAsync(token);
        var shiftIdMap = await _cloneService.CloneScenarioDataAsync(
            null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneShiftId = shiftIdMap[shift.Id];
        await InjectScenarioPlacementAsync(client.Id, cloneShiftId, token);

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);
        accepted.ShouldBeTrue();

        await using var verify = NewContext();

        var realWorks = await verify.Work
            .Where(w => w.ClientId == client.Id && w.CurrentDate == WorkDate && w.AnalyseToken == null)
            .ToListAsync();
        realWorks.Count.ShouldBe(1);
        realWorks[0].ShiftId.ShouldBe(shift.Id);

        var remainingScenarioWorks = await verify.Work.IgnoreQueryFilters()
            .CountAsync(w => w.AnalyseToken == token && !w.IsDeleted);
        remainingScenarioWorks.ShouldBe(0);

        var refreshed = await verify.Set<AnalyseScenario>().FirstAsync(s => s.Id == scenario.Id);
        refreshed.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }
}
