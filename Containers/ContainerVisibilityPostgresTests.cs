// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end group visibility of container templates, overrides and edit locks against PostgreSQL (IT database klacks)
/// with the real UserService, GroupVisibilityService, ShiftGroupFilterService, ShiftVisibilityReadRepository,
/// ShiftVisibilityGuard, ContainerVisibilityGuard, ContainerTemplateRepository, ContainerShiftOverrideRepository,
/// ContainerLockRepository and UnitOfWork. Callers: a Supervisor who sees one root group and an admin. A hidden
/// container is answered exactly like a missing one on every endpoint and nothing is written; a container mixing
/// groups stays saveable for the Supervisor while a new hidden task is refused; an item of another template cannot be
/// overwritten; an override addressed through the wrong container is treated as missing; a lock on a hidden container
/// is refused before a lock row is written. Every row carries the INTEGRATION_TEST_CONTAINERVIS_ prefix (shifts, groups,
/// users) and the cleanup deletes exactly those rows and the container rows hanging off them.
/// </summary>

using Klacks.Api.Application.Commands.ContainerShiftOverrides;
using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.ContainerShiftOverrides;
using Klacks.Api.Application.Handlers.ContainerTemplates;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerShiftOverrides;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Containers;

[TestFixture]
[Category("RealDatabase")]
public class ContainerVisibilityPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_CONTAINERVIS_";
    private const string IdPlaceholder = "<id>";
    private const string SelfInstanceId = "";
    private const int Monday = 1;

    private static readonly DateOnly Day = new(2026, 6, 1);
    private static readonly TimeOnly ChangedStart = new(9, 30);

    private DataBaseContext _context = null!;
    private ScheduleMapper _mapper = null!;
    private UnitOfWork _unitOfWork = null!;
    private UserService _userService = null!;
    private ContainerTemplateRepository _templateRepository = null!;
    private ContainerShiftOverrideRepository _overrideRepository = null!;
    private ContainerLockRepository _lockRepository = null!;
    private ShiftVisibilityGuard _shiftGuard = null!;
    private ContainerVisibilityGuard _containerGuard = null!;

    private AppUser _supervisor = null!;
    private AppUser _admin = null!;
    private Guid _visibleRootId;
    private Guid _hiddenRootId;
    private Shift _visibleContainer = null!;
    private Shift _hiddenContainer = null!;
    private Shift _visibleTask = null!;
    private Shift _hiddenTask = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        await PopulateAsync();
        BuildPipeline();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task GetTemplates_Supervisor_HiddenContainer_IsAnsweredLikeAMissingOne()
    {
        await SeedTemplateAsync(_hiddenContainer.Id, Monday, _visibleTask.Id);
        var missingId = Guid.NewGuid();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => GetTemplatesHandler()
            .Handle(new GetContainerTemplatesQuery(_hiddenContainer.Id), CancellationToken.None)));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => GetTemplatesHandler()
            .Handle(new GetContainerTemplatesQuery(missingId), CancellationToken.None)));

        AssertAlike(hidden, _hiddenContainer.Id, missing, missingId);
        hidden.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenContainer.Id));
    }

    [Test]
    public async Task GetTemplates_Admin_HiddenContainerIsReturned_MissingContainerIs404_EmptyVisibleContainerIsEmpty()
    {
        await SeedTemplateAsync(_hiddenContainer.Id, Monday, _visibleTask.Id);

        var hidden = await AsAsync(_admin, () => GetTemplatesHandler()
            .Handle(new GetContainerTemplatesQuery(_hiddenContainer.Id), CancellationToken.None));
        await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_admin, () => GetTemplatesHandler()
            .Handle(new GetContainerTemplatesQuery(Guid.NewGuid()), CancellationToken.None)));
        var empty = await AsAsync(_supervisor, () => GetTemplatesHandler()
            .Handle(new GetContainerTemplatesQuery(_visibleContainer.Id), CancellationToken.None));

        hidden.Count.ShouldBe(1);
        empty.ShouldBeEmpty();
    }

    [Test]
    public async Task PostTemplates_Supervisor_HiddenContainer_IsAnsweredLikeAMissingOne_NothingWritten()
    {
        await SeedLockAsync(ContainerLockResourceTypes.ContainerTemplate, _hiddenContainer.Id, _supervisor);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PostTemplatesHandler()
            .Handle(new PostContainerTemplatesCommand(_hiddenContainer.Id, [NewTemplate(_hiddenContainer.Id, _visibleTask.Id)]),
                CancellationToken.None)));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenContainer.Id));
        (await StoredTemplatesAsync(_hiddenContainer.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task PostTemplates_Supervisor_NewHiddenTask_IsAnsweredLikeAMissingShift_NothingWritten()
    {
        await SeedLockAsync(ContainerLockResourceTypes.ContainerTemplate, _visibleContainer.Id, _supervisor);
        var missingId = Guid.NewGuid();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PostTemplatesHandler()
            .Handle(new PostContainerTemplatesCommand(_visibleContainer.Id, [NewTemplate(_visibleContainer.Id, _visibleTask.Id, _hiddenTask.Id)]),
                CancellationToken.None)));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PostTemplatesHandler()
            .Handle(new PostContainerTemplatesCommand(_visibleContainer.Id, [NewTemplate(_visibleContainer.Id, missingId)]),
                CancellationToken.None)));

        AssertAlike(hidden, _hiddenTask.Id, missing, missingId);
        (await StoredTemplatesAsync(_visibleContainer.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task PutTemplates_Supervisor_HiddenContainer_IsAnsweredLikeAMissingOne_TemplateUnchanged()
    {
        var stored = await SeedTemplateAsync(_hiddenContainer.Id, Monday, _hiddenTask.Id);
        await SeedLockAsync(ContainerLockResourceTypes.ContainerTemplate, _hiddenContainer.Id, _supervisor);
        var resource = ResourceOf(stored);
        resource.ContainerTemplateItems.Single().StartItem = ChangedStart;
        var missingId = Guid.NewGuid();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PutTemplatesHandler()
            .Handle(new PutContainerTemplatesCommand(_hiddenContainer.Id, [resource]), CancellationToken.None)));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PutTemplatesHandler()
            .Handle(new PutContainerTemplatesCommand(missingId, [resource]), CancellationToken.None)));

        AssertAlike(hidden, _hiddenContainer.Id, missing, missingId);
        (await StoredTemplatesAsync(_hiddenContainer.Id)).Single().ContainerTemplateItems.Single().StartItem.ShouldBeNull();
    }

    [Test]
    public async Task PostOverride_Supervisor_HiddenContainer_IsAnsweredLikeAMissingOne_NothingWritten()
    {
        await SeedLockAsync(ContainerLockResourceTypes.ContainerShiftOverride, _hiddenContainer.Id, _supervisor);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PostOverrideHandler()
            .Handle(new PostContainerShiftOverrideCommand(_hiddenContainer.Id, OverrideResource(_hiddenContainer.Id, _visibleTask.Id)),
                CancellationToken.None)));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenContainer.Id));
        (await _context.ContainerShiftOverrides.AsNoTracking().CountAsync(o => o.ContainerId == _hiddenContainer.Id)).ShouldBe(0);
    }

    [Test]
    public async Task PutTemplates_Supervisor_MixedGroupContainer_StaysSaveable()
    {
        var stored = await SeedTemplateAsync(_visibleContainer.Id, Monday, _hiddenTask.Id, _visibleTask.Id);
        await SeedLockAsync(ContainerLockResourceTypes.ContainerTemplate, _visibleContainer.Id, _supervisor);
        var resource = ResourceOf(stored);
        resource.ContainerTemplateItems.First(i => i.ShiftId == _visibleTask.Id).StartItem = ChangedStart;

        await AsAsync(_supervisor, () => PutTemplatesHandler()
            .Handle(new PutContainerTemplatesCommand(_visibleContainer.Id, [resource]), CancellationToken.None));

        var items = (await StoredTemplatesAsync(_visibleContainer.Id)).Single().ContainerTemplateItems;
        items.Select(i => i.ShiftId).ShouldBe([_hiddenTask.Id, _visibleTask.Id], ignoreOrder: true);
        items.Single(i => i.ShiftId == _visibleTask.Id).StartItem.ShouldBe(ChangedStart);
    }

    [Test]
    public async Task PutTemplates_Supervisor_AddingAHiddenTask_IsAnsweredLikeAMissingShift_TemplateUnchanged()
    {
        var stored = await SeedTemplateAsync(_visibleContainer.Id, Monday, _visibleTask.Id);
        await SeedLockAsync(ContainerLockResourceTypes.ContainerTemplate, _visibleContainer.Id, _supervisor);
        var resource = ResourceOf(stored);
        resource.ContainerTemplateItems.Add(new ContainerTemplateItemResource { ShiftId = _hiddenTask.Id });

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PutTemplatesHandler()
            .Handle(new PutContainerTemplatesCommand(_visibleContainer.Id, [resource]), CancellationToken.None)));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenTask.Id));
        (await StoredTemplatesAsync(_visibleContainer.Id)).Single().ContainerTemplateItems
            .Select(i => i.ShiftId).ShouldBe([_visibleTask.Id]);
    }

    [Test]
    public async Task PutTemplates_Admin_ItemOfAnotherContainersTemplate_CannotBeOverwritten()
    {
        var own = await SeedTemplateAsync(_visibleContainer.Id, Monday, _visibleTask.Id);
        var foreign = await SeedTemplateAsync(_hiddenContainer.Id, Monday, _hiddenTask.Id);
        var foreignItem = foreign.ContainerTemplateItems.Single();
        await SeedLockAsync(ContainerLockResourceTypes.ContainerTemplate, _visibleContainer.Id, _admin);
        var resource = ResourceOf(own);
        resource.ContainerTemplateItems.Add(new ContainerTemplateItemResource
        {
            Id = foreignItem.Id,
            ShiftId = _visibleTask.Id,
            StartItem = ChangedStart
        });

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_admin, () => PutTemplatesHandler()
            .Handle(new PutContainerTemplatesCommand(_visibleContainer.Id, [resource]), CancellationToken.None)));

        ex.Message.ShouldBe(string.Format(ContainerTemplateMessages.ItemNotFound, foreignItem.Id));
        var reloaded = await _context.ContainerTemplateItem.AsNoTracking().SingleAsync(i => i.Id == foreignItem.Id);
        reloaded.ContainerTemplateId.ShouldBe(foreign.Id);
        reloaded.ShiftId.ShouldBe(_hiddenTask.Id);
        reloaded.StartItem.ShouldBeNull();
    }

    [Test]
    public async Task UpdateItem_ItemOfAnotherTemplate_IsAnsweredLikeAMissingItem()
    {
        var own = await SeedTemplateAsync(_visibleContainer.Id, Monday, _visibleTask.Id);
        var foreign = await SeedTemplateAsync(_hiddenContainer.Id, Monday, _hiddenTask.Id);
        var foreignItem = foreign.ContainerTemplateItems.Single();
        var missingId = Guid.NewGuid();

        var wrong = await Should.ThrowAsync<KeyNotFoundException>(() => _templateRepository.UpdateItem(
            own.Id, new ContainerTemplateItem { Id = foreignItem.Id, ShiftId = _visibleTask.Id }));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => _templateRepository.UpdateItem(
            own.Id, new ContainerTemplateItem { Id = missingId, ShiftId = _visibleTask.Id }));

        AssertAlike(wrong, foreignItem.Id, missing, missingId);
    }

    [Test]
    public async Task DeleteTemplates_Supervisor_HiddenContainer_IsAnsweredLikeAMissingOne_TemplatesKept()
    {
        await SeedTemplateAsync(_hiddenContainer.Id, Monday, _hiddenTask.Id);
        await SeedLockAsync(ContainerLockResourceTypes.ContainerTemplate, _hiddenContainer.Id, _supervisor);

        await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => DeleteTemplatesHandler()
            .Handle(new DeleteContainerTemplatesCommand(_hiddenContainer.Id), CancellationToken.None)));

        (await StoredTemplatesAsync(_hiddenContainer.Id)).Count.ShouldBe(1);
    }

    [Test]
    public async Task AvailableTasks_Supervisor_HiddenContainerIs404_VisibleContainerOffersOnlyVisibleTasks()
    {
        var service = Substitute.For<IContainerAvailableTasksService>();
        service.GetAvailableTasksAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<TimeOnly>(), Arg.Any<TimeOnly>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<Shift> { new() { Id = _visibleTask.Id }, new() { Id = _hiddenTask.Id } });
        var handler = new GetAvailableTasksQueryHandler(service, _mapper, _containerGuard, _shiftGuard);

        await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => handler.Handle(
            new GetAvailableTasksQuery(_hiddenContainer.Id, Monday, new TimeOnly(6, 0), new TimeOnly(22, 0)), CancellationToken.None)));
        var offered = await AsAsync(_supervisor, () => handler.Handle(
            new GetAvailableTasksQuery(_visibleContainer.Id, Monday, new TimeOnly(6, 0), new TimeOnly(22, 0)), CancellationToken.None));
        var offeredToAdmin = await AsAsync(_admin, () => handler.Handle(
            new GetAvailableTasksQuery(_visibleContainer.Id, Monday, new TimeOnly(6, 0), new TimeOnly(22, 0)), CancellationToken.None));

        offered.Select(s => s.Id).ShouldBe([_visibleTask.Id]);
        offeredToAdmin.Select(s => s.Id).ShouldBe([_visibleTask.Id, _hiddenTask.Id], ignoreOrder: true);
    }

    [Test]
    public async Task Overrides_Supervisor_HiddenContainer_IsAnsweredLikeAMissingOneOnEveryEndpoint_NothingChanged()
    {
        var stored = await SeedOverrideAsync(_hiddenContainer.Id, _hiddenTask.Id);
        await SeedLockAsync(ContainerLockResourceTypes.ContainerShiftOverride, _hiddenContainer.Id, _supervisor);
        var text = string.Format(ShiftVisibilityMessages.ShiftNotFound, _hiddenContainer.Id);

        (await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () =>
            new GetContainerShiftOverrideQueryHandler(_overrideRepository, _mapper, _containerGuard)
                .Handle(new GetContainerShiftOverrideQuery(_hiddenContainer.Id, Day), CancellationToken.None)))).Message.ShouldBe(text);
        (await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () =>
            new GetContainerShiftOverridesForRangeQueryHandler(_overrideRepository, _mapper, _containerGuard)
                .Handle(new GetContainerShiftOverridesForRangeQuery(_hiddenContainer.Id, Day, Day), CancellationToken.None)))).Message.ShouldBe(text);
        (await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PutOverrideHandler()
            .Handle(new PutContainerShiftOverrideCommand(_hiddenContainer.Id, stored.Id, OverrideResource(_hiddenContainer.Id)),
                CancellationToken.None)))).Message.ShouldBe(text);
        (await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => DeleteOverrideHandler()
            .Handle(new DeleteContainerShiftOverrideCommand(_hiddenContainer.Id, stored.Id), CancellationToken.None)))).Message.ShouldBe(text);

        var reloaded = await StoredOverrideAsync(stored.Id);
        reloaded.ShouldNotBeNull();
        reloaded.FromTime.ShouldBe(stored.FromTime);
    }

    [Test]
    public async Task PostOverride_Supervisor_NewHiddenTaskIsRefused_HiddenTaskCopiedFromTheTemplateIsSaved()
    {
        await SeedTemplateAsync(_visibleContainer.Id, Monday, _hiddenTask.Id);
        await SeedLockAsync(ContainerLockResourceTypes.ContainerShiftOverride, _visibleContainer.Id, _supervisor);
        var otherHiddenTask = NewShift("HIDDEN_TASK_2", ShiftType.IsTask, _hiddenRootId);
        _context.Shift.Add(otherHiddenTask);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => PostOverrideHandler()
            .Handle(new PostContainerShiftOverrideCommand(_visibleContainer.Id, OverrideResource(_visibleContainer.Id, otherHiddenTask.Id)),
                CancellationToken.None)));
        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, otherHiddenTask.Id));
        (await _context.ContainerShiftOverrides.AsNoTracking().CountAsync(o => o.ContainerId == _visibleContainer.Id)).ShouldBe(0);

        await AsAsync(_supervisor, () => PostOverrideHandler()
            .Handle(new PostContainerShiftOverrideCommand(_visibleContainer.Id, OverrideResource(_visibleContainer.Id, _hiddenTask.Id)),
                CancellationToken.None));

        var saved = await _context.ContainerShiftOverrides.AsNoTracking().Include(o => o.ContainerShiftOverrideItems)
            .SingleAsync(o => o.ContainerId == _visibleContainer.Id);
        saved.ContainerShiftOverrideItems.Select(i => i.ShiftId).ShouldBe([_hiddenTask.Id]);
    }

    [Test]
    public async Task Overrides_Admin_OverrideAddressedThroughAnotherContainer_IsTreatedAsMissing_NothingChanged()
    {
        var foreign = await SeedOverrideAsync(_hiddenContainer.Id, _hiddenTask.Id);
        await SeedLockAsync(ContainerLockResourceTypes.ContainerShiftOverride, _visibleContainer.Id, _admin);
        var missingId = Guid.NewGuid();

        var wrong = await Should.ThrowAsync<NotFoundException>(() => AsAsync(_admin, () => PutOverrideHandler()
            .Handle(new PutContainerShiftOverrideCommand(_visibleContainer.Id, foreign.Id, OverrideResource(_visibleContainer.Id, _visibleTask.Id)),
                CancellationToken.None)));
        var missing = await Should.ThrowAsync<NotFoundException>(() => AsAsync(_admin, () => PutOverrideHandler()
            .Handle(new PutContainerShiftOverrideCommand(_visibleContainer.Id, missingId, OverrideResource(_visibleContainer.Id, _visibleTask.Id)),
                CancellationToken.None)));
        var deleted = await AsAsync(_admin, () => DeleteOverrideHandler()
            .Handle(new DeleteContainerShiftOverrideCommand(_visibleContainer.Id, foreign.Id), CancellationToken.None));

        wrong.Message.Replace(foreign.Id.ToString(), IdPlaceholder).ShouldBe(missing.Message.Replace(missingId.ToString(), IdPlaceholder));
        deleted.ShouldBeFalse();
        var reloaded = await StoredOverrideAsync(foreign.Id);
        reloaded.ShouldNotBeNull();
        reloaded.ContainerShiftOverrideItems.Select(i => i.ShiftId).ShouldBe([_hiddenTask.Id]);
    }

    [Test]
    public async Task Lock_Supervisor_HiddenContainerIsAnsweredLikeAMissingOne_NoLockWritten_VisibleContainerIsLocked()
    {
        var missingId = Guid.NewGuid();

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => LockHandler().Handle(
            new AcquireContainerLockCommand(ContainerLockResourceTypes.ContainerTemplate, _hiddenContainer.Id, SelfInstanceId),
            CancellationToken.None)));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => AsAsync(_supervisor, () => LockHandler().Handle(
            new AcquireContainerLockCommand(ContainerLockResourceTypes.ContainerShiftOverride, missingId, SelfInstanceId),
            CancellationToken.None)));
        await Should.ThrowAsync<BadRequestException>(() => AsAsync(_supervisor, () => LockHandler().Handle(
            new AcquireContainerLockCommand(Prefix, _visibleContainer.Id, SelfInstanceId), CancellationToken.None)));
        var visible = await AsAsync(_supervisor, () => LockHandler().Handle(
            new AcquireContainerLockCommand(ContainerLockResourceTypes.ContainerTemplate, _visibleContainer.Id, SelfInstanceId),
            CancellationToken.None));

        AssertAlike(hidden, _hiddenContainer.Id, missing, missingId);
        visible.Acquired.ShouldBeTrue();
        (await _context.ContainerLock.AsNoTracking()
                .Where(l => l.ResourceId == _hiddenContainer.Id || l.ResourceId == missingId || l.ResourceType == Prefix)
                .CountAsync())
            .ShouldBe(0);
    }

    private static void AssertAlike(Exception hidden, Guid hiddenId, Exception missing, Guid missingId) =>
        hidden.Message.Replace(hiddenId.ToString(), IdPlaceholder)
            .ShouldBe(missing.Message.Replace(missingId.ToString(), IdPlaceholder));

    private async Task<T> AsAsync<T>(AppUser user, Func<Task<T>> action)
    {
        using (ExecutionPrincipal.Begin(Guid.Parse(user.Id)))
        {
            try
            {
                return await action();
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    private GetContainerTemplatesQueryHandler GetTemplatesHandler() =>
        new(_templateRepository, _mapper, _containerGuard, Substitute.For<ILogger<GetContainerTemplatesQueryHandler>>());

    private PostContainerTemplatesCommandHandler PostTemplatesHandler() =>
        new(_templateRepository, _unitOfWork, _mapper, _lockRepository, _userService, _containerGuard,
            Substitute.For<ILogger<PostContainerTemplatesCommandHandler>>());

    private PutContainerTemplatesCommandHandler PutTemplatesHandler() =>
        new(_templateRepository, _unitOfWork, _mapper, _lockRepository, _userService, _containerGuard,
            Substitute.For<ILogger<PutContainerTemplatesCommandHandler>>());

    private DeleteContainerTemplatesCommandHandler DeleteTemplatesHandler() =>
        new(_templateRepository, _unitOfWork, _mapper, _lockRepository, _userService, _containerGuard,
            Substitute.For<ILogger<DeleteContainerTemplatesCommandHandler>>());

    private PostContainerShiftOverrideCommandHandler PostOverrideHandler() =>
        new(_overrideRepository, _lockRepository, _unitOfWork, _mapper, _userService, _containerGuard,
            Substitute.For<ILogger<PostContainerShiftOverrideCommandHandler>>());

    private PutContainerShiftOverrideCommandHandler PutOverrideHandler() =>
        new(_overrideRepository, _lockRepository, _unitOfWork, _mapper, _userService, _containerGuard,
            Substitute.For<ILogger<PutContainerShiftOverrideCommandHandler>>());

    private DeleteContainerShiftOverrideCommandHandler DeleteOverrideHandler() =>
        new(_overrideRepository, _unitOfWork, _containerGuard, Substitute.For<ILogger<DeleteContainerShiftOverrideCommandHandler>>());

    private AcquireContainerLockCommandHandler LockHandler() =>
        new(_lockRepository, _userService, _unitOfWork, _containerGuard,
            Substitute.For<IContainerWorkChildrenReadRepository>(),
            Substitute.For<Klacks.Api.Application.Interfaces.IScheduleRowAccessGuard>(),
            _shiftGuard,
            Substitute.For<ILogger<AcquireContainerLockCommandHandler>>());

    private static ContainerTemplateResource NewTemplate(Guid containerId, params Guid[] shiftIds) => new()
    {
        ContainerId = containerId,
        Weekday = Monday,
        FromTime = new TimeOnly(6, 0),
        UntilTime = new TimeOnly(22, 0),
        ContainerTemplateItems = shiftIds.Select(id => new ContainerTemplateItemResource { ShiftId = id }).ToList()
    };

    private static ContainerTemplateResource ResourceOf(ContainerTemplate stored) => new()
    {
        Id = stored.Id,
        ContainerId = stored.ContainerId,
        Weekday = stored.Weekday,
        FromTime = stored.FromTime,
        UntilTime = stored.UntilTime,
        ContainerTemplateItems = stored.ContainerTemplateItems
            .Select(i => new ContainerTemplateItemResource { Id = i.Id, ContainerTemplateId = stored.Id, ShiftId = i.ShiftId })
            .ToList()
    };

    private static ContainerShiftOverrideResource OverrideResource(Guid containerId, params Guid[] shiftIds) => new()
    {
        ContainerId = containerId,
        Date = Day,
        FromTime = new TimeOnly(7, 0),
        UntilTime = new TimeOnly(21, 0),
        ContainerShiftOverrideItems = shiftIds.Select(id => new ContainerShiftOverrideItemResource { ShiftId = id }).ToList()
    };

    private async Task<ContainerTemplate> SeedTemplateAsync(Guid containerId, int weekday, params Guid[] shiftIds)
    {
        var templateId = Guid.NewGuid();
        var template = new ContainerTemplate
        {
            Id = templateId,
            ContainerId = containerId,
            Weekday = weekday,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0),
            ContainerTemplateItems = shiftIds
                .Select(id => new ContainerTemplateItem { Id = Guid.NewGuid(), ContainerTemplateId = templateId, ShiftId = id })
                .ToList()
        };
        _context.ContainerTemplate.Add(template);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return template;
    }

    private async Task<ContainerShiftOverride> SeedOverrideAsync(Guid containerId, params Guid[] shiftIds)
    {
        var overrideId = Guid.NewGuid();
        var entity = new ContainerShiftOverride
        {
            Id = overrideId,
            ContainerId = containerId,
            Date = Day,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0),
            ContainerShiftOverrideItems = shiftIds
                .Select(id => new ContainerShiftOverrideItem { Id = Guid.NewGuid(), ContainerShiftOverrideId = overrideId, ShiftId = id })
                .ToList()
        };
        _context.ContainerShiftOverrides.Add(entity);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return entity;
    }

    private async Task SeedLockAsync(string resourceType, Guid containerId, AppUser holder)
    {
        _context.ContainerLock.Add(new ContainerLock
        {
            Id = Guid.NewGuid(),
            ResourceType = resourceType,
            ResourceId = containerId,
            UserId = Guid.Parse(holder.Id),
            UserName = holder.UserName!,
            InstanceId = SelfInstanceId,
            AcquiredAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Task<List<ContainerTemplate>> StoredTemplatesAsync(Guid containerId) =>
        _context.ContainerTemplate.AsNoTracking().Include(t => t.ContainerTemplateItems)
            .Where(t => t.ContainerId == containerId).ToListAsync();

    private Task<ContainerShiftOverride?> StoredOverrideAsync(Guid overrideId) =>
        _context.ContainerShiftOverrides.AsNoTracking().Include(o => o.ContainerShiftOverrideItems)
            .FirstOrDefaultAsync(o => o.Id == overrideId);

    private async Task PopulateAsync()
    {
        var visibleRoot = NewGroup("VISIBLE_ROOT");
        var hiddenRoot = NewGroup("HIDDEN_ROOT");
        _visibleRootId = visibleRoot.Id;
        _hiddenRootId = hiddenRoot.Id;
        _supervisor = NewUser("SUPERVISOR");
        _admin = NewUser("ADMIN");

        _context.Group.AddRange(visibleRoot, hiddenRoot);
        _context.AppUser.AddRange(_supervisor, _admin);
        await _context.SaveChangesAsync();

        _context.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = _supervisor.Id, GroupId = _visibleRootId });

        _visibleContainer = NewShift("VISIBLE_CONTAINER", ShiftType.IsContainer, _visibleRootId);
        _hiddenContainer = NewShift("HIDDEN_CONTAINER", ShiftType.IsContainer, _hiddenRootId);
        _visibleTask = NewShift("VISIBLE_TASK", ShiftType.IsTask, _visibleRootId);
        _hiddenTask = NewShift("HIDDEN_TASK", ShiftType.IsTask, _hiddenRootId);
        _context.Shift.AddRange(_visibleContainer, _hiddenContainer, _visibleTask, _hiddenTask);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private void BuildPipeline()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var userManager = BuildUserManager();
        _userService = new UserService(accessor, userManager, Substitute.For<ILogger<UserService>>());
        var groupVisibility = new GroupVisibilityService(_context, userManager, _userService);
        var groupFilter = new ShiftGroupFilterService(groupVisibility, _userService);
        var groupGuard = new GroupVisibilityGuard(groupVisibility, _userService);
        var reader = new ShiftVisibilityReadRepository(_context);
        _mapper = new ScheduleMapper();
        _unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        _templateRepository = new ContainerTemplateRepository(
            _context,
            Substitute.For<ILogger<ContainerTemplate>>(),
            new EntityCollectionUpdateService(_context),
            new ContainerTemplateService(_unitOfWork, Substitute.For<ILogger<ContainerTemplateService>>()));
        _overrideRepository = new ContainerShiftOverrideRepository(_context);
        _lockRepository = new ContainerLockRepository(_context);
        _shiftGuard = new ShiftVisibilityGuard(groupFilter, reader, groupGuard, Substitute.For<ILogger<ShiftVisibilityGuard>>());
        _containerGuard = new ContainerVisibilityGuard(_shiftGuard, reader, Substitute.For<ILogger<ContainerVisibilityGuard>>());
    }

    private UserManager<AppUser> BuildUserManager()
    {
        var userManager = Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(),
            Substitute.For<IOptions<IdentityOptions>>(),
            Substitute.For<IPasswordHasher<AppUser>>(),
            new List<IUserValidator<AppUser>>(),
            new List<IPasswordValidator<AppUser>>(),
            Substitute.For<ILookupNormalizer>(),
            Substitute.For<IdentityErrorDescriber>(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<AppUser>>>());
        foreach (var user in new[] { _supervisor, _admin })
        {
            userManager.FindByIdAsync(user.Id).Returns(user);
            IList<string> roles = user == _admin
                ? new List<string> { Roles.Admin }
                : new List<string> { Roles.Authorised };
            userManager.GetRolesAsync(user).Returns(Task.FromResult(roles));
            userManager.IsInRoleAsync(user, Roles.Admin).Returns(user == _admin);
        }

        return userManager;
    }

    private static Group NewGroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Description = "Container visibility integration test",
        ValidFrom = DateTime.UtcNow.AddYears(-1)
    };

    private static Shift NewShift(string suffix, ShiftType shiftType, params Guid[] groupIds)
    {
        var shiftId = Guid.NewGuid();
        return new Shift
        {
            Id = shiftId,
            Name = $"{Prefix}{suffix}",
            Abbreviation = "CVI",
            Description = "Container visibility integration test",
            Status = ShiftStatus.OriginalShift,
            ShiftType = shiftType,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 8m,
            IsMonday = true,
            Quantity = 1,
            SumEmployees = 1,
            GroupItems = groupIds
                .Select(groupId => new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = shiftId })
                .ToList()
        };
    }

    private static AppUser NewUser(string suffix)
    {
        var email = $"{Prefix}{suffix}@example.invalid";
        return new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = Prefix,
            LastName = suffix,
            SecurityStamp = Guid.NewGuid().ToString()
        };
    }

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        var shifts = $"SELECT id FROM shift WHERE starts_with(name, '{Prefix}')";
        var sql = $@"
            DELETE FROM container_lock WHERE resource_id IN ({shifts}) OR starts_with(resource_type, '{Prefix}');
            DELETE FROM container_shift_override_items WHERE shift_id IN ({shifts})
                OR container_shift_override_id IN (SELECT id FROM container_shift_overrides WHERE container_id IN ({shifts}));
            DELETE FROM container_shift_overrides WHERE container_id IN ({shifts});
            DELETE FROM container_template_item WHERE shift_id IN ({shifts})
                OR container_template_id IN (SELECT id FROM container_template WHERE container_id IN ({shifts}));
            DELETE FROM container_template WHERE container_id IN ({shifts});
            DELETE FROM group_item WHERE shift_id IN ({shifts});
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM group_visibility WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_visibility WHERE app_user_id IN (SELECT id FROM ""AspNetUsers"" WHERE starts_with(email, '{Prefix}'));
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""AspNetUsers"" WHERE starts_with(email, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
