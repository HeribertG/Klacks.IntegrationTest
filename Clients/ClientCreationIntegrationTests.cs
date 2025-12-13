using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace IntegrationTest.Clients;

/// <summary>
/// Integration tests for Client creation with all related tables.
///
/// Database Connection:
/// - Host: localhost
/// - Port: 5434
/// - Database: klacks1
/// - Username: postgres
/// - Password: admin
///
/// Connection String: "Host=localhost;Port=5434;Database=klacks1;Username=postgres;Password=admin"
///
/// Or use environment variable DATABASE_URL
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class ClientCreationIntegrationTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private Guid _testClientId;
    private const string TestClientPrefix = "INTEGRATION_TEST_";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks1;Username=postgres;Password=admin";

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
            Console.WriteLine("[OneTimeSetUp] Cleanup completed.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestData();
        _context?.Dispose();
    }

    private async Task CleanupTestData()
    {
        await CleanupTestDataWithContext(_context);
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

    [Test]
    public async Task CreateClient_WithAllRelatedTables_Should_Persist_All_Data()
    {
        // Arrange
        _testClientId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var client = new Client
        {
            Id = _testClientId,
            FirstName = $"{TestClientPrefix}Max",
            Name = "Mustermann",
            Gender = GenderEnum.Male,
            Type = EntityTypeEnum.Employee,
            Birthdate = new DateTime(1990, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            IdNumber = 99999,
            LegalEntity = false,
            Company = "Test Company GmbH"
        };

        client.Membership = new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ValidFrom = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = null
        };

        client.Addresses.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            Street = "Teststrasse 123",
            Zip = "3000",
            City = "Bern",
            Country = "CH",
            State = "BE",
            Type = AddressTypeEnum.Employee,
            ValidFrom = now
        });

        client.Communications.Add(new Communication
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            Value = "+41 79 123 45 67",
            Type = CommunicationTypeEnum.PrivateCellPhone,
            Prefix = "+41"
        });

        client.Communications.Add(new Communication
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            Value = "max.mustermann@test.ch",
            Type = CommunicationTypeEnum.PrivateMail,
            Prefix = ""
        });

        client.Annotations.Add(new Annotation
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            Note = "This is a test annotation for the integration test."
        });

        client.ClientImage = new ClientImage
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ImageData = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            ContentType = "image/png",
            FileName = "test-avatar.png",
            FileSize = 8
        };

        var existingContract = await _context.Contract.FirstOrDefaultAsync();
        if (existingContract != null)
        {
            client.ClientContracts.Add(new ClientContract
            {
                Id = Guid.NewGuid(),
                ClientId = _testClientId,
                ContractId = existingContract.Id,
                IsActive = true,
                FromDate = new DateOnly(now.Year, 1, 1),
                UntilDate = null
            });
        }

        var existingGroup = await _context.Group.FirstOrDefaultAsync(g => !g.IsDeleted);
        if (existingGroup != null)
        {
            client.GroupItems.Add(new GroupItem
            {
                Id = Guid.NewGuid(),
                ClientId = _testClientId,
                GroupId = existingGroup.Id,
                ValidFrom = now,
                ValidUntil = null
            });
        }

        // Act
        await _context.Client.AddAsync(client);
        await _context.SaveChangesAsync();

        // Assert
        Console.WriteLine("=== CLIENT CREATION INTEGRATION TEST ===\n");

        var savedClient = await _context.Client
            .Include(c => c.Membership)
            .Include(c => c.Addresses)
            .Include(c => c.Communications)
            .Include(c => c.Annotations)
            .Include(c => c.ClientImage)
            .Include(c => c.ClientContracts)
            .Include(c => c.GroupItems)
            .FirstOrDefaultAsync(c => c.Id == _testClientId);

        savedClient.Should().NotBeNull("Client should be saved");
        Console.WriteLine($"Client: {savedClient!.FirstName} {savedClient.Name} (ID: {savedClient.Id})");
        Console.WriteLine($"  Type: {savedClient.Type}");
        Console.WriteLine($"  IdNumber: {savedClient.IdNumber}");

        savedClient.Membership.Should().NotBeNull("Membership should be saved");
        Console.WriteLine($"\nMembership:");
        Console.WriteLine($"  ValidFrom: {savedClient.Membership!.ValidFrom:yyyy-MM-dd}");
        Console.WriteLine($"  ValidUntil: {savedClient.Membership.ValidUntil?.ToString("yyyy-MM-dd") ?? "NULL (unlimited)"}");

        savedClient.Addresses.Should().HaveCount(1, "Address should be saved");
        var address = savedClient.Addresses.First();
        Console.WriteLine($"\nAddress:");
        Console.WriteLine($"  Street: {address.Street}");
        Console.WriteLine($"  City: {address.Zip} {address.City}");
        Console.WriteLine($"  Country: {address.Country}, State: {address.State}");

        savedClient.Communications.Should().HaveCount(2, "Communications should be saved");
        Console.WriteLine($"\nCommunications ({savedClient.Communications.Count}):");
        foreach (var comm in savedClient.Communications)
        {
            Console.WriteLine($"  {comm.Type}: {comm.Value}");
        }

        savedClient.Annotations.Should().HaveCount(1, "Annotation should be saved");
        var annotation = savedClient.Annotations.First();
        Console.WriteLine($"\nAnnotation:");
        Console.WriteLine($"  Note: {annotation.Note}");

        savedClient.ClientImage.Should().NotBeNull("ClientImage should be saved");
        Console.WriteLine($"\nClientImage:");
        Console.WriteLine($"  FileName: {savedClient.ClientImage!.FileName}");
        Console.WriteLine($"  ContentType: {savedClient.ClientImage.ContentType}");
        Console.WriteLine($"  FileSize: {savedClient.ClientImage.FileSize} bytes");
        Console.WriteLine($"  ImageData length: {savedClient.ClientImage.ImageData?.Length ?? 0} bytes");

        if (existingContract != null)
        {
            savedClient.ClientContracts.Should().HaveCount(1, "ClientContract should be saved");
            var contract = savedClient.ClientContracts.First();
            Console.WriteLine($"\nClientContract:");
            Console.WriteLine($"  ContractId: {contract.ContractId}");
            Console.WriteLine($"  IsActive: {contract.IsActive}");
            Console.WriteLine($"  FromDate: {contract.FromDate:yyyy-MM-dd}");
        }
        else
        {
            Console.WriteLine("\nClientContract: SKIPPED (no existing contract in database)");
        }

        if (existingGroup != null)
        {
            savedClient.GroupItems.Should().HaveCount(1, "GroupItem should be saved");
            var groupItem = savedClient.GroupItems.First();
            Console.WriteLine($"\nGroupItem:");
            Console.WriteLine($"  GroupId: {groupItem.GroupId}");
            Console.WriteLine($"  ValidFrom: {groupItem.ValidFrom:yyyy-MM-dd}");
        }
        else
        {
            Console.WriteLine("\nGroupItem: SKIPPED (no existing group in database)");
        }

        Console.WriteLine("\n=== ALL TABLES VERIFIED SUCCESSFULLY ===");
    }

    [Test]
    public async Task CreateClient_Without_Membership_Should_Fail_Filter()
    {
        // Arrange
        _testClientId = Guid.NewGuid();
        var currentYear = DateTime.Now.Year;
        var startOfYear = new DateTime(currentYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var endOfYear = new DateTime(currentYear, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var clientWithoutMembership = new Client
        {
            Id = _testClientId,
            FirstName = $"{TestClientPrefix}NoMembership",
            Name = "TestClient",
            Gender = GenderEnum.Male,
            Type = EntityTypeEnum.Employee,
            IdNumber = 99998
        };

        // Act
        await _context.Client.AddAsync(clientWithoutMembership);
        await _context.SaveChangesAsync();

        var clientWithMembershipFilter = await _context.Client
            .Include(c => c.Membership)
            .Where(c => c.Id == _testClientId)
            .Where(c => c.Membership != null &&
                        c.Membership.ValidFrom <= endOfYear &&
                        (!c.Membership.ValidUntil.HasValue || c.Membership.ValidUntil.Value >= startOfYear))
            .FirstOrDefaultAsync();

        var clientWithoutFilter = await _context.Client
            .FirstOrDefaultAsync(c => c.Id == _testClientId);

        // Assert
        Console.WriteLine("=== MEMBERSHIP FILTER TEST ===\n");
        Console.WriteLine($"Client without filter: {(clientWithoutFilter != null ? "FOUND" : "NOT FOUND")}");
        Console.WriteLine($"Client with membership filter: {(clientWithMembershipFilter != null ? "FOUND" : "NOT FOUND")}");

        clientWithoutFilter.Should().NotBeNull("Client should exist in database");
        clientWithMembershipFilter.Should().BeNull("Client without membership should NOT pass the filter");

        Console.WriteLine("\n=== TEST PASSED: Client without Membership is correctly filtered out ===");
    }

    [Test]
    public async Task VerifyDatabaseTables_DirectQuery()
    {
        // Arrange
        _testClientId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var client = new Client
        {
            Id = _testClientId,
            FirstName = $"{TestClientPrefix}DirectQuery",
            Name = "TestClient",
            Gender = GenderEnum.Female,
            Type = EntityTypeEnum.ExternEmp,
            IdNumber = 99997
        };

        client.Membership = new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ValidFrom = now
        };

        client.Addresses.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            Street = "Direct Query Street",
            Zip = "1234",
            City = "TestCity",
            Country = "CH",
            ValidFrom = now
        });

        client.Communications.Add(new Communication
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            Value = "direct@test.ch",
            Type = CommunicationTypeEnum.PrivateMail
        });

        client.Annotations.Add(new Annotation
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            Note = "Direct query test note"
        });

        client.ClientImage = new ClientImage
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ImageData = new byte[] { 0x01, 0x02 },
            ContentType = "image/png",
            FileName = "direct-test.png",
            FileSize = 2
        };

        await _context.Client.AddAsync(client);
        await _context.SaveChangesAsync();

        // Act & Assert
        Console.WriteLine("=== DIRECT DATABASE TABLE VERIFICATION ===\n");

        var clientCount = await _context.Client.CountAsync(c => c.Id == _testClientId);
        var membershipCount = await _context.Membership.CountAsync(m => m.ClientId == _testClientId);
        var addressCount = await _context.Address.CountAsync(a => a.ClientId == _testClientId);
        var communicationCount = await _context.Communication.CountAsync(c => c.ClientId == _testClientId);
        var annotationCount = await _context.Annotation.CountAsync(a => a.ClientId == _testClientId);
        var clientImageCount = await _context.ClientImage.CountAsync(ci => ci.ClientId == _testClientId);

        Console.WriteLine("Table Counts for Test Client:");
        Console.WriteLine($"  client:        {clientCount} (expected: 1)");
        Console.WriteLine($"  membership:    {membershipCount} (expected: 1)");
        Console.WriteLine($"  address:       {addressCount} (expected: 1)");
        Console.WriteLine($"  communication: {communicationCount} (expected: 1)");
        Console.WriteLine($"  annotation:    {annotationCount} (expected: 1)");
        Console.WriteLine($"  client_image:  {clientImageCount} (expected: 1)");

        clientCount.Should().Be(1, "client table should have 1 entry");
        membershipCount.Should().Be(1, "membership table should have 1 entry");
        addressCount.Should().Be(1, "address table should have 1 entry");
        communicationCount.Should().Be(1, "communication table should have 1 entry");
        annotationCount.Should().Be(1, "annotation table should have 1 entry");
        clientImageCount.Should().Be(1, "client_image table should have 1 entry");

        Console.WriteLine("\n=== ALL DATABASE TABLES VERIFIED ===");
    }
}
