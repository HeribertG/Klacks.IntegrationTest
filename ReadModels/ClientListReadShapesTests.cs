// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Claims;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.Api.Application.Queries.Clients;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.ReadModels;

/// <summary>
/// Pins, against real PostgreSQL, the client reads of the Application layer: the client-availability list (total
/// over every kept client, StartRow/RowCount page after the base name sort, group ids of the non-deleted items with
/// scenario rows), the client export (IdNumber order, include/exclude selection, the numeric search that unions the
/// IdNumber hit with the zip hit, deleted clients for administrators) and the TargetHoursDrift roster (employees and
/// external employees, no customers, no deleted clients). The group filter is replaced by a restriction to the
/// seeded clients, so every assertion stays on seeded rows. Every client carries the INTEGRATION_TEST_ClientListReads_
/// prefix and the cleanup deletes exactly those and their memberships, addresses and group items.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class ClientListReadShapesTests
{
    private const string Prefix = "INTEGRATION_TEST_ClientListReads_";
    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private DataBaseContext _context = null!;
    private HashSet<Guid> _seeded = [];

    private Client _alpha = null!;
    private Client _bravo = null!;
    private Client _charlie = null!;
    private Client _delta = null!;
    private Client _customer = null!;
    private Client _deleted = null!;
    private Guid _groupA;
    private Guid _groupB;
    private Guid _scenarioGroup;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task AvailabilityList_CountsEveryKeptClient_AndPagesAfterTheNameSort()
    {
        var response = await ListAsync(startRow: 1, rowCount: 2, sortOrder: "asc");

        response.TotalCount.ShouldBe(4);
        response.Clients.Select(c => c.Id).ShouldBe([_bravo.Id, _charlie.Id]);
        response.Clients[0].GroupIds.ShouldBe([_groupA, _groupB], ignoreOrder: true);
        response.Clients[1].GroupIds.ShouldBe([_scenarioGroup]);
        response.Clients[0].Name.ShouldBe(_bravo.Name);
        response.Clients[0].IdNumber.ShouldBe(await IdNumberOf(_bravo.Id));
    }

    [Test]
    public async Task AvailabilityList_DescendingSort_AndAStartRowPastTheEnd()
    {
        var descending = await ListAsync(startRow: 0, rowCount: 3, sortOrder: "desc");
        descending.TotalCount.ShouldBe(4);
        descending.Clients.Select(c => c.Id).ShouldBe([_delta.Id, _charlie.Id, _bravo.Id]);

        var pastTheEnd = await ListAsync(startRow: 10, rowCount: 5, sortOrder: "asc");
        pastTheEnd.TotalCount.ShouldBe(4);
        pastTheEnd.Clients.ShouldBeEmpty();
    }

    [Test]
    public async Task Export_ComesInIdNumberOrder_AndAppliesTheSelection()
    {
        var all = await ExportAsync(Filter(), selectAll: true, inverted: false, [], isAdmin: false);
        all.Select(r => r.Name).ShouldBe(await NamesInIdNumberOrder(_alpha, _bravo, _charlie, _delta, _customer));
        all.Select(r => r.IdNumber).ShouldBeInOrder();
        var bravo = all.Single(r => r.Name == _bravo.Name);
        bravo.FirstName.ShouldBe("Bea");
        bravo.Company.ShouldBe("Bravo AG");
        bravo.Gender.ShouldBe((int)GenderEnum.Female);
        bravo.Type.ShouldBe((int)EntityTypeEnum.Employee);
        bravo.Birthdate.ShouldBe(new DateTime(1990, 5, 17, 0, 0, 0, DateTimeKind.Utc));

        var only = await ExportAsync(Filter(), selectAll: false, inverted: false, [_delta.Id, _alpha.Id], isAdmin: false);
        only.Select(r => r.Name).ShouldBe(await NamesInIdNumberOrder(_alpha, _delta));

        var without = await ExportAsync(Filter(), selectAll: true, inverted: true, [_alpha.Id, _customer.Id], isAdmin: false);
        without.Select(r => r.Name).ShouldBe(await NamesInIdNumberOrder(_bravo, _charlie, _delta));

        var emptySelection = await ExportAsync(Filter(), selectAll: false, inverted: false, [], isAdmin: false);
        emptySelection.Count.ShouldBe(5);
    }

    [Test]
    public async Task Export_NumericSearch_UnionsTheIdNumberHitWithTheZipHit()
    {
        var charlieIdNumber = await IdNumberOf(_charlie.Id);
        _context.Address.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = _delta.Id,
            Street = Prefix + "Street",
            Zip = charlieIdNumber.ToString(),
            City = "Biel",
            Country = "CH",
            ValidFrom = Past,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var filter = Filter();
        filter.SearchString = charlieIdNumber.ToString();
        filter.IncludeAddress = true;

        var result = await ExportAsync(filter, selectAll: true, inverted: false, [], isAdmin: false);

        result.Select(r => r.Name).ShouldBe(await NamesInIdNumberOrder(_charlie, _delta));
    }

    [Test]
    public async Task Export_DeletedEntries_AreExportedForAdministratorsOnly()
    {
        var filter = Filter();
        filter.ShowDeleteEntries = true;

        var asAdmin = await ExportAsync(filter, selectAll: true, inverted: false, [], isAdmin: true);
        asAdmin.Select(r => r.Name).ShouldBe([_deleted.Name]);

        var asPlanner = await ExportAsync(Filter(true), selectAll: true, inverted: false, [], isAdmin: false);
        asPlanner.Select(r => r.Name).ShouldNotContain(_deleted.Name);
        asPlanner.Count.ShouldBe(5);
    }

    [Test]
    public async Task DriftRoster_HoldsEmployeesAndExternals_WithoutCustomersOrDeletedClients()
    {
        var clientRepository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            new EntityCollectionUpdateService(_context),
            Substitute.For<IClientValidator>(),
            Substitute.For<ILogger<ClientRepository>>());
        List<Guid>? scanned = null;
        var workRepository = Substitute.For<IWorkRepository>();
        workRepository.GetPeriodHoursForClients(
                Arg.Do<List<Guid>>(ids => scanned = ids),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => ((List<Guid>)call[0]).ToDictionary(
                id => id, _ => new PeriodHoursResource { Hours = 10, GuaranteedHours = 160 }));
        var activityProbe = Substitute.For<IScheduleActivityProbe>();
        activityProbe.HasAnyWorkInRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(new DateOnly(2031, 3, 15));
        var detector = new TargetHoursDriftDetector(
            clientRepository, workRepository, activityProbe, NullLogger<TargetHoursDriftDetector>.Instance, clock);

        var events = await detector.DetectAsync();

        scanned.ShouldNotBeNull();
        scanned.Where(_seeded.Contains).ShouldBe([_alpha.Id, _bravo.Id, _charlie.Id, _delta.Id], ignoreOrder: true);
        var affected = ((TargetHoursDriftTriggerEvent)events.Single()).AffectedClients
            .Where(a => _seeded.Contains(a.ClientId))
            .ToDictionary(a => a.ClientId, a => a.ClientName);
        affected[_bravo.Id].ShouldBe($"Bea {_bravo.Name}");

        scanned = null;
        await detector.GetActiveFingerprintsAsync();
        scanned.ShouldNotBeNull();
        scanned.Where(_seeded.Contains).ShouldBe([_alpha.Id, _bravo.Id, _charlie.Id, _delta.Id], ignoreOrder: true);
    }

    private async Task<ClientAvailabilityClientListResponse> ListAsync(int startRow, int rowCount, string sortOrder)
    {
        var searchService = new ClientSearchService();
        var baseQueryService = new ClientBaseQueryService(
            _context,
            SeededOnlyGroupFilter(),
            new ClientSearchFilterService(searchService),
            searchService,
            Substitute.For<IClientFuzzySearchService>());
        var handler = new ListClientsQueryHandler(baseQueryService, NullLogger<ListClientsQueryHandler>.Instance);

        return await handler.Handle(
            new ListClientAvailabilityClientsQuery(new ClientAvailabilityClientFilter
            {
                StartDate = new DateOnly(2031, 3, 1),
                EndDate = new DateOnly(2031, 3, 31),
                SearchString = string.Empty,
                ShowEmployees = true,
                ShowExtern = true,
                OrderBy = "name",
                SortOrder = sortOrder,
                StartRow = startRow,
                RowCount = rowCount,
            }),
            CancellationToken.None);
    }

    private async Task<List<ExportClientItemDto>> ExportAsync(
        FilterResource filter, bool selectAll, bool inverted, List<Guid> selection, bool isAdmin)
    {
        var filterRepository = new ClientFilterRepository(
            _context,
            SeededOnlyGroupFilter(),
            new ClientFilterService(),
            new ClientMembershipFilterService(),
            new ClientSearchService(),
            new ClientSortingService(),
            Substitute.For<IClientFuzzySearchService>());
        var claims = isAdmin ? new[] { new Claim(ClaimTypes.Role, Roles.Admin) } : Array.Empty<Claim>();
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
        });
        var handler = new ExportClientListQueryHandler(
            filterRepository, new FilterMapper(), httpContextAccessor, NullLogger<ExportClientListQueryHandler>.Instance);

        var request = new ExportClientRequest
        {
            Filter = filter,
            SelectAll = selectAll,
            InvertedSelection = inverted,
            Selection = selection,
        };
        return await handler.Handle(new ExportClientListQuery(request), CancellationToken.None);
    }

    private IClientGroupFilterService SeededOnlyGroupFilter()
    {
        var seeded = _seeded.ToList();
        var groupFilter = Substitute.For<IClientGroupFilterService>();
        groupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult(((IQueryable<Client>)args[1]).Where(c => seeded.Contains(c.Id))));
        groupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>(), Arg.Any<bool>())
            .Returns(args => Task.FromResult(((IQueryable<Client>)args[1]).Where(c => seeded.Contains(c.Id))));
        return groupFilter;
    }

    private static FilterResource Filter(bool showDeleteEntries = false) => new()
    {
        SearchString = string.Empty,
        SearchOnlyByName = true,
        ShowDeleteEntries = showDeleteEntries,
        Employee = true,
        ExternEmp = true,
        Customer = true,
        OrderBy = "name",
        SortOrder = "asc",
    };

    private async Task<int> IdNumberOf(Guid clientId) =>
        await _context.Client.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == clientId).Select(c => c.IdNumber).SingleAsync();

    private async Task<List<string>> NamesInIdNumberOrder(params Client[] clients)
    {
        var ids = clients.Select(c => c.Id).ToList();
        return await _context.Client.IgnoreQueryFilters().AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.IdNumber)
            .Select(c => c.Name!)
            .ToListAsync();
    }

    private async Task SeedAsync()
    {
        _delta = NewClient("Delta", "Dan", EntityTypeEnum.Employee);
        _alpha = NewClient("Alpha", "Ada", EntityTypeEnum.Employee);
        _charlie = NewClient("Charlie", "Cem", EntityTypeEnum.ExternEmp);
        _bravo = NewClient("Bravo", "Bea", EntityTypeEnum.Employee);
        _bravo.Company = "Bravo AG";
        _bravo.Gender = GenderEnum.Female;
        _bravo.Birthdate = new DateTime(1990, 5, 17, 0, 0, 0, DateTimeKind.Utc);
        _customer = NewClient("Echo", "Eli", EntityTypeEnum.Customer);
        _deleted = NewClient("Foxtrot", "Fay", EntityTypeEnum.Employee);
        _deleted.IsDeleted = true;
        foreach (var client in new[] { _delta, _alpha, _charlie, _bravo, _customer, _deleted })
        {
            _context.Client.Add(client);
            await _context.SaveChangesAsync();
        }

        _seeded = [_alpha.Id, _bravo.Id, _charlie.Id, _delta.Id, _customer.Id, _deleted.Id];

        _groupA = Guid.NewGuid();
        _groupB = Guid.NewGuid();
        _scenarioGroup = Guid.NewGuid();
        var deletedGroup = Guid.NewGuid();
        _context.Group.AddRange(
            NewGroup(_groupA, "A"), NewGroup(_groupB, "B"), NewGroup(_scenarioGroup, "Scenario"), NewGroup(deletedGroup, "Deleted"));
        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _bravo.Id, GroupId = _groupA },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _bravo.Id, GroupId = _groupB },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _charlie.Id, GroupId = _scenarioGroup, AnalyseToken = Guid.NewGuid() },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _charlie.Id, GroupId = deletedGroup, IsDeleted = true });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Client NewClient(string suffix, string firstName, EntityTypeEnum type) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        FirstName = firstName,
        Type = type,
        Gender = GenderEnum.Male,
        Membership = new Membership { Id = Guid.NewGuid(), ValidFrom = Past },
    };

    private static Group NewGroup(Guid id, string suffix) => new()
    {
        Id = id,
        Name = Prefix + suffix,
        Description = Prefix,
        ValidFrom = Past,
    };

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE starts_with(name, '{Prefix}'));
            DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}'));
            DELETE FROM client WHERE starts_with(name, '{Prefix}');
            DELETE FROM ""group"" WHERE starts_with(name, '{Prefix}');
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }
}
