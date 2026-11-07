// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The Work write chain wired against PostgreSQL the way production runs it: one DataBaseContext with
/// EnableRetryOnFailure, the real UnitOfWork, WorkRepository, ContainerWorkCascadeService, PeriodHoursService,
/// ScheduleChangeTracker, ScheduleCompletionService, WorkNotificationFacade, ScheduleEntriesService, ParentWorkLockGuard
/// and WorkHandOverService. Substituted: the SignalR senders (calls are counted), the timeline queue, the Work macro
/// (surcharges stay as given, the chain is under test, not the macro math), container expansion and default expenses,
/// the contract data (fixed 40 h target), every visibility, day-lock, planning-period and write guard as pass-through,
/// the restore authorizer (owner), the softening restore, the moved-work reversal and the new-owner recalculation
/// (succeeds). The overtime cascade is a substitute unless a test sets its own.
/// </summary>
/// <param name="interceptors">Interceptors added to the handler context, e.g. to fail one save of the chain</param>

using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.ScheduleEntries;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.IntegrationTest.Works;

public sealed class WorkWriteChainStack : IAsyncDisposable
{
    public const decimal GuaranteedHours = 40m;

    private const int DaysPerWeek = 7;

    private readonly ScheduleMapper _scheduleMapper = new();
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly WorkRepository _workRepository;
    private readonly ContainerWorkCascadeService _containerCascade;
    private readonly ScheduleCompletionService _completionService;
    private readonly WorkNotificationFacade _notificationFacade;
    private readonly ScheduleEntriesService _scheduleEntriesService;
    private readonly ISelectedGroupContextResolver _groupContextResolver;

    public WorkWriteChainStack(params IInterceptor[] interceptors)
    {
        Context = WorkChainTestData.NewRetryingContext(interceptors);
        UnitOfWork = new UnitOfWork(Context, Substitute.For<ILogger<UnitOfWork>>());

        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);

        PeriodHoursService = new PeriodHoursService(
            Context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            WorkNotifications,
            Substitute.For<IClientGroupFilterService>(),
            FixedContractData(),
            MondayWeeks());

