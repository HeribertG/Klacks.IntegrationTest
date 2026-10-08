// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

// The in-scope reads of the assistant (GetExpenseInScopeQuery, GetWorkChangeInScopeQuery, ListExpensesInScopeQuery)
// against a real PostgreSQL database and a really group-restricted caller (ClientVisibilityGuard over
// ClientSearchRepository, scope restricted to one group): an expense on a Work of a foreign-group client and a
// WorkChange that moves a foreign-group client in as replacement are answered as not found and left out of the
// list, exactly like rows that do not exist; the rows of the visible client resolve. Seeds its own prefixed rows and
// cleans up only those.

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using ExpensesHandlers = Klacks.Api.Application.Handlers.Expenses;
using WorkChangeHandlers = Klacks.Api.Application.Handlers.WorkChanges;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
[Category("RealDatabase")]
public class ScenarioScopeVisibilityPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_SCOPEVIS_";
    private const string RestrictedUserId = "integration-test-scope-restricted-user";
    private static readonly DateOnly Day = new(2202, 4, 6);

    private DataBaseContext _context = null!;
    private ClientVisibilityGuard _guard = null!;

    private readonly Guid _visibleGroupId = Guid.NewGuid();
    private readonly Guid _foreignGroupId = Guid.NewGuid();
    private readonly Guid _ownClientId = Guid.NewGuid();
    private readonly Guid _foreignClientId = Guid.NewGuid();
    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly Guid _ownWorkId = Guid.NewGuid();
    private readonly Guid _foreignWorkId = Guid.NewGuid();
    private readonly Guid _scenarioToken = Guid.NewGuid();
    private readonly Guid _ownExpenseId = Guid.NewGuid();
    private readonly Guid _foreignExpenseId = Guid.NewGuid();
    private readonly Guid _ownChangeId = Guid.NewGuid();
    private readonly Guid _hiddenReplacementChangeId = Guid.NewGuid();

    [SetUp]
    public async Task Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var groupVisibility = Substitute.For<IGroupVisibilityService>();
        groupVisibility.GetVisibilityScopeAsync()
            .Returns(GroupVisibilityScope.Restricted([_visibleGroupId], [_visibleGroupId]));
        var userService = Substitute.For<IUserService>();
        userService.GetIdString().Returns(RestrictedUserId);
        var groupFilter = new ClientGroupFilterService(
            Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>(),
            groupVisibility,
            userService,
            Substitute.For<ILogger<ClientGroupFilterService>>());
        var clientRepository = new ClientSearchRepository(
            _context, groupFilter, Substitute.For<IClientFuzzySearchService>(), TestCompanyClock.Utc());
        _guard = new ClientVisibilityGuard(clientRepository, groupFilter);

        await CleanupAsync();
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        _context.Dispose();
    }

    [Test]
    public async Task GetExpenseInScope_HiddenOwner_IsNotFound_VisibleOwnerResolves()
    {
        var handler = new ExpensesHandlers.GetInScopeQueryHandler(
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), _guard, new ScheduleMapper(),
            Substitute.For<ILogger<ExpensesHandlers.GetInScopeQueryHandler>>());

        var own = await handler.Handle(new GetExpenseInScopeQuery(_ownExpenseId, _scenarioToken), CancellationToken.None);
        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new GetExpenseInScopeQuery(_foreignExpenseId, _scenarioToken), CancellationToken.None));

        own.Id.ShouldBe(_ownExpenseId);
        hidden.Message.ShouldBe($"Expenses with ID {_foreignExpenseId} not found");
        hidden.Message.ShouldNotContain(_foreignClientId.ToString());
    }

    [Test]
    public async Task GetWorkChangeInScope_HiddenReplacementClient_IsNotFound_VisibleChangeResolves()
    {
        var handler = new WorkChangeHandlers.GetInScopeQueryHandler(
            new WorkChangeRepository(_context, Substitute.For<ILogger<WorkChange>>(), Substitute.For<IWorkMacroService>()),
            _guard, new ScheduleMapper(), Substitute.For<ILogger<WorkChangeHandlers.GetInScopeQueryHandler>>());

        var own = await handler.Handle(new GetWorkChangeInScopeQuery(_ownChangeId, _scenarioToken), CancellationToken.None);
        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new GetWorkChangeInScopeQuery(_hiddenReplacementChangeId, _scenarioToken), CancellationToken.None));

        own.Id.ShouldBe(_ownChangeId);
        hidden.Message.ShouldBe($"WorkChange with ID {_hiddenReplacementChangeId} not found");
    }

    [Test]
    public async Task ListExpensesInScope_LeavesOutTheHiddenOwnersExpense()
    {
        var workRepository = Substitute.For<IWorkRepository>();
        workRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IEnumerable<Guid>>().ToList();
                return _context.Work.AsNoTracking().Where(w => ids.Contains(w.Id)).ToListAsync();
            });
        var handler = new ExpensesHandlers.ListQueryHandler(
            new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>()), workRepository, _guard,
            new ScheduleMapper(), Substitute.For<ILogger<ExpensesHandlers.ListQueryHandler>>());

        var ids = (await handler.Handle(new ListExpensesInScopeQuery(_scenarioToken), CancellationToken.None))
            .Select(e => e.Id)
            .ToList();

        ids.ShouldContain(_ownExpenseId);
        ids.ShouldNotContain(_foreignExpenseId);
    }

    private async Task SeedAsync()
    {
        _context.Group.AddRange(
            new Group { Id = _visibleGroupId, Name = TestPrefix + "Visible", Description = "IT visible group", ValidFrom = DateTime.UtcNow.AddYears(-1) },
            new Group { Id = _foreignGroupId, Name = TestPrefix + "Foreign", Description = "IT foreign group", ValidFrom = DateTime.UtcNow.AddYears(-1) });
        _context.Client.AddRange(NewClient(_ownClientId, "Own"), NewClient(_foreignClientId, "Foreign"));
        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = TestPrefix + "Shift",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
        });
        await _context.SaveChangesAsync();

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _ownClientId, GroupId = _visibleGroupId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _foreignClientId, GroupId = _foreignGroupId });
        _context.Work.AddRange(NewWork(_ownWorkId, _ownClientId), NewWork(_foreignWorkId, _foreignClientId));
        await _context.SaveChangesAsync();

        _context.Expenses.AddRange(
            NewExpense(_ownExpenseId, _ownWorkId),
            NewExpense(_foreignExpenseId, _foreignWorkId));
        _context.WorkChange.AddRange(
            NewChange(_ownChangeId, null),
            NewChange(_hiddenReplacementChangeId, _foreignClientId));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Work NewWork(Guid id, Guid clientId) => new()
    {
        Id = id,
        ClientId = clientId,
        ShiftId = _shiftId,
        CurrentDate = Day,
        WorkTime = 8,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        AnalyseToken = _scenarioToken,
    };

    private Expenses NewExpense(Guid id, Guid workId) => new()
    {
        Id = id,
        WorkId = workId,
        Amount = 10m,
        Description = TestPrefix + "Expense",
        AnalyseToken = _scenarioToken,
    };

    private WorkChange NewChange(Guid id, Guid? replaceClientId) => new()
    {
        Id = id,
        WorkId = _ownWorkId,
        ChangeTime = 1m,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(9, 0),
        Type = replaceClientId.HasValue ? WorkChangeType.ReplacementWithin : WorkChangeType.CorrectionStart,
        ReplaceClientId = replaceClientId,
        Description = TestPrefix + "Change",
        AnalyseToken = _scenarioToken,
    };

    private static Client NewClient(Guid id, string suffix) => new()
    {
        Id = id,
        Name = TestPrefix + suffix,
        FirstName = "Scope",
        Type = EntityTypeEnum.Employee,
        Gender = GenderEnum.Female,
        LegalEntity = false
    };

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM expenses WHERE description LIKE {0} AND work_id IN ({1}, {2})",
            TestPrefix + "%", _ownWorkId, _foreignWorkId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work_change WHERE description LIKE {0} AND work_id = {1}", TestPrefix + "%", _ownWorkId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE id IN ({0}, {1}) AND client_id IN ({2}, {3})",
            _ownWorkId, _foreignWorkId, _ownClientId, _foreignClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0} AND name LIKE {1}", _shiftId, TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE name LIKE {0})", TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client WHERE name LIKE {0}", TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM \"group\" WHERE name LIKE {0}", TestPrefix + "%");
    }
}
