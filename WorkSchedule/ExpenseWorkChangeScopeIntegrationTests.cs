// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Common;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using ExpensesHandlers = Klacks.Api.Application.Handlers.Expenses;
using WorkChangeHandlers = Klacks.Api.Application.Handlers.WorkChanges;

namespace Klacks.IntegrationTest.WorkSchedule;

/// <summary>
/// WP1 (2026-10-08) against real Postgres: a PUT of a scenario expense or scenario WorkChange keeps analyse_token
/// (the resources carry none and PUT writes the full row, which used to wipe it), a POST inherits the parent
/// Work's token, and a scenario expense or WorkChange can be deleted (the filtered Get used to answer 404).
/// </summary>
[TestFixture]
public class ExpenseWorkChangeScopeIntegrationTests
{
    private const string Prefix = "INTEGRATION_TEST_ExpenseScope_";
    private static readonly DateOnly Day = new(2026, 5, 6);

    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private Guid _clientId;
    private Guid _shiftId;
    private Guid _scenarioWorkId;
    private Guid _closedWorkId;
    private Guid _scenarioToken;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
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
        _closedWorkId = Guid.NewGuid();
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
            WorkTime = 480,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            AnalyseToken = _scenarioToken,
        });
        _context.Work.Add(new Work
        {
            Id = _closedWorkId,
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = Day.AddDays(1),
            WorkTime = 480,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            LockLevel = WorkLockLevel.Closed,
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var workId in new[] { _scenarioWorkId, _closedWorkId })
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM expenses WHERE work_id = {0} AND description LIKE {1}", workId, Prefix + "%");
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM surcharge_item WHERE work_change_id IN (SELECT id FROM work_change WHERE work_id = {0} AND description LIKE {1})",
                workId, Prefix + "%");
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM work_change WHERE work_id = {0} AND description LIKE {1}", workId, Prefix + "%");
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM work WHERE id = {0} AND client_id = {1}", workId, _clientId);
        }
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0} AND name LIKE {1}", _shiftId, Prefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id = {0} AND name LIKE {1}", _clientId, Prefix + "%");
        _context.Dispose();
    }

    [Test]
    public async Task PostExpense_OnAScenarioWork_IsStoredWithTheWorksToken()
    {
        var response = await BuildExpensePostHandler().Handle(new PostCommand<ExpensesResource>(new ExpensesResource
        {
            WorkId = _scenarioWorkId,
            Amount = 9m,
            Description = Prefix + "Posted",
        }), CancellationToken.None);

        response.ShouldNotBeNull();
        var stored = await _context.Expenses.AsNoTracking().SingleAsync(e => e.Id == response!.Id);
        stored.AnalyseToken.ShouldBe(_scenarioToken);
    }

    [Test]
    public async Task PutExpense_OfAScenarioExpense_KeepsAnalyseTokenInTheDatabase()
    {
        var expenseId = await SeedScenarioExpenseAsync();

        var response = await BuildExpensePutHandler().Handle(new PutCommand<ExpensesResource>(new ExpensesResource
        {
            Id = expenseId,
            WorkId = _scenarioWorkId,
            Amount = 42m,
            Description = Prefix + "Edited",
            Taxable = true,
        }), CancellationToken.None);

        response.ShouldNotBeNull();
        var stored = await _context.Expenses.AsNoTracking().SingleAsync(e => e.Id == expenseId);
        stored.AnalyseToken.ShouldBe(_scenarioToken, "a PUT must not move a scenario expense into the main plan");
        stored.Amount.ShouldBe(42m);
        response!.ScheduleEntries.ShouldContain(e => e.SourceId == _scenarioWorkId,
            "the scenario re-read must still ship the scenario's three-day snapshot");
    }

    [Test]
    public async Task DeleteExpense_OfAScenarioExpense_IsDeleted()
    {
        var expenseId = await SeedScenarioExpenseAsync();

        var response = await BuildExpenseDeleteHandler().Handle(
            new DeleteCommand<ExpensesResource>(expenseId), CancellationToken.None);

        response.ShouldNotBeNull("a scenario expense must not be answered like a missing one");
        var stored = await _context.Expenses.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == expenseId);
        stored.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task PutWorkChange_OfAScenarioChange_KeepsAnalyseTokenInTheDatabase()
    {
        var changeId = await SeedScenarioWorkChangeAsync();

        var response = await BuildWorkChangePutHandler().Handle(new PutCommand<WorkChangeResource>(new WorkChangeResource
        {
            Id = changeId,
            WorkId = _scenarioWorkId,
            ChangeTime = 1m,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            Type = WorkChangeType.CorrectionStart,
            Description = Prefix + "Edited",
        }), CancellationToken.None);

        response.ShouldNotBeNull();
        var stored = await _context.WorkChange.AsNoTracking().SingleAsync(wc => wc.Id == changeId);
        stored.AnalyseToken.ShouldBe(_scenarioToken, "a PUT must not move a scenario change into the main plan");
        stored.ChangeTime.ShouldBe(1m);
    }

    [Test]
    public async Task DeleteWorkChange_OfAScenarioChange_IsDeleted()
    {
        var changeId = await SeedScenarioWorkChangeAsync();

        var response = await BuildWorkChangeDeleteHandler().Handle(
            new DeleteCommand<WorkChangeResource>(changeId), CancellationToken.None);

        response.ShouldNotBeNull("a scenario change must not be answered like a missing one");
        var stored = await _context.WorkChange.IgnoreQueryFilters().AsNoTracking().SingleAsync(wc => wc.Id == changeId);
        stored.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task PutExpense_OnAClosedMainPlanWork_IsRefused_RowUnchanged()
    {
        var expenseId = await SeedExpenseAsync(_closedWorkId, null);

        await Should.ThrowAsync<InvalidRequestException>(() => BuildExpensePutHandler().Handle(
            new PutCommand<ExpensesResource>(new ExpensesResource
            {
                Id = expenseId,
                WorkId = _closedWorkId,
                Amount = 99m,
                Description = Prefix + "Edited",
            }), CancellationToken.None));

        var stored = await _context.Expenses.AsNoTracking().SingleAsync(e => e.Id == expenseId);
        stored.Amount.ShouldBe(12m, "a closed period must not be changed through its expenses");
    }

    [Test]
    public async Task DeleteExpense_OnAClosedMainPlanWork_IsRefused_RowKept()
    {
        var expenseId = await SeedExpenseAsync(_closedWorkId, null);

        await Should.ThrowAsync<InvalidRequestException>(() => BuildExpenseDeleteHandler().Handle(
            new DeleteCommand<ExpensesResource>(expenseId), CancellationToken.None));

        var stored = await _context.Expenses.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == expenseId);
        stored.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task PutWorkChange_OnAClosedMainPlanWork_IsRefused_RowUnchanged()
    {
        var changeId = await SeedWorkChangeAsync(_closedWorkId, null);

        await Should.ThrowAsync<InvalidRequestException>(() => BuildWorkChangePutHandler().Handle(
            new PutCommand<WorkChangeResource>(new WorkChangeResource
            {
                Id = changeId,
                WorkId = _closedWorkId,
                ChangeTime = 3m,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(11, 0),
                Type = WorkChangeType.CorrectionStart,
                Description = Prefix + "Edited",
            }), CancellationToken.None));

        var stored = await _context.WorkChange.AsNoTracking().SingleAsync(wc => wc.Id == changeId);
        stored.ChangeTime.ShouldBe(0.5m, "a closed period must not be changed through its work changes");
    }

    private Task<Guid> SeedScenarioExpenseAsync() => SeedExpenseAsync(_scenarioWorkId, _scenarioToken);

    private Task<Guid> SeedScenarioWorkChangeAsync() => SeedWorkChangeAsync(_scenarioWorkId, _scenarioToken);

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

    private async Task<Guid> SeedWorkChangeAsync(Guid workId, Guid? analyseToken)
    {
        var id = Guid.NewGuid();
        _context.WorkChange.Add(new WorkChange
        {
            Id = id,
            WorkId = workId,
            ChangeTime = 0.5m,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(8, 30),
            Type = WorkChangeType.CorrectionStart,
            Description = Prefix + "Seeded",
            AnalyseToken = analyseToken,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private ExpensesHandlers.PostCommandHandler BuildExpensePostHandler()
    {
        var accessor = BuildHttpContextAccessor();
        return new ExpensesHandlers.PostCommandHandler(
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), AllClientsVisible(),
            new ScheduleMapper(), BuildUnitOfWork(), BuildPeriodHoursService(), BuildScheduleEntriesService(),
            Substitute.For<IWorkNotificationService>(), accessor, Substitute.For<IScheduleChangeTracker>(),
            BuildGroupResolver(accessor), DbBackedWorkRepository(), Substitute.For<IDayLockService>(), NewLockGuard(),
            Substitute.For<ILogger<ExpensesHandlers.PostCommandHandler>>());
    }

    private ExpensesHandlers.PutCommandHandler BuildExpensePutHandler()
    {
        var accessor = BuildHttpContextAccessor();
        return new ExpensesHandlers.PutCommandHandler(
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), AllClientsVisible(),
            new ScheduleMapper(), BuildUnitOfWork(), BuildPeriodHoursService(), BuildScheduleEntriesService(),
            Substitute.For<IWorkNotificationService>(), accessor, Substitute.For<IScheduleChangeTracker>(),
            BuildGroupResolver(accessor), DbBackedWorkRepository(), Substitute.For<IDayLockService>(), NewLockGuard(),
            Substitute.For<ILogger<ExpensesHandlers.PutCommandHandler>>());
    }

    private ExpensesHandlers.DeleteCommandHandler BuildExpenseDeleteHandler()
    {
        var accessor = BuildHttpContextAccessor();
        return new ExpensesHandlers.DeleteCommandHandler(
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), AllClientsVisible(),
            new ScheduleMapper(), BuildUnitOfWork(), BuildPeriodHoursService(), BuildScheduleEntriesService(),
            Substitute.For<IWorkNotificationService>(), accessor, Substitute.For<IScheduleChangeTracker>(),
            BuildGroupResolver(accessor), Substitute.For<IDayLockService>(), NewLockGuard(),
            Substitute.For<ILogger<ExpensesHandlers.DeleteCommandHandler>>());
    }

    private WorkChangeHandlers.PutCommandHandler BuildWorkChangePutHandler()
        => new(
            BuildWorkChangeRepository(), DbBackedWorkRepository(), AllClientsVisible(), new ScheduleMapper(),
            BuildPeriodHoursService(), BuildSavingCompletionService(), Substitute.For<IWorkChangeResultService>(),
            Substitute.For<IWorkNotificationFacade>(), Substitute.For<IDayLockService>(),
            Substitute.For<IReplacementRequestRecorder>(), BuildHttpContextAccessor(), NewLockGuard(),
            Substitute.For<ILogger<WorkChangeHandlers.PutCommandHandler>>());

    private WorkChangeHandlers.DeleteCommandHandler BuildWorkChangeDeleteHandler()
        => new(
            BuildWorkChangeRepository(), DbBackedWorkRepository(), AllClientsVisible(), new ScheduleMapper(),
            BuildPeriodHoursService(), Substitute.For<IWorkNotificationService>(), BuildSavingCompletionService(),
            Substitute.For<IWorkChangeResultService>(), BuildHttpContextAccessor(), Substitute.For<IDayLockService>(),
            Substitute.For<IReplacementRequestRecorder>(), NewLockGuard(),
            Substitute.For<ILogger<WorkChangeHandlers.DeleteCommandHandler>>());

    private WorkChangeRepository BuildWorkChangeRepository()
        => new(_context, Substitute.For<ILogger<WorkChange>>(), Substitute.For<IWorkMacroService>());

    private IWorkRepository DbBackedWorkRepository()
    {
        var repository = Substitute.For<IWorkRepository>();
        repository.GetNoTracking(Arg.Any<Guid>())
            .Returns(ci => _context.Work.AsNoTracking().FirstOrDefaultAsync(w => w.Id == ci.Arg<Guid>()));
        repository.Get(Arg.Any<Guid>())
            .Returns(ci => _context.Work.AsNoTracking().FirstOrDefaultAsync(w => w.Id == ci.Arg<Guid>()));
        return repository;
    }

    private IScheduleCompletionService BuildSavingCompletionService()
    {
        var service = Substitute.For<IScheduleCompletionService>();
        service.SaveAndTrackWithReplaceClientAsync(
                Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>())
            .Returns(async _ => { await _context.SaveChangesAsync(); });
        return service;
    }

    private static ParentWorkLockGuard NewLockGuard() => new(new WorkLockLevelService());

    private static IPeriodHoursService BuildPeriodHoursService()
    {
        var service = Substitute.For<IPeriodHoursService>();
        service.GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns((new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31)));
        return service;
    }

    private ScheduleEntriesService BuildScheduleEntriesService()
        => new(_context, Substitute.For<ILogger<ScheduleEntriesService>>());

    private static SelectedGroupContextResolver BuildGroupResolver(IHttpContextAccessor accessor)
        => new(accessor, Substitute.For<IShiftGroupFilterService>());

    private static IClientVisibilityGuard AllClientsVisible()
    {
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    private static IHttpContextAccessor BuildHttpContextAccessor()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HttpHeaderNames.SignalRConnectionId] = string.Empty;
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }

    private IUnitOfWork BuildUnitOfWork()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(_ => _context.SaveChangesAsync());
        return unitOfWork;
    }
}
