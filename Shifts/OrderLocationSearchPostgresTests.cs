// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Geo;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Shifts;

/// <summary>
/// Pins, against real PostgreSQL, the order location search of the shift list (ShiftRepository.FilterShifts with
/// ShiftLocationQuery: OrderLocationAddressRule.LocatedAt, AddressLocationPredicate and the customer visibility), the
/// shared address predicate in the client search (one address must meet canton and city) and the ungrouped-order count
/// of IShiftReader. Customers: WP (main Bern, workplace Thun), MAIN (main Bern 3007), INV (invoicing Bern, main Zurich),
/// BERLIN (main Berlin BE/DE), GONE (deleted workplace Bern, main Biel), HIDDEN (main Bern 3005, member of a hidden
/// group); one open order each. Every row carries the INTEGRATION_TEST_OrderLoc_ prefix and the cleanup deletes
/// exactly those rows.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class OrderLocationSearchPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_OrderLoc_";
    private const int PageSize = 50;

    private DataBaseContext _context = null!;
    private SupervisorVisibilityScenario _scenario = null!;
    private IShiftRepository _shiftRepository = null!;
    private string _token = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        _scenario = await SupervisorVisibilityScenario.CreateAsync(Prefix, _context);
        _token = "ordloc" + Guid.NewGuid().ToString("N")[..10];

        await SeedCustomerWithOrderAsync("WP",
            Addr(AddressTypeEnum.Employee, "Bern", "3011", "BE", "CH"),
            Addr(AddressTypeEnum.Workplace, "Thun", "3600", "BE", "CH"));
        await SeedCustomerWithOrderAsync("MAIN", Addr(AddressTypeEnum.Employee, "Bern", "3007", "BE", "CH"));
        await SeedCustomerWithOrderAsync("INV",
            Addr(AddressTypeEnum.InvoicingAddress, "Bern", "3001", "BE", "CH"),
            Addr(AddressTypeEnum.Employee, "Zürich", "8001", "ZH", "CH"));
        await SeedCustomerWithOrderAsync("BERLIN", Addr(AddressTypeEnum.Employee, "Berlin", "10115", "BE", "DE"));
        var deletedWorkplace = Addr(AddressTypeEnum.Workplace, "Bern", "3000", "BE", "CH");
        deletedWorkplace.IsDeleted = true;
        await SeedCustomerWithOrderAsync("GONE", deletedWorkplace, Addr(AddressTypeEnum.Employee, "Biel", "2502", "BE", "CH"));
        var hidden = await SeedCustomerWithOrderAsync("HIDDEN", Addr(AddressTypeEnum.Employee, "Bern", "3005", "BE", "CH"));
        _context.Set<GroupItem>().Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _scenario.HiddenGroup.Id, ClientId = hidden.Id });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var queryPipeline = new ShiftQueryPipelineService(
            new DateRangeFilterService(),
            new ShiftSearchService(),
            new ShiftSortingService(),
            new ShiftStatusFilterService(),
            new ShiftPaginationService());
        _shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            queryPipeline,
            new ShiftGroupManagementService(_context, Substitute.For<ILogger<ShiftGroupManagementService>>()),
            new EntityCollectionUpdateService(_context),
            new ShiftValidator(),
            new ScheduleMapper(),
            TestCompanyClock.Utc());
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task City_UsesTheWorkplaceFirst_ThenTheMainAddress_NeverTheInvoicingAddress()
    {
        (await FindAsync(new AddressLocationCriteria(null, [], "bern", null))).ShouldBe(["HIDDEN", "MAIN"]);
        (await FindAsync(new AddressLocationCriteria(null, [], "Thun", null))).ShouldBe(["WP"]);
    }

    [Test]
    public async Task CountryBoundState_DoesNotMixUpBernAndBerlin()
    {
        var bernCanton = new AddressLocationCriteria(null, [new StateCodeMatch("BE", "CH")], null, null);
        var anyBe = new AddressLocationCriteria(null, [new StateCodeMatch("BE", null)], null, null);

        (await FindAsync(bernCanton)).ShouldBe(["GONE", "HIDDEN", "MAIN", "WP"]);
        (await FindAsync(anyBe)).ShouldBe(["BERLIN", "GONE", "HIDDEN", "MAIN", "WP"]);
        (await FindAsync(new AddressLocationCriteria("de", [], null, null))).ShouldBe(["BERLIN"]);
    }

    [Test]
    public async Task ZipPrefix_AndCity_HoldOnTheSameOrderAddress()
    {
        (await FindAsync(new AddressLocationCriteria(null, [], null, "30"))).ShouldBe(["HIDDEN", "MAIN"]);
        (await FindAsync(new AddressLocationCriteria(null, [], "Bern", "3007"))).ShouldBe(["MAIN"]);
    }

    [Test]
    public async Task RestrictedCaller_NeverFindsAnOrderByTheAddressOfAHiddenCustomer()
    {
        var criteria = new AddressLocationCriteria(null, [], "Bern", null);

        (await FindAsync(criteria, [_scenario.VisibleGroup.Id])).ShouldBe(["MAIN"]);
        (await FindAsync(AddressLocationCriteria.None, [_scenario.VisibleGroup.Id]))
            .ShouldBe(["BERLIN", "GONE", "HIDDEN", "INV", "MAIN", "WP"]);
    }

    [Test]
    public async Task ClientSearch_CantonAndCity_MustHoldOnOneAddress()
    {
        var searchTerm = Prefix + _token;

        var thunInBern = await _scenario.ClientSearch.SearchAsync(
            searchTerm, "BE", null, null, "Thun", null, null, null, PageSize, CancellationToken.None);
        var bernInZurich = await _scenario.ClientSearch.SearchAsync(
            searchTerm, "ZH", null, null, "Bern", null, null, null, PageSize, CancellationToken.None);
        var deletedBern = await _scenario.ClientSearch.SearchAsync(
            searchTerm, null, null, null, "Bern", "3000", null, null, PageSize, CancellationToken.None);

        thunInBern.Items.Select(i => i.LastName).ShouldBe([ClientName("WP")]);
        bernInZurich.Items.ShouldBeEmpty();
        deletedBern.Items.ShouldBeEmpty();
    }

    [Test]
    public async Task UngroupedOrderCount_CountsOnlyRealOrdersWithoutALiveGroupLink()
    {
        var reader = new ShiftReader(_context);
        var before = await reader.CountUngroupedOrdersAsync();

        var customer = await SeedCustomerWithOrderAsync("COUNT_A", Addr(AddressTypeEnum.Employee, "Bern", "3011", "BE", "CH"));
        var grouped = NewOrder("COUNT_GROUPED", customer.Id, ShiftStatus.OriginalOrder);
        var scenarioCopy = NewOrder("COUNT_SCENARIO", customer.Id, ShiftStatus.OriginalOrder);
        scenarioCopy.AnalyseToken = Guid.NewGuid();
        var sealedOrder = NewOrder("COUNT_SEALED", customer.Id, ShiftStatus.SealedOrder);
        _context.Shift.AddRange(grouped, scenarioCopy, sealedOrder);
        _context.Set<GroupItem>().Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _scenario.VisibleGroup.Id, ShiftId = grouped.Id });
        await _context.SaveChangesAsync();

        var after = await reader.CountUngroupedOrdersAsync();

        after.OpenOrders.ShouldBe(before.OpenOrders + 1);
        after.SealedOrders.ShouldBe(before.SealedOrders + 1);
    }

    private async Task<List<string>> FindAsync(AddressLocationCriteria criteria, IReadOnlyCollection<Guid>? customerVisibleGroupIds = null)
    {
        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Original,
            IsSealedOrder = false,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true,
            IsTimeRange = true,
            IsSporadic = true,
            SearchString = _token,
            OrderBy = string.Empty,
            SortOrder = string.Empty,
            RequiredPage = 0,
            NumberOfItemsPerPage = PageSize
        };

        var result = await _shiftRepository.GetFilteredAndPaginatedShifts(
            filter, null, new ShiftLocationFilter(criteria, customerVisibleGroupIds));

        return result.Shifts
            .Select(s => s.Name[(Prefix.Length + _token.Length + 1)..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<Client> SeedCustomerWithOrderAsync(string suffix, params Address[] addresses)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = ClientName(suffix),
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = true,
            Type = EntityTypeEnum.Customer,
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidUntil = null
            }
        };
        foreach (var address in addresses)
        {
            address.ClientId = client.Id;
            client.Addresses.Add(address);
        }

        _context.Set<Client>().Add(client);
        _context.Shift.Add(NewOrder(suffix, client.Id, ShiftStatus.OriginalOrder));
        await _context.SaveChangesAsync();
        return client;
    }

    private string ClientName(string suffix) => $"{Prefix}{_token}_{suffix}";

    private Shift NewOrder(string suffix, Guid clientId, ShiftStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{Prefix}{_token}_{suffix}",
        Abbreviation = "OLS",
        Description = Prefix,
        Status = status,
        ShiftType = ShiftType.IsTask,
        ClientId = clientId,
        FromDate = new DateOnly(2026, 1, 1),
        UntilDate = null,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        AfterShift = new TimeOnly(0, 0),
        BeforeShift = new TimeOnly(0, 0),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 1,
    };

    private static Address Addr(AddressTypeEnum type, string city, string zip, string state, string country) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        City = city,
        Zip = zip,
        State = state,
        Country = country,
        ValidFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, {0}))", Prefix);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE starts_with(name, {0})", Prefix);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, {0}))", Prefix);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, {0}))", Prefix);
        await SupervisorVisibilityScenario.CleanupAsync(_context, Prefix);
    }
}
