// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Common;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Expenses;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Klacks.Api.Application.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

/// <summary>
/// Verifies that Expenses CRUD handlers ship the three-day schedule snapshot
/// in their response, so the frontend can update the grid in place without
/// a separate /Works/Schedule round-trip (which used to paginate to 200
/// clients and silently drop any client outside the alphabetical window).
/// </summary>
[TestFixture]
public class ExpensesRefreshIntegrationTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;

    private Guid _clientId;
    private Guid _shiftId;
    private Guid _workId;

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

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        await SetupTestData();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestData();
        _context?.Dispose();
    }

    private async Task SetupTestData()
    {
        _clientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _workId = Guid.NewGuid();

        _context.Client.Add(new Client
        {
            Id = _clientId,
            Name = "TEST_ExpensesRefresh",
            FirstName = "Integration",
            IsDeleted = false,
        });

        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = "TEST_Shift_ExpensesRefresh",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsDeleted = false,
        });

        _context.Work.Add(new Work
        {
            Id = _workId,
            ClientId = _clientId,
            ShiftId = _shiftId,
            CurrentDate = new DateOnly(2026, 5, 6),
            WorkTime = 480,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            IsDeleted = false,
        });

        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestData()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM expenses WHERE work_id = {0}", _workId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE id = {0}", _workId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0}", _shiftId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id = {0}", _clientId);
    }

    [Test]
    public async Task PostExpenses_ReturnsScheduleEntriesForClient()
    {
        var handler = BuildPostHandler();
        var command = new PostCommand<ExpensesResource>(new ExpensesResource
        {
            WorkId = _workId,
            Amount = 25.50m,
            Description = "Integration test expense",
            Taxable = true,
        });

        var response = await handler.Handle(command, CancellationToken.None);

        response.ShouldNotBeNull();
        response!.ScheduleEntries.ShouldNotBeNull();
        response.ScheduleEntries.Count.ShouldBeGreaterThan(0,
            "The Post handler must ship the three-day schedule snapshot so the " +
            "frontend can update in place without a separate refresh round-trip.");
        response.ScheduleEntries.ShouldContain(e => e.SourceId == _workId);
    }

    [Test]
    public async Task DeleteExpenses_ReturnsScheduleEntriesForClient()
    {
        var existingExpense = new Expenses
        {
            Id = Guid.NewGuid(),
            WorkId = _workId,
            Amount = 12m,
            Description = "Pre-existing expense",
            Taxable = false,
            IsDeleted = false,
        };
        _context.Expenses.Add(existingExpense);
        await _context.SaveChangesAsync();

        var handler = BuildDeleteHandler();
        var response = await handler.Handle(
            new DeleteCommand<ExpensesResource>(existingExpense.Id),
            CancellationToken.None);

        response.ShouldNotBeNull();
        response!.ScheduleEntries.ShouldNotBeNull();
        response.ScheduleEntries.Count.ShouldBeGreaterThan(0,
            "The Delete handler must ship the three-day schedule snapshot so " +
            "the frontend can clear the deleted expense and refresh the grid in place.");
        response.ScheduleEntries.ShouldContain(e => e.SourceId == _workId);
    }

    private Klacks.Api.Application.Handlers.Expenses.PostCommandHandler BuildPostHandler()
    {
        var expensesRepo = new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>());
        var unitOfWork = BuildUnitOfWork();
        var scheduleMapper = new ScheduleMapper();
        var periodHoursService = Substitute.For<IPeriodHoursService>();
        periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns((new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31)));
        var scheduleEntriesService = new ScheduleEntriesService(
            _context, Substitute.For<ILogger<ScheduleEntriesService>>());
        var notificationService = Substitute.For<IWorkNotificationService>();
        var changeTracker = Substitute.For<IScheduleChangeTracker>();
        var httpContextAccessor = BuildHttpContextAccessorWithoutGroupHeader();
        var groupResolver = new SelectedGroupContextResolver(
            httpContextAccessor, Substitute.For<IShiftGroupFilterService>());

        return new Klacks.Api.Application.Handlers.Expenses.PostCommandHandler(
            expensesRepo,
            scheduleMapper,
            unitOfWork,
            periodHoursService,
            scheduleEntriesService,
            notificationService,
            httpContextAccessor,
            changeTracker,
            groupResolver,
            Substitute.For<ILogger<Klacks.Api.Application.Handlers.Expenses.PostCommandHandler>>());
    }

    private Klacks.Api.Application.Handlers.Expenses.DeleteCommandHandler BuildDeleteHandler()
    {
        var expensesRepo = new ExpensesRepository(_context, Substitute.For<ILogger<Expenses>>());
        var unitOfWork = BuildUnitOfWork();
        var scheduleMapper = new ScheduleMapper();
        var periodHoursService = Substitute.For<IPeriodHoursService>();
        periodHoursService.GetPeriodBoundariesAsync(Arg.Any<DateOnly>())
            .Returns((new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31)));
        var scheduleEntriesService = new ScheduleEntriesService(
            _context, Substitute.For<ILogger<ScheduleEntriesService>>());
        var notificationService = Substitute.For<IWorkNotificationService>();
        var changeTracker = Substitute.For<IScheduleChangeTracker>();
        var httpContextAccessor = BuildHttpContextAccessorWithoutGroupHeader();
        var groupResolver = new SelectedGroupContextResolver(
            httpContextAccessor, Substitute.For<IShiftGroupFilterService>());

        return new Klacks.Api.Application.Handlers.Expenses.DeleteCommandHandler(
            expensesRepo,
            scheduleMapper,
            unitOfWork,
            periodHoursService,
            scheduleEntriesService,
            notificationService,
            httpContextAccessor,
            changeTracker,
            groupResolver,
            Substitute.For<ILogger<Klacks.Api.Application.Handlers.Expenses.DeleteCommandHandler>>());
    }

    private static IHttpContextAccessor BuildHttpContextAccessorWithoutGroupHeader()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HttpHeaderNames.SignalRConnectionId] = string.Empty;
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }

    private IUnitOfWork BuildUnitOfWork()
    {
        var uow = Substitute.For<IUnitOfWork>();
        uow.CompleteAsync().Returns(_ => _context.SaveChangesAsync());
        return uow;
    }
}
