// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins against PostgreSQL (IT database klacks) that the available-task list of a container template slot
/// (ContainerAvailableTasksService over ShiftReader) keeps only real-plan tasks (analyse_token IS NULL) whose validity
/// overlaps the container's validity, bounds inclusive: with a container valid in November a task starting in December or
/// ended in October is dropped, a task ending on the first or starting on the last day stays; an open-ended container
/// keeps the December task. Also pins the untracked validity projection IShiftReader.GetValidityPeriodAsync. Every row
/// carries the INTEGRATION_TEST_CANDVALID_ prefix, the search fragment keeps the list to this run's rows, and the cleanup
/// deletes exactly the prefixed rows.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
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
public class ContainerAvailableTasksValidityPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_CANDVALID_";
    private const int Monday = 1;

    private static readonly DateOnly November = new(2026, 11, 1);
    private static readonly DateOnly NovemberEnd = new(2026, 11, 30);
    private static readonly TimeOnly From = new(8, 0);
    private static readonly TimeOnly Until = new(18, 0);

    private DataBaseContext _context = null!;
    private ContainerAvailableTasksService _availableTasks = null!;
    private ShiftReader _reader = null!;
    private string _token = null!;
    private Dictionary<string, Shift> _shifts = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        _token = Guid.NewGuid().ToString("N")[..10];
        await CleanupAsync();
        await PopulateAsync();
        _reader = new ShiftReader(_context);
        _availableTasks = BuildService();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ANovemberContainer_KeepsOnlyRealPlanTasksValidInNovember_BoundsInclusive()
    {
        var listed = await _availableTasks.GetAvailableTasksAsync(
            _shifts["NovemberContainer"].Id, Monday, From, Until, searchString: _token);

        listed.Select(t => t.Id).ShouldBe(Ids("Inside", "OpenSinceSummer", "EndsOnFirstDay", "StartsOnLastDay"), ignoreOrder: true);
        _context.ChangeTracker.Entries<Shift>().ShouldBeEmpty();
    }

    [Test]
    public async Task AnOpenEndedContainer_AlsoKeepsTheDecemberTask_ButNeverTheScenarioCopy()
    {
        var listed = await _availableTasks.GetAvailableTasksAsync(
            _shifts["OpenContainer"].Id, Monday, From, Until, searchString: _token);

        listed.Select(t => t.Id).ShouldBe(
            Ids("Inside", "OpenSinceSummer", "EndsOnFirstDay", "StartsOnLastDay", "December"), ignoreOrder: true);
    }

    [Test]
    public async Task ValidityPeriod_IsReadAsAnUntrackedProjection()
    {
        var bounded = await _reader.GetValidityPeriodAsync(_shifts["NovemberContainer"].Id);
        var open = await _reader.GetValidityPeriodAsync(_shifts["OpenContainer"].Id);
        var unknown = await _reader.GetValidityPeriodAsync(Guid.NewGuid());

        bounded.ShouldBe(new ShiftValidityPeriod(November, NovemberEnd));
        open.ShouldBe(new ShiftValidityPeriod(November, null));
        unknown.ShouldBeNull();
        _context.ChangeTracker.Entries<Shift>().ShouldBeEmpty();
    }

    private List<Guid> Ids(params string[] names) => names.Select(name => _shifts[name].Id).ToList();

    private async Task PopulateAsync()
    {
        _shifts = new Dictionary<string, Shift>
        {
            ["NovemberContainer"] = NewShift("NovemberContainer", ShiftType.IsContainer, November, NovemberEnd),
            ["OpenContainer"] = NewShift("OpenContainer", ShiftType.IsContainer, November),
            ["Inside"] = NewShift("Inside", ShiftType.IsTask, November.AddDays(5)),
            ["OpenSinceSummer"] = NewShift("OpenSinceSummer", ShiftType.IsTask, new DateOnly(2026, 6, 1)),
            ["EndsOnFirstDay"] = NewShift("EndsOnFirstDay", ShiftType.IsTask, new DateOnly(2026, 6, 1), November),
            ["StartsOnLastDay"] = NewShift("StartsOnLastDay", ShiftType.IsTask, NovemberEnd),
            ["December"] = NewShift("December", ShiftType.IsTask, new DateOnly(2026, 12, 1)),
            ["EndedInOctober"] = NewShift("EndedInOctober", ShiftType.IsTask, new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 31)),
            ["ScenarioCopy"] = NewShift("ScenarioCopy", ShiftType.IsTask, November.AddDays(5)),
        };
        _shifts["ScenarioCopy"].AnalyseToken = Guid.NewGuid();
        _context.Shift.AddRange(_shifts.Values);

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Shift NewShift(string name, ShiftType shiftType, DateOnly fromDate, DateOnly? untilDate = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{Prefix}{_token}_{name}",
        Abbreviation = name[..3].ToUpperInvariant(),
        Description = string.Empty,
        Status = ShiftStatus.OriginalShift,
        ShiftType = shiftType,
        FromDate = fromDate,
        UntilDate = untilDate,
        StartShift = From,
        EndShift = new TimeOnly(17, 0),
        AfterShift = new TimeOnly(0, 0),
        BeforeShift = new TimeOnly(0, 0),
        WorkTime = 0.75m,
        IsTimeRange = shiftType == ShiftType.IsTask,
        IsSporadic = false,
        IsMonday = true,
    };

    private ContainerAvailableTasksService BuildService()
    {
        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var containerTemplateService = new ContainerTemplateService(
            Substitute.For<IUnitOfWork>(), Substitute.For<ILogger<ContainerTemplateService>>());
        return new ContainerAvailableTasksService(
            _reader,
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
            DELETE FROM shift WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
