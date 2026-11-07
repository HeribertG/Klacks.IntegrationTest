// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Why the stand-in delete lock counts scenario works, against PostgreSQL (port 5434): the accept promotes the scenario
/// works without looking at their client, so a scenario work of a soft-deleted client lands in the real plan. The delete
/// lock therefore counts a stand-in's works in an open scenario, and once the scenario data is discarded (reject or delete) they no longer
/// count.
/// Every row carries the prefix INTEGRATION_TEST_StandInScenario_ and only those rows are removed.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.IntegrationTest.TestHelpers;
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
public class StandInScenarioWorkDeleteLockSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_StandInScenario_";
    private static readonly DateOnly PeriodFrom = new(2099, 9, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 9, 30);
    private static readonly DateOnly WorkDate = new(2099, 9, 7);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _scenarioService = null!;
    private Group _group = null!;
    private Shift _shift = null!;
    private Client _standIn = null!;

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
        _scenarioService = AnalyseScenarioTestServiceFactory.Create(_context);

        _group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "GROUP",
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = null
        };
        _shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TSI",
            Description = "Stand-in scenario delete lock",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        };
        _standIn = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "Fair 1",
            FirstName = string.Empty,
            Company = string.Empty,
            LegalEntity = false,
            Type = EntityTypeEnum.StandIn
        };
        await _context.Set<Group>().AddAsync(_group);
        await _context.Shift.AddAsync(_shift);
        await _context.Set<Client>().AddAsync(_standIn);
        await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = _group.Id, ShiftId = _shift.Id });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task StandInWithOnlyAScenarioWork_IsCountedForTheDeleteLock_UntilTheScenarioDataIsDiscarded()
    {
        var scenario = await CreateScenarioWithStandInWorkAsync("LOCK");

        (await WorkRepository().CountWorksOfClientAsync(_standIn.Id)).ShouldBe(1);

        await _scenarioService.SoftDeleteScenarioDataAsync(scenario.Token, CancellationToken.None);
        await _context.SaveChangesAsync();

        (await WorkRepository().CountWorksOfClientAsync(_standIn.Id)).ShouldBe(0, "a discarded scenario leaves no work behind");
    }

    [Test]
    public async Task Accept_PromotesAScenarioWorkOfADeletedClient_IntoTheRealPlan()
    {
        var scenario = await CreateScenarioWithStandInWorkAsync("ACCEPT");
        _standIn.IsDeleted = true;
        _standIn.DeletedTime = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var accepted = await Handler().Handle(
            new AcceptAnalyseScenarioCommand(scenario.Id, OverrideStale: true), CancellationToken.None);

        accepted.Accepted.ShouldBeTrue();
        await using var verify = NewContext();
        (await verify.Work.CountAsync(w => w.ClientId == _standIn.Id && w.AnalyseToken == null))
            .ShouldBe(1, "the accept does not check the client, which is why the stand-in delete lock counts scenario works");
    }

    private async Task<AnalyseScenario> CreateScenarioWithStandInWorkAsync(string suffix)
    {
        var token = Guid.NewGuid();
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Token = token,
            GroupId = _group.Id,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active
        };
        await _context.Set<AnalyseScenario>().AddAsync(scenario);
        await _scenarioService.CloneScenarioDataAsync(_group.Id, PeriodFrom, PeriodUntil, token, null, null, CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneShiftId = await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.AnalyseToken == token && s.ScenarioSourceShiftId == _shift.Id)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync();
        await _context.Work.AddAsync(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _standIn.Id,
            ShiftId = cloneShiftId ?? _shift.Id,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token
        });
        await _context.SaveChangesAsync();
        return scenario;
    }

    private WorkRepository WorkRepository() => new(
        _context,
        Substitute.For<ILogger<Work>>(),
        Substitute.For<Klacks.Api.Infrastructure.Services.Clients.IClientBaseQueryService>(),
        Substitute.For<Klacks.Api.Domain.Interfaces.Macros.IWorkMacroService>(),
        Substitute.For<Klacks.Api.Domain.Interfaces.Associations.IClientContractDataProvider>());

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
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private AcceptAnalyseScenarioCommandHandler Handler()
    {
        var repo = new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>());
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        var compliance = Substitute.For<Klacks.Api.Application.Interfaces.Schedules.IScenarioComplianceService>();
        compliance
            .EvaluateAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new Klacks.Api.Application.DTOs.Schedules.ScenarioComplianceReport([], []));
        return new AcceptAnalyseScenarioCommandHandler(
            repo, _scenarioService, unitOfWork, Substitute.For<IWorkSofteningRepository>(),
            compliance,
            Substitute.For<Klacks.Api.Application.Interfaces.Schedules.ISupervisorOverrideAuthorizer>(),
            Substitute.For<Klacks.Api.Application.Interfaces.IScheduleTimelineService>(),
            new Klacks.Api.Infrastructure.Repositories.Assistant.AgentConditionRepository(_context),
            new Klacks.Api.Application.Services.Assistant.Conditions.AgentConditionLedgerService(
                new Klacks.Api.Infrastructure.Repositories.Assistant.AgentConditionRepository(_context),
                TimeProvider.System,
                Substitute.For<ILogger<Klacks.Api.Application.Services.Assistant.Conditions.AgentConditionLedgerService>>()),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Schedules.IPeriodHoursService>(),
            Substitute.For<Klacks.Api.Application.Interfaces.IWorkNotificationService>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Assistant.IEscalationChainService>(),
            Klacks.IntegrationTest.TestHelpers.UnrestrictedGroupVisibility.Create(),
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }
}
