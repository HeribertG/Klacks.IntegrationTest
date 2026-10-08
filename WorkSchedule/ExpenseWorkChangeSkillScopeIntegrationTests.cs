// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

/// <summary>
/// The assistant's expense and WorkChange skills against real Postgres through the real mediator and DI scope:
/// update_expense / update_workchange reach scenario rows when the scenario token is passed, keep analyse_token, and
/// answer a row of another scope as not found; delete_expense / delete_workchange reach a scenario row by id (the
/// rollback of add_expense passes only the id). Runs read and write in one DI scope, so it also proves the scoped
/// read leaves nothing tracked that would collide with the full-row PUT.
/// </summary>
[TestFixture]
public class ExpenseWorkChangeSkillScopeIntegrationTests
{
    private const string Prefix = "INTEGRATION_TEST_SkillScope_";
    private static readonly DateOnly Day = new(2201, 3, 14);

    private SignalRTestWebApplicationFactory _factory = null!;
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private Guid _clientId;
    private Guid _shiftId;
    private Guid _scenarioWorkId;
    private Guid _mainWorkId;
    private Guid _scenarioToken;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _clientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _scenarioWorkId = Guid.NewGuid();
        _mainWorkId = Guid.NewGuid();
        _scenarioToken = Guid.NewGuid();

