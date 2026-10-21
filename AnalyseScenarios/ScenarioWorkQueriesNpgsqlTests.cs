// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proof for the scenario work queries that the unit tests only run on the in-memory provider:
/// CorrectedWorkQuery.LoadAsync (enum list Contains + join, null and set token, agent filter), LoadFixedWorkIdsAsync
/// (replacement covers plus corrected works) and ScenarioSummaryBuilder's count of expenses whose work changed employee
/// (join on the nullable ClonedFromWorkId). Every row carries an INTEGRATION_TEST_ prefixed key, the cleanup deletes
/// nothing else.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class ScenarioWorkQueriesNpgsqlTests
{
    private const string TestPrefix = "INTEGRATION_TEST_SCENWORKQ_";
    private const decimal RealCorrectionHours = 1m;
    private const decimal ScenarioCorrectionHours = 2m;
    private static readonly DateOnly From = new(2099, 8, 2);
    private static readonly DateOnly Until = new(2099, 8, 8);
    private static readonly DateOnly Day = new(2099, 8, 4);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private readonly Guid _token = Guid.NewGuid();
    private Guid _clientA;
    private Guid _clientB;
    private Guid _realCorrectedId;
    private Guid _scenarioCorrectedId;
    private Guid _scenarioCoveredId;
    private Guid _scenarioReassignedId;

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
        _clientA = Guid.NewGuid();
        _clientB = Guid.NewGuid();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TSQ",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        };
        _context.Shift.Add(shift);
        _context.Client.AddRange(
            new Client { Id = _clientA, Name = TestPrefix + "A", FirstName = "Test", Company = string.Empty },
            new Client { Id = _clientB, Name = TestPrefix + "B", FirstName = "Test", Company = string.Empty });

        var realCorrected = NewWork(_clientA, shift.Id, null);
        var realOrigin = NewWork(_clientA, shift.Id, null);
        var scenarioCorrected = NewWork(_clientA, shift.Id, _token, realCorrected.Id);
        var scenarioCovered = NewWork(_clientB, shift.Id, _token);
        var scenarioReassigned = NewWork(_clientB, shift.Id, _token, realOrigin.Id);
        _context.Work.AddRange(realCorrected, realOrigin, scenarioCorrected, scenarioCovered, scenarioReassigned);
        _realCorrectedId = realCorrected.Id;
        _scenarioCorrectedId = scenarioCorrected.Id;
        _scenarioCoveredId = scenarioCovered.Id;
        _scenarioReassignedId = scenarioReassigned.Id;

        _context.WorkChange.AddRange(
            NewChange(realCorrected.Id, WorkChangeType.CorrectionEnd, RealCorrectionHours, null, null),
            NewChange(scenarioCorrected.Id, WorkChangeType.CorrectionEnd, ScenarioCorrectionHours, null, _token),
            NewChange(scenarioCovered.Id, WorkChangeType.ReplacementWithin, 4m, _clientA, _token));
        _context.Expenses.AddRange(
            NewExpense(scenarioCorrected.Id),
            NewExpense(scenarioReassigned.Id));
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task CorrectedWorks_OfTheRealPlan_AreLoadedWithTheirHours()
    {
        var corrected = await CorrectedWorkQuery.LoadAsync(_context, null, From, Until, [_clientA, _clientB], CancellationToken.None);

        corrected.ShouldContainKeyAndValue(_realCorrectedId, RealCorrectionHours);
        corrected.ShouldNotContainKey(_scenarioCorrectedId);
    }

    [Test]
    public async Task CorrectedWorks_OfAScenario_AreLoaded_AndTheAgentFilterApplies()
    {
        var corrected = await CorrectedWorkQuery.LoadAsync(_context, _token, From, Until, null, CancellationToken.None);
        var otherAgentOnly = await CorrectedWorkQuery.LoadAsync(_context, _token, From, Until, [_clientB], CancellationToken.None);

        corrected.Keys.ToList().ShouldBe(new List<Guid> { _scenarioCorrectedId }, "a replacement cover is not a correction");
        corrected[_scenarioCorrectedId].ShouldBe(ScenarioCorrectionHours);
        otherAgentOnly.ShouldBeEmpty();
    }

    [Test]
    public async Task FixedWorks_OfAScenario_AreReplacementCoversPlusCorrectedWorks()
    {
        var fixedIds = await CorrectedWorkQuery.LoadFixedWorkIdsAsync(_context, _token, From, Until, CancellationToken.None);

        fixedIds.ShouldBe(new[] { _scenarioCorrectedId, _scenarioCoveredId }, ignoreOrder: true);
    }

    [Test]
    public async Task Summary_CountsOnlyExpensesWhoseWorkChangedEmployee()
    {
        var scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        scenarioRepository.GetByTokenAsync(_token, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = _token, FromDate = From, UntilDate = Until, GroupId = null });
        var contractProvider = Substitute.For<IClientContractDataProvider>();
        contractProvider
            .GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>());
        var builder = new ScenarioSummaryBuilder(_context, scenarioRepository, AnalyseScenarioTestServiceFactory.Create(_context), contractProvider);

        var summary = await builder.BuildAsync(_token, CancellationToken.None);

        summary.ShouldNotBeNull();
        summary.ExpensesWithChangedEmployee.ShouldBe(1, "only the reassigned clone's expense moved to another employee");
    }

    private Work NewWork(Guid clientId, Guid shiftId, Guid? token, Guid? clonedFrom = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = shiftId,
        CurrentDate = Day,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        WorkTime = 8m,
        AnalyseToken = token,
        ClonedFromWorkId = clonedFrom
    };

    private static WorkChange NewChange(Guid workId, WorkChangeType type, decimal hours, Guid? replaceClientId, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = workId,
        Type = type,
        ChangeTime = hours,
        StartTime = new TimeOnly(16, 0),
        EndTime = new TimeOnly(17, 0),
        ReplaceClientId = replaceClientId,
        Description = TestPrefix + "CHANGE",
        AnalyseToken = token
    };

    private Expenses NewExpense(Guid workId) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = workId,
        Amount = 12m,
        Description = TestPrefix + "EXPENSE",
        AnalyseToken = _token
    };

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
            DELETE FROM expenses WHERE description LIKE '{TestPrefix}%';
            DELETE FROM surcharge_item WHERE work_change_id IN (SELECT id FROM work_change WHERE description LIKE '{TestPrefix}%');
            DELETE FROM work_change WHERE description LIKE '{TestPrefix}%';
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