        _workRepository = new WorkRepository(
            Context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IClientContractDataProvider>());
        _containerCascade = new ContainerWorkCascadeService(Context, Substitute.For<ILogger<ContainerWorkCascadeService>>());
        _completionService = new ScheduleCompletionService(
            UnitOfWork,
            new ScheduleChangeTracker(Context, WorkNotifications, _httpContextAccessor),
            Timeline,
            PeriodHoursService,
            _httpContextAccessor);
        _notificationFacade = new WorkNotificationFacade(
            WorkNotifications, ShiftStatsNotifications, OneAssignmentPerShiftDay(), _scheduleMapper, _httpContextAccessor);
        _scheduleEntriesService = new ScheduleEntriesService(Context, Substitute.For<ILogger<ScheduleEntriesService>>());
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid>());
    }

    public DataBaseContext Context { get; }

    public UnitOfWork UnitOfWork { get; }

    public PeriodHoursService PeriodHoursService { get; }

    public IWorkNotificationService WorkNotifications { get; } = Substitute.For<IWorkNotificationService>();

    public IShiftStatsNotificationService ShiftStatsNotifications { get; } = Substitute.For<IShiftStatsNotificationService>();

    public IScheduleTimelineService Timeline { get; } = Substitute.For<IScheduleTimelineService>();

    public IOvertimeCascadeService OvertimeCascade { get; set; } = Substitute.For<IOvertimeCascadeService>();

    public PostCommandHandler NewPostHandler()
        => new(
            _workRepository, AllClientsVisible(), _scheduleMapper, PeriodHoursService, _scheduleEntriesService,
            _completionService, _notificationFacade, Substitute.For<IShiftDefaultExpensesApplier>(),
            Substitute.For<IContainerWorkExpansionService>(), _groupContextResolver, Substitute.For<IDayLockService>(),
            UnitOfWork, OvertimeCascade, Substitute.For<IWorkWriteGuard>(), Substitute.For<IClientPlanningPeriodGuard>(),
            AllShiftsVisible(), Substitute.For<ILogger<PostCommandHandler>>());

    public PutCommandHandler NewPutHandler()
        => new(
            _workRepository, AllRowsVisible(), _scheduleMapper, PeriodHoursService, _scheduleEntriesService,
            _completionService, _notificationFacade, _containerCascade, _groupContextResolver,
            Substitute.For<IDayLockService>(), UnitOfWork, OvertimeCascade, Substitute.For<IWorkWriteGuard>(),
            _httpContextAccessor, NewWorkLockGuard(), Substitute.For<IClientPlanningPeriodGuard>(), AllShiftsVisible(),
            Substitute.For<ILogger<PutCommandHandler>>());

    public DeleteCommandHandler NewDeleteHandler()
        => new(
            _workRepository, AllRowsVisible(), _scheduleMapper, PeriodHoursService, _scheduleEntriesService,
            _completionService, _notificationFacade, _containerCascade, _groupContextResolver,
            Substitute.For<IDayLockService>(), UnitOfWork, OvertimeCascade, _httpContextAccessor, NewWorkLockGuard(),
            Substitute.For<ILogger<DeleteCommandHandler>>());

    public BulkDeleteWorksCommandHandler NewBulkDeleteHandler()
        => new(
            _workRepository, AllRowsVisible(), _scheduleMapper, PeriodHoursService, _completionService,
            _notificationFacade, OvertimeCascade, _containerCascade, Substitute.For<IDayLockService>(),
            _httpContextAccessor, NewWorkLockGuard(), Substitute.For<ILogger<BulkDeleteWorksCommandHandler>>());

    public RestoreWorkCommandHandler NewRestoreHandler()
    {
        var authorizer = Substitute.For<IWorkRestoreAuthorizer>();
        authorizer.Resolve(Arg.Any<Work>()).Returns(WorkRestoreAccess.Owner);
        return new(
            _workRepository, AllRowsVisible(), _scheduleMapper, PeriodHoursService, _scheduleEntriesService,
            _completionService, _notificationFacade, _containerCascade, Substitute.For<IWorkSofteningRepository>(),
            _groupContextResolver, Substitute.For<IDayLockService>(), Substitute.For<IWorkWriteGuard>(), authorizer,
            UnitOfWork, OvertimeCascade, Substitute.For<ILogger<RestoreWorkCommandHandler>>());
    }

    public ReassignWorkClientCommandHandler NewReassignHandler()
    {
        var childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        childrenManager.RecalculateForNewOwnerAsync(Arg.Any<Work>(), Arg.Any<CancellationToken>()).Returns(true);
        return new(
            _workRepository, AllRowsVisible(), _scheduleMapper, PeriodHoursService, _scheduleEntriesService,
            _completionService, _notificationFacade, _containerCascade,
            new WorkHandOverService(_containerCascade, childrenManager), _groupContextResolver,
            Substitute.For<IDayLockService>(), UnitOfWork, OvertimeCascade, Substitute.For<IWorkWriteGuard>(),
            _httpContextAccessor, NewWorkLockGuard(), Substitute.For<IClientPlanningPeriodGuard>(),
            Substitute.For<IMovedWorkReversal>(), Substitute.For<ILogger<ReassignWorkClientCommandHandler>>());
    }

    public async ValueTask DisposeAsync() => await Context.DisposeAsync();

    private static ParentWorkLockGuard NewWorkLockGuard() => new(new WorkLockLevelService());

    private static IClientVisibilityGuard AllClientsVisible()
    {
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    private static IScheduleRowAccessGuard AllRowsVisible()
    {
        var guard = Substitute.For<IScheduleRowAccessGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        guard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Work>>(), Arg.Any<Func<Work, Guid>>(), Arg.Any<Func<Work, Guid?>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Work>>().ToList());
        return guard;
    }

    private static IShiftVisibilityGuard AllShiftsVisible()
    {
        var guard = Substitute.For<IShiftVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    private static IShiftScheduleReader OneAssignmentPerShiftDay()
    {
        var reader = Substitute.For<IShiftScheduleReader>();
        reader.GetShiftSchedulePartialAsync(
                Arg.Any<List<(Guid ShiftId, DateOnly Date)>>(), Arg.Any<Guid?>(), Arg.Any<List<Guid>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<List<(Guid ShiftId, DateOnly Date)>>()
                .Select(pair => new ShiftDayAssignment { ShiftId = pair.ShiftId, Date = pair.Date })
                .ToList());
        return reader;
    }

    private static IWeekConfiguration MondayWeeks()
    {
        var weeks = Substitute.For<IWeekConfiguration>();
        weeks.GetWeekStartDayAsync(Arg.Any<CancellationToken>()).Returns(DayOfWeek.Monday);
        weeks.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var date = call.Arg<DateOnly>();
                return date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Monday + DaysPerWeek) % DaysPerWeek));
            });
        return weeks;
    }

    private static IClientContractDataProvider FixedContractData()
    {
        var provider = Substitute.For<IClientContractDataProvider>();
        var data = new EffectiveContractData { GuaranteedHours = GuaranteedHours, HasActiveContract = true };
        provider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>()).Returns(data);
        provider.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(call => call.Arg<List<Guid>>().ToDictionary(id => id, _ => data));
        return provider;
    }
}