        _context.Client.Add(new Client { Id = _clientId, Name = Prefix + "Client", FirstName = "Integration" });
        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = Prefix + "Shift",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
        });
        _context.Work.Add(new Work
        {
            Id = _scenarioWorkId,
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = Day,
            WorkTime = 8,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            AnalyseToken = _scenarioToken,
        });
        _context.Work.Add(new Work
        {
            Id = _mainWorkId,
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = Day.AddDays(1),
            WorkTime = 8,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM expenses WHERE work_id IN ({0}, {1}) AND description LIKE {2}",
            _scenarioWorkId, _mainWorkId, Prefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE id = {0} AND client_id = {1}", _mainWorkId, _clientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM surcharge_item WHERE work_change_id IN (SELECT id FROM work_change WHERE work_id = {0} AND description LIKE {1})",
            _scenarioWorkId, Prefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work_change WHERE work_id = {0} AND description LIKE {1}", _scenarioWorkId, Prefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_period_hours WHERE client_id = {0}", _clientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE id = {0} AND client_id = {1}", _scenarioWorkId, _clientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0} AND name LIKE {1}", _shiftId, Prefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id = {0} AND name LIKE {1}", _clientId, Prefix + "%");
        _context.Dispose();
    }

    [Test]
    public async Task UpdateExpenseSkill_WithTheScenarioToken_EditsTheScenarioExpense_AndKeepsItsToken()
    {
        var expenseId = await SeedExpenseAsync();
        using var scope = _factory.Services.CreateScope();
        var skill = new UpdateExpenseSkill(scope.ServiceProvider.GetRequiredService<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = expenseId.ToString(),
            ["amount"] = 42m,
            ["analyseToken"] = _scenarioToken.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        var stored = await _context.Expenses.AsNoTracking().SingleAsync(e => e.Id == expenseId);
        stored.Amount.ShouldBe(42m);
        stored.AnalyseToken.ShouldBe(_scenarioToken);
    }

    [Test]
    public async Task UpdateExpenseSkill_WithoutToken_DoesNotReachTheScenarioExpense()
    {
        var expenseId = await SeedExpenseAsync();
        using var scope = _factory.Services.CreateScope();
        var skill = new UpdateExpenseSkill(scope.ServiceProvider.GetRequiredService<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = expenseId.ToString(),
            ["amount"] = 42m
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        var stored = await _context.Expenses.AsNoTracking().SingleAsync(e => e.Id == expenseId);
        stored.Amount.ShouldBe(12m);
    }

    [Test]
    public async Task DeleteExpenseSkill_ById_DeletesTheScenarioExpense()
    {
        var expenseId = await SeedExpenseAsync();
        using var scope = _factory.Services.CreateScope();
        var skill = new DeleteExpenseSkill(scope.ServiceProvider.GetRequiredService<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = expenseId.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        var stored = await _context.Expenses.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == expenseId);
        stored.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task UpdateWorkChangeSkill_WithTheScenarioToken_EditsTheScenarioChange_AndKeepsItsToken()
    {
        var changeId = await SeedWorkChangeAsync();
        using var scope = _factory.Services.CreateScope();
        var skill = new UpdateWorkChangeSkill(scope.ServiceProvider.GetRequiredService<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workChangeId"] = changeId.ToString(),
            ["changeTime"] = 1m,
            ["description"] = Prefix + "Edited",
            ["analyseToken"] = _scenarioToken.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        var stored = await _context.WorkChange.AsNoTracking().SingleAsync(wc => wc.Id == changeId);
        stored.ChangeTime.ShouldBe(1m);
        stored.AnalyseToken.ShouldBe(_scenarioToken);
    }

    [Test]
    public async Task DeleteWorkChangeSkill_ById_DeletesTheScenarioChange()
    {
        var changeId = await SeedWorkChangeAsync();
        using var scope = _factory.Services.CreateScope();
        var skill = new DeleteWorkChangeSkill(scope.ServiceProvider.GetRequiredService<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workChangeId"] = changeId.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        var stored = await _context.WorkChange.IgnoreQueryFilters().AsNoTracking().SingleAsync(wc => wc.Id == changeId);
        stored.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task ListExpenses_NeverMixesMainPlanAndScenarioRows()
    {
        var scenarioExpenseId = await SeedExpenseAsync();
        var mainExpenseId = await SeedExpenseAsync(_mainWorkId, null);
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var mainList = (await mediator.Send(new ListQuery<ExpensesResource>(), CancellationToken.None))
            .Select(e => e.Id).ToList();
        var scenarioList = (await mediator.Send(new ListExpensesInScopeQuery(_scenarioToken), CancellationToken.None))
            .Select(e => e.Id).ToList();

        mainList.ShouldContain(mainExpenseId);
        mainList.ShouldNotContain(scenarioExpenseId, "the main-plan list (REST GET /Expenses) must not leak scenario rows");
        scenarioList.Count.ShouldBe(1, "a scenario list holds only that scenario's rows");
        scenarioList[0].ShouldBe(scenarioExpenseId);
    }

    [Test]
    public async Task ListExpensesSkill_WithTheScenarioToken_ReportsOnlyTheScenarioExpenses()
    {
        await SeedExpenseAsync();
        await SeedExpenseAsync(_mainWorkId, null);
        using var scope = _factory.Services.CreateScope();
        var skill = new ListExpensesSkill(scope.ServiceProvider.GetRequiredService<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["analyseToken"] = _scenarioToken.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Found 1 expense entries");
    }

    [Test]
    public async Task PostWorkChange_OnAMissingWork_IsAnsweredWithKeyNotFound()
    {
        var missingWorkId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(async () => await mediator.Send(
            new PostCommand<WorkChangeResource>(new WorkChangeResource
            {
                WorkId = missingWorkId,
                Type = WorkChangeType.CorrectionStart,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                ChangeTime = 1m,
                Description = Prefix + "Missing",
            }), CancellationToken.None));

        ex.Message.ShouldBe($"Work with ID {missingWorkId} not found");
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "integration-test",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private Task<Guid> SeedExpenseAsync() => SeedExpenseAsync(_scenarioWorkId, _scenarioToken);

    private async Task<Guid> SeedExpenseAsync(Guid workId, Guid? analyseToken)
    {
        var id = Guid.NewGuid();
        _context.Expenses.Add(new Expenses
        {
            Id = id,
            WorkId = workId,
            Amount = 12m,
            Description = Prefix + "Seeded",
            AnalyseToken = analyseToken,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private async Task<Guid> SeedWorkChangeAsync()
    {
        var id = Guid.NewGuid();
        _context.WorkChange.Add(new WorkChange
        {
            Id = id,
            WorkId = _scenarioWorkId,
            ChangeTime = 0.5m,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(8, 30),
            Type = WorkChangeType.CorrectionStart,
            Description = Prefix + "Seeded",
            AnalyseToken = _scenarioToken,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }
}
