// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.Common;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
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
    private readonly List<Guid> _extraClientIds = [];

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
            "DELETE FROM replacement_requests WHERE absent_client_id = {0} AND analyse_token = {1}", _clientId, _scenarioToken);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0} AND name LIKE {1}", _shiftId, Prefix + "%");
        foreach (var extraClientId in _extraClientIds)
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM client WHERE id = {0} AND name LIKE {1}", extraClientId, Prefix + "%");
        }

        _extraClientIds.Clear();
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
    public async Task GetWorkChange_ById_ResolvesAScenarioChangeFromTheStoredRow()
    {
        var changeId = await SeedScenarioWorkChangeAsync();
        var handler = new WorkChangeHandlers.GetQueryHandler(
            BuildWorkChangeRepository(), AllRowsVisible(), new ScheduleMapper(),
            Substitute.For<ILogger<WorkChangeHandlers.GetQueryHandler>>());
        _context.ChangeTracker.Clear();

        var result = await handler.Handle(new GetQuery<WorkChangeResource>(changeId), CancellationToken.None);

        result.Id.ShouldBe(changeId);
    }

    [Test]
    public async Task GetExpense_ById_ResolvesAScenarioExpenseFromTheStoredRow()
    {
        var expenseId = await SeedScenarioExpenseAsync();
        var handler = new ExpensesHandlers.GetQueryHandler(
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), AllRowsVisible(), new ScheduleMapper(),
            Substitute.For<ILogger<ExpensesHandlers.GetQueryHandler>>());
        _context.ChangeTracker.Clear();

        var result = await handler.Handle(new GetQuery<ExpensesResource>(expenseId), CancellationToken.None);

        result.Id.ShouldBe(expenseId);
    }

    [Test]
    public async Task ScenarioReplacementQueries_FindOnlyTheReplacementOfThatEmployeeDayAndScenario()
    {
        var changeId = await SeedScenarioWorkChangeAsync();
        var stored = await _context.WorkChange.SingleAsync(wc => wc.Id == changeId);
        stored.Type = WorkChangeType.ReplacementWithin;
        stored.ReplaceClientId = _clientId;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var repository = BuildWorkChangeRepository();

        var onTheDay = await repository.ListScenarioReplacementsAsync(_scenarioToken, _clientId, Day);
        var otherDay = await repository.ListScenarioReplacementsAsync(_scenarioToken, _clientId, Day.AddDays(1));
        var otherScenario = await repository.ListScenarioReplacementsAsync(Guid.NewGuid(), _clientId, Day);

        onTheDay.ShouldHaveSingleItem().Id.ShouldBe(changeId);
        onTheDay[0].Work.ShouldNotBeNull().ShiftId.ShouldBe(_shiftId);
        otherDay.ShouldBeEmpty();
        otherScenario.ShouldBeEmpty();
        (await repository.IsReplacementInScenarioAsync(_scenarioToken, _clientId)).ShouldBeTrue();
        (await repository.IsReplacementInScenarioAsync(Guid.NewGuid(), _clientId)).ShouldBeFalse();
        var slotStart = stored.StartTime;
        var slotEnd = stored.EndTime;
        (await repository.HasOverlappingScenarioReplacementAsync(_scenarioToken, _clientId, Day, slotStart, slotStart, slotEnd)).ShouldBeTrue();
        (await repository.HasOverlappingScenarioReplacementAsync(_scenarioToken, _clientId, Day.AddDays(3), slotStart, slotStart, slotEnd)).ShouldBeFalse();
        (await repository.HasOverlappingScenarioReplacementAsync(Guid.NewGuid(), _clientId, Day, slotStart, slotStart, slotEnd)).ShouldBeFalse();
    }

    [Test]
    public async Task AcceptingAnAlternative_SwitchesTheReplacement_AndDropsTheUntouchedProposal()
    {
        var oldStandInId = await AddExtraClientAsync("OldStandIn");
        var alternativeId = await AddExtraClientAsync("Alternative");
        var changeId = await SeedScenarioWorkChangeAsync();
        var seeded = await _context.WorkChange.SingleAsync(wc => wc.Id == changeId);
        seeded.Type = WorkChangeType.ReplacementWithin;
        seeded.ReplaceClientId = oldStandInId;
        var slot = (Start: seeded.StartTime, End: seeded.EndTime);
        _context.ReplacementRequests.Add(new ReplacementRequest
        {
            Id = Guid.NewGuid(),
            AbsentClientId = _clientId,
            CandidateClientId = oldStandInId,
            ShiftId = _shiftId,
            Date = Day,
            StartTime = slot.Start,
            EndTime = slot.End,
            Outcome = ReplacementRequestOutcome.Proposed,
            Source = ReplacementRequestSource.RecoveryEngine,
            ReportedAtUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            ShiftStartUtc = new DateTime(2026, 5, 6, 8, 0, 0, DateTimeKind.Utc),
            AnalyseToken = _scenarioToken,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await BuildContactHandler(alternativeId).Handle(
            new RecordReplacementContactCommand(new RecordReplacementContactRequest(
                _clientId, alternativeId, _shiftId, Day, slot.Start, slot.End, null, null, _scenarioToken,
                ReplacementRequestOutcome.Accepted)),
            CancellationToken.None);
        _context.ChangeTracker.Clear();

        result.IsScenarioReplacement.ShouldBeTrue();
        var stored = await _context.WorkChange.AsNoTracking().SingleAsync(wc => wc.Id == changeId);
        stored.ReplaceClientId.ShouldBe(alternativeId);
        stored.AnalyseToken.ShouldBe(_scenarioToken);
        stored.StartTime.ShouldBe(slot.Start);
        var rows = await _context.ReplacementRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.AbsentClientId == _clientId).ToListAsync();
        rows.Single(r => r.CandidateClientId == oldStandInId).IsDeleted.ShouldBeTrue();
        var accepted = rows.Single(r => r.CandidateClientId == alternativeId);
        accepted.IsDeleted.ShouldBeFalse();
        accepted.Outcome.ShouldBe(ReplacementRequestOutcome.Accepted);
    }

    [Test]
    public async Task RemoveScenarioMemberships_TranslatesOnPostgres()
    {
        var stager = new ScenarioOwnedRowStager(_context);

        await stager.RemoveScenarioMembershipsAsync(_scenarioToken, _clientId, Day, CancellationToken.None);

        _context.ChangeTracker.Entries().ShouldNotContain(e => e.State == EntityState.Deleted);
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

    private async Task<Guid> AddExtraClientAsync(string suffix)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = Prefix + suffix, FirstName = "Integration" };
        _extraClientIds.Add(client.Id);
        _context.Client.Add(client);
        await _context.SaveChangesAsync();
        return client.Id;
    }

    private RecordReplacementContactCommandHandler BuildContactHandler(Guid eligibleClientId)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReplacementSearchResult(
                [new ReplacementCandidate(eligibleClientId, "Alternative", false, [], 0m)], []));
        mediator.Send(Arg.Any<GetWorkChangeInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci => new WorkChangeHandlers.GetInScopeQueryHandler(
                    BuildWorkChangeRepository(), AllClientsVisible(), new ScheduleMapper(),
                    Substitute.For<ILogger<WorkChangeHandlers.GetInScopeQueryHandler>>())
                .Handle(ci.Arg<GetWorkChangeInScopeQuery>(), CancellationToken.None));
        mediator.Send(Arg.Any<PutCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => BuildWorkChangePutHandler().Handle(ci.Arg<PutCommand<WorkChangeResource>>(), CancellationToken.None));

        var scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        scenarioRepository.GetByTokenAsync(_scenarioToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = _scenarioToken, GroupId = Guid.NewGuid(), Status = AnalyseScenarioStatus.Active });
        var scenarioService = Substitute.For<IAnalyseScenarioService>();
        scenarioService.ResolveRootShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid>());

        var requestRepository = new ReplacementRequestRepository(_context, Substitute.For<ILogger<ReplacementRequest>>());
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTimeZoneAsync(Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);

        return new RecordReplacementContactCommandHandler(
            requestRepository,
            Substitute.For<IReplacementContactValidator>(),
            new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>()),
            Substitute.For<ISettingsReader>(),
            companyClock,
            TimeProvider.System,
            BuildHttpContextAccessor(),
            new ScenarioReplacementSwitcher(
                BuildWorkChangeRepository(), requestRepository, scenarioRepository, scenarioService, mediator));
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
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), AllRowsVisible(),
            new ScheduleMapper(), BuildUnitOfWork(), BuildPeriodHoursService(), BuildScheduleEntriesService(),
            Substitute.For<IWorkNotificationService>(), accessor, Substitute.For<IScheduleChangeTracker>(),
            BuildGroupResolver(accessor), DbBackedWorkRepository(), Substitute.For<IDayLockService>(), NewLockGuard(),
            Substitute.For<ILogger<ExpensesHandlers.PostCommandHandler>>());
    }

    private ExpensesHandlers.PutCommandHandler BuildExpensePutHandler()
    {
        var accessor = BuildHttpContextAccessor();
        return new ExpensesHandlers.PutCommandHandler(
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), AllRowsVisible(),
            new ScheduleMapper(), BuildUnitOfWork(), BuildPeriodHoursService(), BuildScheduleEntriesService(),
            Substitute.For<IWorkNotificationService>(), accessor, Substitute.For<IScheduleChangeTracker>(),
            BuildGroupResolver(accessor), DbBackedWorkRepository(), Substitute.For<IDayLockService>(), NewLockGuard(),
            Substitute.For<ILogger<ExpensesHandlers.PutCommandHandler>>());
    }

    private ExpensesHandlers.DeleteCommandHandler BuildExpenseDeleteHandler()
    {
        var accessor = BuildHttpContextAccessor();
        return new ExpensesHandlers.DeleteCommandHandler(
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), AllRowsVisible(),
            new ScheduleMapper(), BuildUnitOfWork(), BuildPeriodHoursService(), BuildScheduleEntriesService(),
            Substitute.For<IWorkNotificationService>(), accessor, Substitute.For<IScheduleChangeTracker>(),
            BuildGroupResolver(accessor), Substitute.For<IDayLockService>(), NewLockGuard(),
            Substitute.For<ILogger<ExpensesHandlers.DeleteCommandHandler>>());
    }

    private WorkChangeHandlers.PutCommandHandler BuildWorkChangePutHandler()
        => new(
            BuildWorkChangeRepository(), DbBackedWorkRepository(), AllRowsVisible(), new ScheduleMapper(),
            BuildPeriodHoursService(), BuildSavingCompletionService(), Substitute.For<IWorkChangeResultService>(),
            Substitute.For<IWorkNotificationFacade>(), Substitute.For<IDayLockService>(),
            Substitute.For<IReplacementRequestRecorder>(), BuildHttpContextAccessor(), NewLockGuard(),
            Substitute.For<IReplacementWriteGuard>(), Substitute.For<ILogger<WorkChangeHandlers.PutCommandHandler>>());

    private WorkChangeHandlers.DeleteCommandHandler BuildWorkChangeDeleteHandler()
        => new(
            BuildWorkChangeRepository(), DbBackedWorkRepository(), AllRowsVisible(), new ScheduleMapper(),
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

    private static IScheduleRowAccessGuard AllRowsVisible()
    {
        var scenarios = Substitute.For<IScenarioTokenAccessGuard>();
        scenarios.IsVisibleAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        return new Klacks.Api.Application.Services.Groups.ScheduleRowAccessGuard(AllClientsVisible(), scenarios);
    }

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
