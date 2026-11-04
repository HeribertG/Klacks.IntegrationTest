// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Containers;

/// <summary>
/// Container split against Postgres: the split writer re-parents the moved services with their ids, a moved service
/// without customer follows the person of its part and so adds no hours of its own (WorkOwnHours), a service with
/// customer stays with the customer, and nothing is deleted. The shift grid (get_shift_schedule) counts the parts of a split
/// container as one planned container, on the day of its first part. Only rows with the INTEGRATION_TEST_ prefix are written
/// and deleted.
/// </summary>
[TestFixture]
public class ContainerSplitPostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_CONTAINER_SPLIT_";
    private static readonly DateOnly Day = new(2026, 12, 7);

    private DataBaseContext _context = null!;
    private ShiftScheduleService _service = null!;
    private string _connectionString = null!;

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
        _service = new ShiftScheduleService(
            _context,
            new Klacks.Api.Infrastructure.Repositories.Associations.ShiftRequiredQualificationRepository(
                _context, Substitute.For<ILogger<Klacks.Api.Domain.Models.Associations.ShiftRequiredQualification>>()),
            Substitute.For<ILogger<ShiftScheduleService>>());
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        _context.Dispose();
    }

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM work WHERE parent_work_id IN (SELECT id FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Shift> CreateShiftAsync(string suffix, ShiftType type, Guid? customerId = null)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, Abbreviation = "CPR", Description = "probe",
            Status = ShiftStatus.OriginalShift, ShiftType = type, FromDate = new DateOnly(2026, 1, 1),
            StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0), WorkTime = 8m,
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            IsSaturday = true, IsSunday = true, SumEmployees = 1, Quantity = 1, ClientId = customerId,
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = TestPrefix + suffix, FirstName = "Probe", Company = string.Empty };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Work> AddWorkAsync(Guid shiftId, Guid clientId, DateOnly date, TimeOnly start, TimeOnly end, Guid? parentId = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(), ShiftId = shiftId, ClientId = clientId, CurrentDate = date, StartTime = start, EndTime = end,
            WorkTime = 1m, ParentWorkId = parentId,
        };
        await _context.Set<Work>().AddAsync(work);
        await _context.SaveChangesAsync();
        return work;
    }

    [Test]
    public async Task Split_MovesTheOwnServiceToTheNewPerson_KeepsTheCustomerService_AndDeletesNothing()
    {
        var container = await CreateShiftAsync("Container", ShiftType.IsContainer);
        var ownTask = await CreateShiftAsync("Task", ShiftType.IsTask);
        var personA = await CreateClientAsync("A");
        var personB = await CreateClientAsync("B");
        var customer = await CreateClientAsync("Customer");
        var customerTask = await CreateShiftAsync("CustomerTask", ShiftType.IsTask, customer.Id);

        var part1 = await AddWorkAsync(container.Id, personA.Id, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));
        var early = await AddWorkAsync(ownTask.Id, personA.Id, Day, new TimeOnly(9, 0), new TimeOnly(10, 0), part1.Id);
        var lateOwn = await AddWorkAsync(ownTask.Id, personA.Id, Day, new TimeOnly(13, 0), new TimeOnly(14, 0), part1.Id);
        var lateCustomer = await AddWorkAsync(customerTask.Id, customer.Id, Day, new TimeOnly(14, 0), new TimeOnly(15, 0), part1.Id);

        var second = await new ContainerSplitWriter(_context).StageAsync(
            new ContainerSplitPlan(part1.Id, new TimeOnly(12, 0), Day, personB.Id, new HashSet<Guid> { lateOwn.Id, lateCustomer.Id }),
            CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var moved = await _context.Work.AsNoTracking().SingleAsync(w => w.Id == lateOwn.Id);
        (moved.ParentWorkId, moved.ClientId).ShouldBe((second.Id, personB.Id));
        var withCustomer = await _context.Work.AsNoTracking().SingleAsync(w => w.Id == lateCustomer.Id);
        (withCustomer.ParentWorkId, withCustomer.ClientId).ShouldBe((second.Id, customer.Id));

        var ids = new[] { part1.Id, early.Id, second.Id, lateOwn.Id, lateCustomer.Id };
        var counting = await _context.Work.AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .Where(WorkOwnHours.Counts)
            .Select(w => w.Id)
            .ToListAsync();
        counting.ShouldBe(new[] { part1.Id, second.Id, lateCustomer.Id }, ignoreOrder: true);
        (await _context.Work.IgnoreQueryFilters().CountAsync(w => ids.Contains(w.Id) && w.IsDeleted)).ShouldBe(0);
    }

    [Test]
    public async Task ShiftGrid_CountsTheTwoPartsOfASplitContainer_AsOnePlannedContainer()
    {
        var container = await CreateShiftAsync("GridContainer", ShiftType.IsContainer);
        var task = await CreateShiftAsync("GridTask", ShiftType.IsTask);
        var personA = await CreateClientAsync("GA");
        var personB = await CreateClientAsync("GB");
        var part1 = await AddWorkAsync(container.Id, personA.Id, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));
        await AddWorkAsync(task.Id, personA.Id, Day, new TimeOnly(9, 0), new TimeOnly(10, 0), part1.Id);
        var late = await AddWorkAsync(task.Id, personA.Id, Day, new TimeOnly(13, 0), new TimeOnly(14, 0), part1.Id);
        await new ContainerSplitWriter(_context).StageAsync(
            new ContainerSplitPlan(part1.Id, new TimeOnly(12, 0), Day, personB.Id, new HashSet<Guid> { late.Id }),
            CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var rows = await _service.GetShiftScheduleQuery(Day, Day).ToListAsync();

        rows.Single(r => r.ShiftId == container.Id && r.Date == Day).Engaged.ShouldBe(1);
    }

    [Test]
    public async Task ShiftGrid_CountsANightContainerSplitAfterMidnight_OnceOnTheDayOfItsFirstPart()
    {
        var container = await CreateShiftAsync("NightContainer", ShiftType.IsContainer);
        var task = await CreateShiftAsync("NightTask", ShiftType.IsTask);
        var personA = await CreateClientAsync("NA");
        var personB = await CreateClientAsync("NB");
        var part1 = await AddWorkAsync(container.Id, personA.Id, Day, new TimeOnly(22, 0), new TimeOnly(6, 0));
        await AddWorkAsync(task.Id, personA.Id, Day, new TimeOnly(22, 30), new TimeOnly(1, 0), part1.Id);
        var morning = await AddWorkAsync(task.Id, personA.Id, Day, new TimeOnly(3, 0), new TimeOnly(5, 0), part1.Id);
        await new ContainerSplitWriter(_context).StageAsync(
            new ContainerSplitPlan(part1.Id, new TimeOnly(2, 0), Day.AddDays(1), personB.Id, new HashSet<Guid> { morning.Id }),
            CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var rows = await _service.GetShiftScheduleQuery(Day, Day.AddDays(1)).ToListAsync();

        var byDate = rows.Where(r => r.ShiftId == container.Id).ToDictionary(r => r.Date, r => r.Engaged);
        byDate[Day].ShouldBe(1);
        byDate[Day.AddDays(1)].ShouldBe(0);
        (await _context.Work.AsNoTracking().SingleAsync(w => w.Id == morning.Id)).CurrentDate.ShouldBe(Day.AddDays(1));
    }
}
