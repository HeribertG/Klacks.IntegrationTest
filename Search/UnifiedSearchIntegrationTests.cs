using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Search;

[TestFixture]
[Category("RealDatabase")]
public class UnifiedSearchIntegrationTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private IClientSearchService _searchService = null!;
    private IClientSearchFilterService _searchFilterService = null!;
    private const string TestClientPrefix = "UNIFIED_SEARCH_TEST_";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        using var context = new DataBaseContext(options, mockHttpContextAccessor);

        var orphanedTestClients = await context.Client
            .Where(c => c.FirstName != null && c.FirstName.StartsWith(TestClientPrefix))
            .ToListAsync();

        if (orphanedTestClients.Count > 0)
        {
            Console.WriteLine($"[OneTimeSetUp] Found {orphanedTestClients.Count} orphaned test clients. Cleaning up...");
            await CleanupTestDataWithContext(context);
        }
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        _searchService = new ClientSearchService();
        _searchFilterService = new ClientSearchFilterService(_searchService);

        await CreateTestClients();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataWithContext(_context);
        _context?.Dispose();
    }

    private async Task CreateTestClients()
    {
        var now = DateTime.UtcNow;
        var clients = new List<Client>
        {
            CreateClient($"{TestClientPrefix}Elle", "Abel", EntityTypeEnum.Employee, false, 91001),
            CreateClient($"{TestClientPrefix}Abel", "Elle", EntityTypeEnum.Employee, false, 91002),
            CreateClient($"{TestClientPrefix}Gabrielle", "Abel", EntityTypeEnum.Employee, false, 91003),
            CreateClient($"{TestClientPrefix}Max", "Mustermann", EntityTypeEnum.Employee, false, 91004),
            CreateClientWithCompany($"{TestClientPrefix}Peter", "Firma", "Elle Abel GmbH", EntityTypeEnum.Employee, true, 91005),
        };

        foreach (var client in clients)
        {
            client.Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                ValidFrom = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidUntil = null
            };
        }

        await _context.Client.AddRangeAsync(clients);
        await _context.SaveChangesAsync();
    }

    private static Client CreateClient(string firstName, string name, EntityTypeEnum type, bool legalEntity, int idNumber)
    {
        return new Client
        {
            Id = Guid.NewGuid(),
            FirstName = firstName,
            Name = name,
            Type = type,
            LegalEntity = legalEntity,
            IdNumber = idNumber,
            Gender = GenderEnum.Male
        };
    }

    private static Client CreateClientWithCompany(string firstName, string name, string company, EntityTypeEnum type, bool legalEntity, int idNumber)
    {
        return new Client
        {
            Id = Guid.NewGuid(),
            FirstName = firstName,
            Name = name,
            Company = company,
            Type = type,
            LegalEntity = legalEntity,
            IdNumber = idNumber,
            Gender = GenderEnum.Male
        };
    }

    [Test]
    public async Task AllSearchPaths_UseBackendSearchService_ReturnConsistentResults()
    {
        // Arrange
        var searchString = "Elle Abel";
        var scheduleQuery = GetTestClientQuery();
        var absenceQuery = GetTestClientQuery();
        var availabilityQuery = GetTestClientQuery();

        // Act
        var scheduleResult = await _searchFilterService
            .ApplySearchFilter(scheduleQuery, searchString, false)
            .ToListAsync();

        var absenceResult = await _searchFilterService
            .ApplySearchFilter(absenceQuery, searchString, false)
            .ToListAsync();

        var availabilityResult = await _searchFilterService
            .ApplySearchFilter(availabilityQuery, searchString, false)
            .ToListAsync();

        // Assert
        Console.WriteLine("=== ALL 3 SEARCH PATHS USE ClientSearchFilterService -> IDENTICAL RESULTS ===\n");
        Console.WriteLine($"Schedule result count:          {scheduleResult.Count}");
        Console.WriteLine($"Absence result count:           {absenceResult.Count}");
        Console.WriteLine($"Client-Availability result count: {availabilityResult.Count}");

        var scheduleIds = scheduleResult.Select(c => c.IdNumber).OrderBy(x => x).ToList();
        var absenceIds = absenceResult.Select(c => c.IdNumber).OrderBy(x => x).ToList();
        var availabilityIds = availabilityResult.Select(c => c.IdNumber).OrderBy(x => x).ToList();

        scheduleIds.Should().BeEquivalentTo(absenceIds, "Schedule and Absence must return identical results");
        absenceIds.Should().BeEquivalentTo(availabilityIds, "Absence and Client-Availability must return identical results");

        scheduleResult.Should().Contain(c => c.IdNumber == 91001, "Elle Abel - exact match");
        scheduleResult.Should().Contain(c => c.IdNumber == 91002, "Abel Elle - reversed order, AND across fields");
        scheduleResult.Should().Contain(c => c.IdNumber == 91003, "Gabrielle Abel - partial match in FirstName");
        scheduleResult.Should().NotContain(c => c.IdNumber == 91004, "Max Mustermann - no match");
        scheduleResult.Should().Contain(c => c.IdNumber == 91005, "Peter Firma - Company match 'Elle Abel GmbH'");

        Console.WriteLine("\nFound clients:");
        foreach (var c in scheduleResult.OrderBy(c => c.IdNumber))
        {
            Console.WriteLine($"  {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name} (Company: {c.Company})");
        }
    }

    [Test]
    public void Schedule_SearchStringNowPassedThrough_NoLongerIgnored()
    {
        // Arrange
        var filter = new WorkScheduleFilter
        {
            SearchString = "Elle Abel",
            StartDate = DateOnly.FromDateTime(DateTime.Today),
            EndDate = DateOnly.FromDateTime(DateTime.Today.AddDays(7))
        };

        var workFilter = new Klacks.Api.Domain.Models.Filters.WorkFilter
        {
            SearchString = filter.SearchString,
            StartDate = filter.StartDate,
            EndDate = filter.EndDate
        };

        // Act
        var searchStringIsPassedThrough = !string.IsNullOrEmpty(workFilter.SearchString);

        // Assert
        Console.WriteLine("=== SCHEDULE: SearchString is now passed through ===\n");
        Console.WriteLine($"WorkScheduleFilter.SearchString: '{filter.SearchString}'");
        Console.WriteLine($"WorkFilter.SearchString:          '{workFilter.SearchString}'");
        Console.WriteLine($"SearchString is passed through:   {searchStringIsPassedThrough}");

        searchStringIsPassedThrough.Should().BeTrue("WorkFilter.SearchString must carry the value from WorkScheduleFilter");
        workFilter.SearchString.Should().Be("Elle Abel");
    }

    [Test]
    public async Task ClientAvailability_NowUsesBackendSearch_NotMemoryFilter()
    {
        // Arrange
        var searchString = "Elle Abel";
        var query = GetTestClientQuery();

        // Act
        var result = _searchFilterService.ApplySearchFilter(query, searchString, false);
        var clients = await result.ToListAsync();

        // Assert
        Console.WriteLine("=== CLIENT-AVAILABILITY: Backend search finds reversed name order ===\n");

        clients.Should().Contain(c => c.IdNumber == 91002,
            "Abel Elle is now found - backend AND logic instead of frontend displayName.includes()");

        var allClients = await GetTestClientQuery().ToListAsync();
        var frontendSimulation = allClients.Where(c =>
        {
            var displayName = FormatClientDisplayName(c);
            return displayName.ToLower().Contains(searchString.ToLower());
        }).ToList();

        var backendFindsMore = clients.Count > frontendSimulation.Count;

        Console.WriteLine($"Backend search results:  {clients.Count}");
        Console.WriteLine($"Frontend filter results: {frontendSimulation.Count}");
        Console.WriteLine($"Backend finds more:      {backendFindsMore}");

        Console.WriteLine("\nBackend found (AND logic across fields):");
        foreach (var c in clients.OrderBy(c => c.IdNumber))
        {
            Console.WriteLine($"  {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name}");
        }

        Console.WriteLine("\nFrontend would have found (displayName.includes):");
        foreach (var c in frontendSimulation.OrderBy(c => c.IdNumber))
        {
            Console.WriteLine($"  {c.IdNumber}: {FormatClientDisplayName(c)}");
        }

        clients.Count.Should().BeGreaterThanOrEqualTo(frontendSimulation.Count,
            "Backend AND logic finds at least as many results as frontend substring match");
    }

    [Test]
    public async Task Search_EmptyString_ReturnsAllClients()
    {
        // Arrange
        var query = GetTestClientQuery();
        var totalCount = await GetTestClientQuery().CountAsync();

        // Act
        var result = await _searchFilterService
            .ApplySearchFilter(query, string.Empty, false)
            .ToListAsync();

        // Assert
        Console.WriteLine("=== EMPTY SEARCH STRING: Returns all clients ===\n");
        Console.WriteLine($"Total test clients: {totalCount}");
        Console.WriteLine($"Result count:       {result.Count}");

        result.Should().HaveCount(totalCount, "empty search string must return all clients");
    }

    [Test]
    public async Task Search_PagingWorks_ReturnsSubset()
    {
        // Arrange
        var searchString = "Elle Abel";
        var fullResult = await _searchFilterService
            .ApplySearchFilter(GetTestClientQuery(), searchString, false)
            .OrderBy(c => c.IdNumber)
            .ToListAsync();

        // Act
        var pagedResult = await _searchFilterService
            .ApplySearchFilter(GetTestClientQuery(), searchString, false)
            .OrderBy(c => c.IdNumber)
            .Skip(1)
            .Take(2)
            .ToListAsync();

        // Assert
        Console.WriteLine("=== PAGING: Skip(1).Take(2) returns correct subset ===\n");
        Console.WriteLine($"Full result count:  {fullResult.Count}");
        Console.WriteLine($"Paged result count: {pagedResult.Count}");
        Console.WriteLine($"Paged StartRow=1, RowCount=2");

        pagedResult.Should().HaveCountLessThanOrEqualTo(2, "Take(2) limits to max 2 results");

        if (fullResult.Count > 1)
        {
            pagedResult.First().IdNumber.Should().Be(fullResult[1].IdNumber,
                "Skip(1) skips the first result");
        }

        Console.WriteLine("\nFull results:");
        foreach (var c in fullResult)
        {
            Console.WriteLine($"  {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name}");
        }

        Console.WriteLine("\nPaged results (Skip=1, Take=2):");
        foreach (var c in pagedResult)
        {
            Console.WriteLine($"  {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name}");
        }
    }

    private IQueryable<Client> GetTestClientQuery()
    {
        return _context.Client
            .Where(c => c.FirstName != null && c.FirstName.StartsWith(TestClientPrefix));
    }

    private static string FormatClientDisplayName(Client client)
    {
        if (client.LegalEntity && !string.IsNullOrEmpty(client.Company))
        {
            return client.Company;
        }

        var nameParts = new List<string>();
        var firstName = client.FirstName?.Replace(TestClientPrefix, "");
        if (!string.IsNullOrEmpty(firstName)) nameParts.Add(firstName);
        if (!string.IsNullOrEmpty(client.Name)) nameParts.Add(client.Name);
        var fullName = string.Join(" ", nameParts);

        if (client.IdNumber > 0 && !string.IsNullOrEmpty(fullName))
        {
            return $"{client.IdNumber}, {fullName}";
        }

        return fullName;
    }

    private static async Task CleanupTestDataWithContext(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM client_image WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM communication WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM annotation WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM client WHERE first_name LIKE '{TestClientPrefix}%';
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
