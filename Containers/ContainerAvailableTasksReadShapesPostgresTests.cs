// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the read shape of the available-task list (ContainerAvailableTasksService) against PostgreSQL (IT database klacks),
/// beside the slot rule that ContainerAvailableTasksEngineParityPostgresTests pins: which tasks the candidate read keeps
/// (task type, status from OriginalShift on, not sporadic, not used in another container, inside the time frame, on the
/// slot's weekday, inside the container's group when it has more than one shift, matching the search fragment), the order
/// (start time, then client name with client-less tasks last), the loaded client with all its addresses, and the override
/// read: extra ids that are tasks are appended after the list in the shift base order (original id, from date, start
/// time) whatever their status, sporadic flag or time; non-task ids and ids already listed are not added. Every row
/// carries the INTEGRATION_TEST_CANDREAD_ prefix, the search fragment keeps the list to this run's rows, and the cleanup
/// deletes exactly the prefixed rows.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Containers;

[TestFixture]
[Category("RealDatabase")]
public class ContainerAvailableTasksReadShapesPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_CANDREAD_";
    private const int Monday = 1;

    private static readonly DateOnly SeedFrom = new(2026, 1, 1);
    private static readonly TimeOnly From = new(8, 0);
    private static readonly TimeOnly Until = new(12, 0);

    private DataBaseContext _context = null!;
    private ContainerAvailableTasksService _availableTasks = null!;
    private string _token = null!;
    private Guid _zetaClientId;
    private Guid _alphaClientId;
    private Dictionary<string, Shift> _shifts = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        _token = Guid.NewGuid().ToString("N")[..10];
        await CleanupAsync();
        await PopulateAsync();
        _availableTasks = BuildService();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task List_KeepsTheCandidatesInStartTimeThenClientNameOrder_WithTheirClientAndAllAddresses()
    {
        var listed = await _availableTasks.GetAvailableTasksAsync(
            _shifts["Container"].Id, Monday, From, Until, searchString: _token.ToUpperInvariant());

        listed.Select(t => t.Id).ShouldBe(Ids("EarlyAlpha", "NineAlpha", "NineZeta", "NineNoClient", "OutsideGroup"));
        listed.Single(t => t.Id == _shifts["NineZeta"].Id).Client!.Addresses.Count.ShouldBe(2);
        listed.Single(t => t.Id == _shifts["NineAlpha"].Id).Client!.Name.ShouldBe(Prefix + "Alpha");
        listed.Single(t => t.Id == _shifts["NineNoClient"].Id).Client.ShouldBeNull();
        _context.ChangeTracker.Entries<Shift>().ShouldBeEmpty();
    }

    [Test]
    public async Task List_InsideAGroupWithSeveralShifts_KeepsOnlyTheGroupsTasks()
    {
        var listed = await _availableTasks.GetAvailableTasksAsync(
            _shifts["GroupContainer"].Id, Monday, From, Until, searchString: _token);

        listed.Select(t => t.Id).ShouldBe(Ids("EarlyAlpha", "NineZeta"));
    }

    [Test]
    public async Task Override_AppendsExtraTasksInTheBaseOrder_WhateverTheirStatusSporadicFlagOrTime()
    {
        var extra = Ids("NineZeta", "Afternoon", "Sporadic", "StatusOrder", "Container", "TuesdayOnly");

        var listed = await _availableTasks.GetAvailableTasksAsync(
            _shifts["Container"].Id, Monday, From, Until, searchString: _token, additionalAvailableWorkIds: extra);

        listed.Select(t => t.Id).ShouldBe(
            Ids("EarlyAlpha", "NineAlpha", "NineZeta", "NineNoClient", "OutsideGroup", "StatusOrder", "TuesdayOnly", "Sporadic", "Afternoon"));
        listed.Single(t => t.Id == _shifts["Afternoon"].Id).Client!.Addresses.Count.ShouldBe(2);
        _context.ChangeTracker.Entries<Shift>().ShouldBeEmpty();
    }

    private List<Guid> Ids(params string[] names) => names.Select(name => _shifts[name].Id).ToList();

    private async Task PopulateAsync()
    {
        _zetaClientId = Guid.NewGuid();
        _alphaClientId = Guid.NewGuid();
        _context.Client.AddRange(
            new Client
            {
                Id = _zetaClientId,
                Name = Prefix + "Zeta",
                FirstName = "Integration",
                Addresses = [NewAddress(_zetaClientId, "Hauptstrasse 1"), NewAddress(_zetaClientId, "Bahnhofplatz 2")],
            },
            new Client
            {
                Id = _alphaClientId,
                Name = Prefix + "Alpha",
                FirstName = "Integration",
                Addresses = [NewAddress(_alphaClientId, "Seevorstadt 3")],
            });

        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = Prefix + "Group",
            Description = Prefix,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _context.Group.Add(group);

        _shifts = new Dictionary<string, Shift>
        {
            ["Container"] = NewShift("Container", ShiftType.IsContainer, new TimeOnly(8, 0), SeedFrom),
            ["GroupContainer"] = NewShift("GroupContainer", ShiftType.IsContainer, new TimeOnly(8, 0), SeedFrom),
            ["UsingContainer"] = NewShift("UsingContainer", ShiftType.IsContainer, new TimeOnly(8, 0), SeedFrom),
            ["EarlyAlpha"] = NewShift("EarlyAlpha", ShiftType.IsTask, new TimeOnly(8, 0), SeedFrom, _alphaClientId),
            ["NineZeta"] = NewShift("NineZeta", ShiftType.IsTask, new TimeOnly(9, 0), SeedFrom, _zetaClientId),
            ["NineAlpha"] = NewShift("NineAlpha", ShiftType.IsTask, new TimeOnly(9, 0), SeedFrom.AddDays(1), _alphaClientId),
            ["NineNoClient"] = NewShift("NineNoClient", ShiftType.IsTask, new TimeOnly(9, 0), SeedFrom),
            ["Afternoon"] = NewShift("Afternoon", ShiftType.IsTask, new TimeOnly(14, 0), SeedFrom.AddDays(5), _zetaClientId),
            ["Sporadic"] = NewShift("Sporadic", ShiftType.IsTask, new TimeOnly(9, 0), SeedFrom.AddDays(4)),
            ["StatusOrder"] = NewShift("StatusOrder", ShiftType.IsTask, new TimeOnly(9, 0), SeedFrom.AddDays(2)),
            ["TuesdayOnly"] = NewShift("TuesdayOnly", ShiftType.IsTask, new TimeOnly(9, 0), SeedFrom.AddDays(3)),
            ["Used"] = NewShift("Used", ShiftType.IsTask, new TimeOnly(9, 0), SeedFrom),
            ["OutsideGroup"] = NewShift("OutsideGroup", ShiftType.IsTask, new TimeOnly(10, 0), SeedFrom, _zetaClientId),
        };
        _shifts["Sporadic"].IsSporadic = true;
        _shifts["StatusOrder"].Status = ShiftStatus.OriginalOrder;
        _shifts["TuesdayOnly"].IsMonday = false;
        _shifts["TuesdayOnly"].IsTuesday = true;
        _shifts["Afternoon"].EndShift = new TimeOnly(16, 0);
        _context.Shift.AddRange(_shifts.Values);

        foreach (var name in new[] { "GroupContainer", "EarlyAlpha", "NineZeta" })
        {
            _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = group.Id, ShiftId = _shifts[name].Id });
        }

        _context.ContainerTemplate.Add(new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = _shifts["UsingContainer"].Id,
            Weekday = Monday,
            FromTime = From,
            UntilTime = Until,
            ContainerTemplateItems =
            [
                new ContainerTemplateItem { Id = Guid.NewGuid(), ShiftId = _shifts["Used"].Id, StartItem = From, EndItem = Until }
            ]
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Shift NewShift(string name, ShiftType shiftType, TimeOnly start, DateOnly fromDate, Guid? clientId = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{Prefix}{_token}_{name}",
        Abbreviation = name[..3].ToUpperInvariant(),
        Description = string.Empty,
        Status = ShiftStatus.OriginalShift,
        ShiftType = shiftType,
        ClientId = clientId,
        FromDate = fromDate,
        StartShift = start,
        EndShift = start.AddHours(1),
        AfterShift = new TimeOnly(0, 0),
        BeforeShift = new TimeOnly(0, 0),
        WorkTime = 1m,
        IsTimeRange = false,
        IsSporadic = false,
        IsMonday = true,
    };

    private static Address NewAddress(Guid clientId, string street) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Street = street,
        Zip = "2502",
        City = "Biel",
        Country = "CH",
        ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private ContainerAvailableTasksService BuildService()
    {
        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            Substitute.For<IShiftQueryPipelineService>(),
            Substitute.For<IShiftGroupManagementService>(),
            collectionUpdateService,
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper(),
            TestCompanyClock.Utc());
        var containerTemplateService = new ContainerTemplateService(
            Substitute.For<IUnitOfWork>(), Substitute.For<ILogger<ContainerTemplateService>>());
        return new ContainerAvailableTasksService(
            shiftRepository,
            new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>()),
            new ContainerTemplateRepository(
                _context, Substitute.For<ILogger<ContainerTemplate>>(), collectionUpdateService, containerTemplateService),
            Substitute.For<ILogger<ContainerAvailableTasksService>>());
    }

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        var shifts = $"SELECT id FROM shift WHERE starts_with(name, '{Prefix}')";
        var sql = $@"
            DELETE FROM container_template_item WHERE shift_id IN ({shifts})
                OR container_template_id IN (SELECT id FROM container_template WHERE container_id IN ({shifts}));
            DELETE FROM container_template WHERE container_id IN ({shifts});
            DELETE FROM group_item WHERE shift_id IN ({shifts});
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
            DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM client WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
