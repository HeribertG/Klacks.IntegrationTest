// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Search;

[TestFixture]
[Category("RealDatabase")]
public class UnifiedBaseQueryTests
{
    private DataBaseContext _context = null!;
    private ClientBaseQueryService _baseQueryService = null!;
    private WorkRepository _workRepository = null!;
    private ListClientsQueryHandler _availabilityHandler = null!;

    [SetUp]
    public void Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);

        var mockGroupFilterService = Substitute.For<IClientGroupFilterService>();
        mockGroupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Klacks.Api.Domain.Models.Staffs.Client>>())
            .Returns(args => Task.FromResult((IQueryable<Klacks.Api.Domain.Models.Staffs.Client>)args[1]));

        var searchService = new ClientSearchService();
        var searchFilterService = new ClientSearchFilterService(searchService);

        _baseQueryService = new ClientBaseQueryService(_context, mockGroupFilterService, searchFilterService);

        _workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Klacks.Api.Domain.Models.Schedules.Work>>(),
            _baseQueryService,
            Substitute.For<Klacks.Api.Domain.Interfaces.Macros.IWorkMacroService>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Associations.IClientContractDataProvider>());

        _availabilityHandler = new ListClientsQueryHandler(
            _baseQueryService,
            Substitute.For<ILogger<ListClientsQueryHandler>>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task IdenticalParams_Schedule_And_Availability_MustMatch()
    {
        // Arrange
        var startDate = new DateOnly(2026, 3, 1);
        var endDate = new DateOnly(2026, 3, 31);
        const string searchString = "Ella Abel";

        // Act - WorkRepository (Schedule-Pfad)
        var workFilter = new Klacks.Api.Domain.Models.Filters.WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            SearchString = searchString,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc",
            StartRow = 0,
            RowCount = 200,
        };
        var (scheduleClients, scheduleTotalCount) = await _workRepository.WorkList(workFilter);

        // Act - ListClientsQueryHandler (Client-Availability-Pfad)
        var availabilityFilter = new ClientAvailabilityClientFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            SearchString = searchString,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc",
            StartRow = 0,
            RowCount = 200,
        };
        var availabilityResult = await _availabilityHandler.Handle(
            new ListClientAvailabilityClientsQuery(availabilityFilter),
            CancellationToken.None);

        // Assert
        Console.WriteLine($"Schedule:     {scheduleTotalCount} Clients");
        Console.WriteLine($"Availability: {availabilityResult.TotalCount} Clients");

        foreach (var c in scheduleClients)
        {
            Console.WriteLine($"  Schedule:     {c.Name}, {c.FirstName} (Type={c.Type}, IdNumber={c.IdNumber})");
        }
        foreach (var c in availabilityResult.Clients)
        {
            Console.WriteLine($"  Availability: {c.Name}, {c.FirstName} (IdNumber={c.IdNumber})");
        }

        scheduleTotalCount.Should().Be(availabilityResult.TotalCount,
            "Bei identischen Parametern müssen Schedule und Availability gleiche Ergebnisse liefern");
    }

    [Test]
    public async Task BaseQuery_ShowsAllMatchingClients_ForEllaAbel()
    {
        // Arrange
        var startDate = new DateOnly(2026, 3, 1);
        var endDate = new DateOnly(2026, 3, 31);
        const string searchString = "Ella Abel";

        var baseFilter = new ClientBaseFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            SearchString = searchString,
            ShowEmployees = true,
            ShowExtern = true,
        };

        // Act
        var query = await _baseQueryService.BuildBaseQuery(baseFilter);
        var results = await query.Select(c => new
        {
            c.Name,
            c.FirstName,
            c.Type,
            c.IdNumber,
            c.Membership!.ValidFrom,
            c.Membership.ValidUntil
        }).ToListAsync();

        // Assert
        Console.WriteLine($"BaseQuery count: {results.Count}");
        foreach (var c in results)
        {
            Console.WriteLine($"  {c.Name}, {c.FirstName} | Type={c.Type} | IdNr={c.IdNumber} | ValidFrom={c.ValidFrom:yyyy-MM-dd} | ValidUntil={c.ValidUntil?.ToString("yyyy-MM-dd") ?? "NULL"}");
        }

        results.Should().NotBeEmpty("'Ella Abel' sollte mindestens 1 Treffer haben");
    }

    [Test]
    public async Task DiagnosticTest_DifferentDateRanges()
    {
        // Arrange
        const string searchString = "Ella Abel";

        // Exact month (Client-Availability typical)
        var exactStart = new DateOnly(2026, 3, 1);
        var exactEnd = new DateOnly(2026, 3, 31);

        // Extended range (Schedule typical with dayVisibleBefore=10, dayVisibleAfter=10)
        var extendedStart = new DateOnly(2026, 2, 19);
        var extendedEnd = new DateOnly(2026, 4, 10);

        // Whole year (Absence-Gantt typical)
        var yearStart = new DateOnly(2026, 1, 1);
        var yearEnd = new DateOnly(2026, 12, 31);

        // Act
        var exactFilter = new ClientBaseFilter { StartDate = exactStart, EndDate = exactEnd, SearchString = searchString, ShowEmployees = true, ShowExtern = true };
        var extendedFilter = new ClientBaseFilter { StartDate = extendedStart, EndDate = extendedEnd, SearchString = searchString, ShowEmployees = true, ShowExtern = true };
        var yearFilter = new ClientBaseFilter { StartDate = yearStart, EndDate = yearEnd, SearchString = searchString, ShowEmployees = true, ShowExtern = true };

        var exactQuery = await _baseQueryService.BuildBaseQuery(exactFilter);
        var extendedQuery = await _baseQueryService.BuildBaseQuery(extendedFilter);
        var yearQuery = await _baseQueryService.BuildBaseQuery(yearFilter);

        var exactCount = await exactQuery.CountAsync();
        var extendedCount = await extendedQuery.CountAsync();
        var yearCount = await yearQuery.CountAsync();

        // Assert
        Console.WriteLine($"Exact month  (2026-03-01 bis 2026-03-31): {exactCount} Clients");
        Console.WriteLine($"Extended     (2026-02-19 bis 2026-04-10): {extendedCount} Clients");
        Console.WriteLine($"Whole year   (2026-01-01 bis 2026-12-31): {yearCount} Clients");

        if (exactCount != extendedCount || exactCount != yearCount)
        {
            Console.WriteLine("\nDIFFERENZ GEFUNDEN! Details:");

            var exactResults = await exactQuery.Select(c => new { c.Name, c.FirstName, c.IdNumber }).ToListAsync();
            var extendedResults = await extendedQuery.Select(c => new { c.Name, c.FirstName, c.IdNumber }).ToListAsync();
            var yearResults = await yearQuery.Select(c => new { c.Name, c.FirstName, c.IdNumber }).ToListAsync();

            var onlyInExtended = extendedResults.Where(e => !exactResults.Any(x => x.IdNumber == e.IdNumber)).ToList();
            var onlyInYear = yearResults.Where(y => !exactResults.Any(x => x.IdNumber == y.IdNumber)).ToList();

            Console.WriteLine($"\nNur in Extended (nicht in Exact): {onlyInExtended.Count}");
            foreach (var c in onlyInExtended)
            {
                Console.WriteLine($"  {c.Name}, {c.FirstName} (IdNr={c.IdNumber})");
            }

            Console.WriteLine($"\nNur in Year (nicht in Exact): {onlyInYear.Count}");
            foreach (var c in onlyInYear)
            {
                Console.WriteLine($"  {c.Name}, {c.FirstName} (IdNr={c.IdNumber})");
            }
        }

        exactCount.Should().Be(extendedCount, "Membership-Filter mit unterschiedlichen Zeiträumen sollte für aktive Clients kein Unterschied machen");
    }
}
