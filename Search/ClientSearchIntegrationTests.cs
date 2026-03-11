using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Search;

[TestFixture]
[Category("RealDatabase")]
public class ClientSearchIntegrationTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private IClientSearchService _searchService = null!;
    private IClientSearchFilterService _searchFilterService = null!;
    private const string TestClientPrefix = "SEARCH_TEST_";

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
            CreateClient($"{TestClientPrefix}Elle", "Abel", EntityTypeEnum.Employee, false, 90001),
            CreateClient($"{TestClientPrefix}Abel", "Elle", EntityTypeEnum.Employee, false, 90002),
            CreateClient($"{TestClientPrefix}Gabrielle", "Abel", EntityTypeEnum.Employee, false, 90003),
            CreateClient($"{TestClientPrefix}Helle", "Abelman", EntityTypeEnum.Employee, false, 90004),
            CreateClient($"{TestClientPrefix}Elena", "Mabel", EntityTypeEnum.Employee, false, 90005),
            CreateClient($"{TestClientPrefix}Max", "Mustermann", EntityTypeEnum.Employee, false, 90006),
            CreateClient($"{TestClientPrefix}Anna", "Schmidt", EntityTypeEnum.ExternEmp, false, 90007),
            CreateClientWithCompany($"{TestClientPrefix}Peter", "Firma", "Elle Abel GmbH", EntityTypeEnum.Employee, true, 90008),
            CreateClient($"{TestClientPrefix}Elle", "Abelson", EntityTypeEnum.Employee, false, 90009),
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
    public async Task BackendSearch_ElleAbel_StandardSearch_FindsExpectedClients()
    {
        // Arrange
        var searchString = "Elle Abel";
        var query = GetTestClientQuery();

        // Act
        var result = _searchFilterService.ApplySearchFilter(query, searchString, false);
        var clients = await result.ToListAsync();

        // Assert
        Console.WriteLine("=== BACKEND SEARCH: 'Elle Abel' (StandardSearch - AND-Logik) ===\n");
        Console.WriteLine($"Gefundene Clients: {clients.Count}");
        foreach (var c in clients)
        {
            Console.WriteLine($"  IdNumber={c.IdNumber}, FirstName='{c.FirstName?.Replace(TestClientPrefix, "")}', Name='{c.Name}', Company='{c.Company}'");
        }

        clients.Should().Contain(c => c.IdNumber == 90001, "Elle Abel - exakter Match");
        clients.Should().Contain(c => c.IdNumber == 90002, "Abel Elle - umgekehrte Reihenfolge, AND auf verschiedene Felder");
        clients.Should().Contain(c => c.IdNumber == 90003, "Gabrielle Abel - 'elle' in FirstName enthalten");
        clients.Should().Contain(c => c.IdNumber == 90009, "Elle Abelson - 'abel' in Name enthalten");
        clients.Should().NotContain(c => c.IdNumber == 90006, "Max Mustermann - kein Match");
        clients.Should().NotContain(c => c.IdNumber == 90007, "Anna Schmidt - kein Match");

        Console.WriteLine("\n=== BACKEND: Sucht 'elle' AND 'abel' in Name/FirstName/SecondName/MaidenName/Company ===");
    }

    [Test]
    public async Task FrontendFilter_ElleAbel_DisplayNameIncludes_FindsExpectedClients()
    {
        // Arrange
        var searchTerm = "elle abel";
        var allClients = await GetTestClientQuery().ToListAsync();

        // Act
        var filteredClients = allClients.Where(c =>
        {
            var displayName = FormatClientDisplayName(c);
            return displayName.ToLower().Contains(searchTerm.ToLower());
        }).ToList();

        // Assert
        Console.WriteLine("=== FRONTEND FILTER: 'elle abel' (displayName.includes - Client-Availability) ===\n");
        Console.WriteLine($"Gefundene Clients: {filteredClients.Count}");
        foreach (var c in filteredClients)
        {
            var displayName = FormatClientDisplayName(c);
            Console.WriteLine($"  IdNumber={c.IdNumber}, displayName='{displayName}'");
        }

        Console.WriteLine("\nNICHT gefunden (aber vom Backend gefunden):");
        var backendQuery = _searchFilterService.ApplySearchFilter(GetTestClientQuery(), "Elle Abel", false);
        var backendClients = await backendQuery.ToListAsync();
        var missingInFrontend = backendClients.Where(bc =>
            !filteredClients.Any(fc => fc.IdNumber == bc.IdNumber)).ToList();
        foreach (var c in missingInFrontend)
        {
            var displayName = FormatClientDisplayName(c);
            Console.WriteLine($"  IdNumber={c.IdNumber}, displayName='{displayName}' -> NICHT gefunden weil '{searchTerm}' kein zusammenhängender Substring ist!");
        }

        Console.WriteLine("\n=== FRONTEND: Sucht 'elle abel' als zusammenhängenden Substring in displayName ===");
    }

    [Test]
    public async Task ScheduleHandler_SearchStringIgnored_ReturnsAllClients()
    {
        // Arrange
        var searchStringFromUser = "Elle Abel";
        var query = GetTestClientQuery();

        var searchStringPassedToRepository = string.Empty;

        // Act
        var withSearch = await _searchFilterService
            .ApplySearchFilter(query, searchStringFromUser, false)
            .CountAsync();

        var withoutSearch = await _searchFilterService
            .ApplySearchFilter(GetTestClientQuery(), searchStringPassedToRepository, false)
            .CountAsync();

        // Assert
        Console.WriteLine("=== SCHEDULE BUG: SearchString wird auf string.Empty gesetzt ===\n");
        Console.WriteLine($"User gibt ein: '{searchStringFromUser}'");
        Console.WriteLine($"GetScheduleEntriesQueryHandler setzt: SearchString = string.Empty");
        Console.WriteLine($"");
        Console.WriteLine($"Ergebnis MIT Suche '{searchStringFromUser}': {withSearch} Clients");
        Console.WriteLine($"Ergebnis OHNE Suche (string.Empty): {withoutSearch} Clients");
        Console.WriteLine($"");
        Console.WriteLine($"Differenz: {withoutSearch - withSearch} Clients werden FÄLSCHLICHERWEISE angezeigt!");

        withoutSearch.Should().BeGreaterThan(withSearch,
            "SearchString=string.Empty gibt alle Clients zurück, SearchString='Elle Abel' filtert korrekt");

        Console.WriteLine("\n=== BUG: GetScheduleEntriesQueryHandler.cs Zeile 147: SearchString = string.Empty ===");
    }

    [Test]
    public async Task Compare_AllThreeSearchApproaches_ShowDifferences()
    {
        // Arrange
        var searchString = "Elle Abel";
        var query = GetTestClientQuery();

        // Act
        var backendResults = await _searchFilterService
            .ApplySearchFilter(query, searchString, false)
            .ToListAsync();

        var scheduleResults = await _searchFilterService
            .ApplySearchFilter(GetTestClientQuery(), string.Empty, false)
            .ToListAsync();

        var allClients = await GetTestClientQuery().ToListAsync();
        var frontendResults = allClients.Where(c =>
        {
            var displayName = FormatClientDisplayName(c);
            return displayName.ToLower().Contains(searchString.ToLower());
        }).ToList();

        // Assert
        Console.WriteLine("=== VERGLEICH ALLER 3 SUCHANSÄTZE für 'Elle Abel' ===\n");

        Console.WriteLine($"1. SCHEDULE (Bug - SearchString ignoriert): {scheduleResults.Count} Clients (ALLE!)");
        Console.WriteLine($"2. ABSENCE-GANTT (Backend-Suche korrekt):   {backendResults.Count} Clients");
        Console.WriteLine($"3. CLIENT-AVAILABILITY (Frontend-Filter):    {frontendResults.Count} Clients");

        Console.WriteLine("\n--- Details ---\n");

        Console.WriteLine("ABSENCE-GANTT (Backend AND-Logik):");
        foreach (var c in backendResults.OrderBy(c => c.IdNumber))
        {
            Console.WriteLine($"  {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name} (Company: {c.Company})");
        }

        Console.WriteLine("\nCLIENT-AVAILABILITY (displayName.includes):");
        foreach (var c in frontendResults.OrderBy(c => c.IdNumber))
        {
            var dn = FormatClientDisplayName(c);
            Console.WriteLine($"  {c.IdNumber}: displayName='{dn}'");
        }

        Console.WriteLine("\nUNTERSCHIEDE:");

        var onlyInBackend = backendResults.Where(bc =>
            !frontendResults.Any(fc => fc.IdNumber == bc.IdNumber)).ToList();
        if (onlyInBackend.Count > 0)
        {
            Console.WriteLine("  Nur im Backend (Absence) gefunden:");
            foreach (var c in onlyInBackend)
            {
                var dn = FormatClientDisplayName(c);
                Console.WriteLine($"    {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name} -> displayName='{dn}' enthält NICHT 'elle abel' als Substring");
            }
        }

        var onlyInFrontend = frontendResults.Where(fc =>
            !backendResults.Any(bc => bc.IdNumber == fc.IdNumber)).ToList();
        if (onlyInFrontend.Count > 0)
        {
            Console.WriteLine("  Nur im Frontend (Client-Availability) gefunden:");
            foreach (var c in onlyInFrontend)
            {
                Console.WriteLine($"    {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name}");
            }
        }

        if (onlyInBackend.Count == 0 && onlyInFrontend.Count == 0)
        {
            Console.WriteLine("  Keine Unterschiede zwischen Backend und Frontend-Filter");
        }

        scheduleResults.Count.Should().BeGreaterThan(backendResults.Count,
            "Schedule zeigt ALLE Clients weil SearchString ignoriert wird");

        Console.WriteLine("\n=== FAZIT ===");
        Console.WriteLine("1. Schedule: SearchString wird NICHT weitergeleitet (BUG in GetScheduleEntriesQueryHandler.cs:147)");
        Console.WriteLine("2. Absence: Backend-Suche funktioniert korrekt (AND-Logik über einzelne Felder)");
        Console.WriteLine("3. Client-Availability: Frontend-Filter sucht als zusammenhängenden Substring");
        Console.WriteLine("   -> Unterschiedliche Ergebnisse wenn Name-Teile in verschiedenen Feldern oder umgekehrter Reihenfolge stehen");
    }

    [Test]
    public async Task BackendSearch_ExactSearchWithPlus_UsesOrLogic()
    {
        // Arrange
        var searchString = "Elle+Abel";
        var query = GetTestClientQuery();

        // Act
        var result = await _searchFilterService
            .ApplySearchFilter(query, searchString, false)
            .ToListAsync();

        // Assert
        Console.WriteLine("=== BACKEND SEARCH: 'Elle+Abel' (ExactSearch - OR-Logik) ===\n");
        Console.WriteLine($"Gefundene Clients: {result.Count}");
        foreach (var c in result.OrderBy(c => c.IdNumber))
        {
            Console.WriteLine($"  {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name}");
        }

        result.Count.Should().BeGreaterThanOrEqualTo(1,
            "OR-Suche findet mehr oder gleich viele wie AND-Suche");

        Console.WriteLine("\n=== '+' Separator verwendet OR-Logik statt AND ===");
    }

    [Test]
    public async Task BackendSearch_SingleKeyword_UsesExactSearch()
    {
        // Arrange
        var searchString = "Abel";
        var query = GetTestClientQuery();

        // Act
        var result = await _searchFilterService
            .ApplySearchFilter(query, searchString, false)
            .ToListAsync();

        // Assert
        Console.WriteLine("=== BACKEND SEARCH: 'Abel' (Single Keyword - ExactSearch) ===\n");
        Console.WriteLine($"Gefundene Clients: {result.Count}");
        foreach (var c in result.OrderBy(c => c.IdNumber))
        {
            Console.WriteLine($"  {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name}");
        }

        result.Should().Contain(c => c.IdNumber == 90001, "Elle Abel");
        result.Should().Contain(c => c.IdNumber == 90002, "Abel Elle");
        result.Should().Contain(c => c.IdNumber == 90003, "Gabrielle Abel");
        result.Should().Contain(c => c.IdNumber == 90005, "Elena Mabel - 'abel' in Name 'Mabel' enthalten");
        result.Should().Contain(c => c.IdNumber == 90009, "Elle Abelson");
    }

    [Test]
    public async Task BackendSearch_SingleCharacter_UsesStartsWithSearch()
    {
        // Arrange
        var searchString = "A";
        var query = GetTestClientQuery();

        // Act
        var result = await _searchFilterService
            .ApplySearchFilter(query, searchString, false)
            .ToListAsync();

        // Assert
        Console.WriteLine("=== BACKEND SEARCH: 'A' (Single Character - StartsWith) ===\n");
        Console.WriteLine($"Gefundene Clients: {result.Count}");
        foreach (var c in result.OrderBy(c => c.IdNumber))
        {
            Console.WriteLine($"  {c.IdNumber}: {c.FirstName?.Replace(TestClientPrefix, "")} {c.Name}");
        }

        result.Should().Contain(c => c.IdNumber == 90001, "Elle Abel - Name startet mit 'A'");
        result.Should().Contain(c => c.IdNumber == 90003, "Gabrielle Abel - Name startet mit 'A'");
        result.Should().NotContain(c => c.IdNumber == 90007, "Anna Schmidt - FirstName hat TestPrefix, startet nicht mit 'A'");

        Console.WriteLine("\n=== Einzelnes Zeichen nutzt StartsWith statt Contains ===");
    }

    [Test]
    public async Task FrontendFilter_ReversedNameOrder_NotFound()
    {
        // Arrange
        var searchTerm = "elle abel";
        var reversedClient = await _context.Client
            .FirstOrDefaultAsync(c => c.IdNumber == 90002);

        // Act
        var displayName = FormatClientDisplayName(reversedClient!);
        var isFound = displayName.ToLower().Contains(searchTerm);

        // Assert
        Console.WriteLine("=== FRONTEND BUG: Umgekehrte Reihenfolge wird NICHT gefunden ===\n");
        Console.WriteLine($"Client: FirstName='{reversedClient!.FirstName?.Replace(TestClientPrefix, "")}', Name='{reversedClient.Name}'");
        Console.WriteLine($"displayName: '{displayName}'");
        Console.WriteLine($"Suchbegriff: '{searchTerm}'");
        Console.WriteLine($"displayName.includes(suchbegriff): {isFound}");
        Console.WriteLine();
        Console.WriteLine("Backend-Suche findet diesen Client, weil:");
        Console.WriteLine("  'elle' -> matches Name='Elle' (Contains)");
        Console.WriteLine("  'abel' -> matches FirstName='Abel' (Contains)");
        Console.WriteLine("  -> AND-Verknüpfung ist erfüllt");
        Console.WriteLine();
        Console.WriteLine("Frontend-Filter findet ihn NICHT, weil:");
        Console.WriteLine($"  displayName='{displayName}' enthält NICHT 'elle abel' als zusammenhängenden Substring");

        isFound.Should().BeFalse("displayName '{0}' enthält nicht 'elle abel' als zusammenhängenden Substring", displayName);
    }

    private IQueryable<Client> GetTestClientQuery()
    {
        return _context.Client
            .Where(c => c.FirstName != null && c.FirstName.StartsWith(TestClientPrefix))
            .Where(c => c.Type != EntityTypeEnum.Customer);
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
