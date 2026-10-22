// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database guard for the staleness gate of the accept: scenarios are decoupled copies, and accepting one replaces
/// the original's movable entries in its period with the scenario. When the original changed after the scenario was
/// created - another scenario accepted in between (owner example: A created, B created, B accepted, then A accepted
/// would silently drop B's replacement) or an edit by hand - the accept is refused with ScenarioStaleException until the
/// caller acknowledges it with OverrideStale. A seal or unseal after creation is not stale: the accept keeps the locked
/// side of the slot and discards the clone, so nothing is overwritten. Runs the production staleness queries on Npgsql,
/// including the accepted-sibling lookup.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Exceptions;
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
public class AcceptStaleScenarioSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_STALEACCEPT_";
    private const int ClockGapMilliseconds = 20;
    private static readonly DateOnly PeriodFrom = new(2099, 7, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 7, 31);
    private static readonly DateOnly WorkDate = new(2099, 7, 6);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _scenarioService = null!;
    private Group _group = null!;
    private Shift _shift = null!;
    private Work _realWork = null!;

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
            Abbreviation = "TST",
            Description = "Stale accept guard",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        };
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Group>().AddAsync(_group);
        await _context.Shift.AddAsync(_shift);
        await _context.Set<Client>().AddAsync(client);
        await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = _group.Id, ShiftId = _shift.Id });
        _realWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ShiftId = _shift.Id,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m
        };
        await _context.Work.AddAsync(_realWork);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task UnchangedOriginal_IsAccepted()
    {
        var scenario = await CreateClonedScenarioAsync("UNCHANGED");

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        accepted.Accepted.ShouldBeTrue();
    }

    [Test]
    public async Task EditByHandAfterCreation_IsRefusedAsStale_UntilAcknowledged()
    {
        var scenario = await CreateClonedScenarioAsync("HANDEDIT");
        await Task.Delay(ClockGapMilliseconds);
        _realWork.StartTime = new TimeOnly(9, 0);
        await _context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() =>
            Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None));

        ex.Staleness.ChangedEntryCount.ShouldBe(1);
        ex.Staleness.AcceptedScenarioNames.ShouldBeEmpty();
        await using (var verify = NewContext())
        {
            (await verify.Work.CountAsync(w => w.Id == _realWork.Id)).ShouldBe(1, "a refused accept must not touch the original");
        }

        var accepted = await Handler().Handle(
            new AcceptAnalyseScenarioCommand(scenario.Id, OverrideStale: true), CancellationToken.None);
        accepted.Accepted.ShouldBeTrue();
    }

    [Test]
    public async Task SiblingAcceptedAfterCreation_IsRefusedAsStale_AndNamesTheSibling()
    {
        var scenarioA = await CreateClonedScenarioAsync("A");
        await Task.Delay(ClockGapMilliseconds);
        var scenarioB = await CreateClonedScenarioAsync("B");
        await Task.Delay(ClockGapMilliseconds);

        (await Handler().Handle(new AcceptAnalyseScenarioCommand(scenarioB.Id), CancellationToken.None)).Accepted.ShouldBeTrue();

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() =>
            Handler().Handle(new AcceptAnalyseScenarioCommand(scenarioA.Id), CancellationToken.None));

        ex.Staleness.ChangedEntryCount.ShouldBeGreaterThan(0);
        ex.Staleness.AcceptedScenarioNames.ShouldBe(new[] { TestPrefix + "B" });
    }

    [Test]
    public async Task UnsealAfterCreation_IsNotStale_BecauseTheLockedCloneKeepsTheRealWork()
    {
        _realWork.LockLevel = WorkLockLevel.Confirmed;
        await _context.SaveChangesAsync();
        var scenario = await CreateClonedScenarioAsync("UNSEAL");
        await Task.Delay(ClockGapMilliseconds);

        var unsealed = await WorkRepository().UnsealByDayAndGroup(WorkDate, _group.Id, WorkLockLevel.Confirmed);
        unsealed.ShouldBe(1);

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        accepted.Accepted.ShouldBeTrue("the scenario's copy is locked, so the accept keeps the unsealed real work");
        await using var verify = NewContext();
        (await verify.Work.Where(w => w.AnalyseToken == null && w.CurrentDate == WorkDate && w.ClientId == _realWork.ClientId)
                .ToListAsync())
            .ShouldHaveSingleItem().Id.ShouldBe(_realWork.Id);
    }

    [Test]
    public async Task SealAfterCreation_IsNotStale_BecauseTheAcceptKeepsTheSealedWork()
    {
        var scenario = await CreateClonedScenarioAsync("SEAL");
        await Task.Delay(ClockGapMilliseconds);

        var sealedCount = await WorkRepository().SealByDayAndGroup(WorkDate, _group.Id, WorkLockLevel.Confirmed, "sealer");
        sealedCount.ShouldBe(1);

        var accepted = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        accepted.Accepted.ShouldBeTrue("the sealed work stays and its clone is discarded, so nothing is overwritten");
        await using var verify = NewContext();
        var live = (await verify.Work
                .Where(w => w.AnalyseToken == null && w.CurrentDate == WorkDate && w.ClientId == _realWork.ClientId)
                .ToListAsync())
            .ShouldHaveSingleItem();
        live.Id.ShouldBe(_realWork.Id);
        live.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
    }

    [Test]
    public async Task ChildBuiltFromAParentAfterARealEdit_IsRefusedAsStale()
    {
        var parent = await CreateClonedScenarioAsync("PARENT");
        await Task.Delay(ClockGapMilliseconds);
        _realWork.StartTime = new TimeOnly(9, 0);
        await _context.SaveChangesAsync();
        await Task.Delay(ClockGapMilliseconds);
        var child = await CreateClonedScenarioAsync("CHILD", parent.Token);

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() =>
            Handler().Handle(new AcceptAnalyseScenarioCommand(child.Id), CancellationToken.None));

        ex.Staleness.ChangedEntryCount.ShouldBe(1, "the child carries the parent's snapshot, which predates the edit");
    }

    private WorkRepository WorkRepository() => new(
        _context,
        Substitute.For<ILogger<Work>>(),
        Substitute.For<Klacks.Api.Infrastructure.Services.Clients.IClientBaseQueryService>(),
        Substitute.For<Klacks.Api.Domain.Interfaces.Macros.IWorkMacroService>(),
        Substitute.For<Klacks.Api.Domain.Interfaces.Associations.IClientContractDataProvider>());
    private async Task<AnalyseScenario> CreateClonedScenarioAsync(string suffix, Guid? sourceToken = null)
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
            Status = AnalyseScenarioStatus.Active,
            SourceToken = sourceToken
        };
        await _context.Set<AnalyseScenario>().AddAsync(scenario);
        await _scenarioService.CloneScenarioDataAsync(_group.Id, PeriodFrom, PeriodUntil, token, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();
        return scenario;
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
