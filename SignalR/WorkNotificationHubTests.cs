using FluentAssertions;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Presentation.DTOs.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NUnit.Framework;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace IntegrationTest.SignalR;

[TestFixture]
public class WorkNotificationHubTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _httpClient = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    private Guid _testClientId;
    private Guid _testShiftId;

    private const string TEST_JWT_SECRET = "ThisIsAVeryLongSecretKeyForTestingPurposesOnly12345";
    private const string TEST_JWT_ISSUER = "TestIssuer";
    private const string TEST_JWT_AUDIENCE = "TestAudience";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks1;Username=postgres;Password=admin";

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureServices(services =>
                {
                    var jwtSettings = new JwtSettings
                    {
                        Secret = TEST_JWT_SECRET,
                        ValidIssuer = TEST_JWT_ISSUER,
                        ValidAudience = TEST_JWT_AUDIENCE
                    };
                    services.AddSingleton(jwtSettings);
                });
            });

        _httpClient = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _httpClient?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        await SetupTestData();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestData();
        _context?.Dispose();
    }

    private async Task SetupTestData()
    {
        // Arrange
        _testClientId = Guid.NewGuid();
        _testShiftId = Guid.NewGuid();

        var client = new Client
        {
            Id = _testClientId,
            Name = "TEST_SignalR",
            FirstName = "Integration",
            IsDeleted = false
        };
        _context.Client.Add(client);

        var shift = new Shift
        {
            Id = _testShiftId,
            Name = "TEST_SignalR_Shift",
            Abbreviation = "TSS",
            StartShift = new TimeOnly(8, 0, 0),
            EndShift = new TimeOnly(16, 0, 0),
            WorkTime = 8,
            IsDeleted = false
        };
        _context.Shift.Add(shift);

        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestData()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE client_id = {0}", _testClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0}", _testShiftId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id = {0}", _testClientId);
    }

    private string GenerateTestToken(string userId)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, $"test{userId}@test.com"),
            new Claim(ClaimTypes.Name, $"TestUser{userId}"),
            new Claim(ClaimTypes.GivenName, "Test"),
            new Claim(ClaimTypes.Surname, "User"),
            new Claim("jti", Guid.NewGuid().ToString()),
            new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TEST_JWT_SECRET));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature);

        var token = new JwtSecurityToken(
            issuer: TEST_JWT_ISSUER,
            audience: TEST_JWT_AUDIENCE,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            notBefore: DateTime.UtcNow,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HubConnection CreateHubConnection(string token)
    {
        var hubUrl = $"{_factory.Server.BaseAddress}hubs/work-notifications";

        return new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();
    }

    [Test]
    public async Task WorkCreated_SendsNotificationToOtherClients_NotToSender()
    {
        // Arrange
        var token1 = GenerateTestToken("user1");
        var token2 = GenerateTestToken("user2");

        await using var connection1 = CreateHubConnection(token1);
        await using var connection2 = CreateHubConnection(token2);

        WorkNotificationDto? receivedByConnection1 = null;
        WorkNotificationDto? receivedByConnection2 = null;

        connection1.On<WorkNotificationDto>("WorkCreated", notification =>
        {
            receivedByConnection1 = notification;
        });

        connection2.On<WorkNotificationDto>("WorkCreated", notification =>
        {
            receivedByConnection2 = notification;
        });

        await connection1.StartAsync();
        await connection2.StartAsync();

        var connectionId1 = await connection1.InvokeAsync<string>("GetConnectionId");

        // Act
        var workRequest = new
        {
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = DateTime.UtcNow.Date,
            WorkTime = 8
        };

        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token1}");
        _httpClient.DefaultRequestHeaders.Remove("X-SignalR-ConnectionId");
        _httpClient.DefaultRequestHeaders.Add("X-SignalR-ConnectionId", connectionId1);

        var response = await _httpClient.PostAsJsonAsync("/api/v1/works", workRequest);

        await Task.Delay(1000);

        // Assert
        receivedByConnection1.Should().BeNull("Sender should not receive their own notification");
        receivedByConnection2.Should().NotBeNull("Other clients should receive the notification");
        receivedByConnection2!.ClientId.Should().Be(_testClientId);
        receivedByConnection2.ShiftId.Should().Be(_testShiftId);
        receivedByConnection2.OperationType.Should().Be("created");
    }

    [Test]
    public async Task WorkDeleted_SendsNotificationToOtherClients()
    {
        // Arrange
        var workId = Guid.NewGuid();
        var work = new Work
        {
            Id = workId,
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = DateTime.UtcNow.Date,
            WorkTime = 8,
            IsSealed = false,
            IsDeleted = false
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();

        var token1 = GenerateTestToken("user1");
        var token2 = GenerateTestToken("user2");

        await using var connection1 = CreateHubConnection(token1);
        await using var connection2 = CreateHubConnection(token2);

        WorkNotificationDto? receivedByConnection2 = null;

        connection2.On<WorkNotificationDto>("WorkDeleted", notification =>
        {
            receivedByConnection2 = notification;
        });

        await connection1.StartAsync();
        await connection2.StartAsync();

        var connectionId1 = await connection1.InvokeAsync<string>("GetConnectionId");

        // Act
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token1}");
        _httpClient.DefaultRequestHeaders.Remove("X-SignalR-ConnectionId");
        _httpClient.DefaultRequestHeaders.Add("X-SignalR-ConnectionId", connectionId1);

        var response = await _httpClient.DeleteAsync($"/api/v1/works/{workId}");

        await Task.Delay(1000);

        // Assert
        receivedByConnection2.Should().NotBeNull("Other clients should receive delete notification");
        receivedByConnection2!.WorkId.Should().Be(workId);
        receivedByConnection2.OperationType.Should().Be("deleted");
    }

    [Test]
    public async Task MultipleClients_AllReceiveNotificationExceptSender()
    {
        // Arrange
        var token1 = GenerateTestToken("user1");
        var token2 = GenerateTestToken("user2");
        var token3 = GenerateTestToken("user3");

        await using var connection1 = CreateHubConnection(token1);
        await using var connection2 = CreateHubConnection(token2);
        await using var connection3 = CreateHubConnection(token3);

        var receivedNotifications = new List<(string ConnectionName, WorkNotificationDto Notification)>();

        connection1.On<WorkNotificationDto>("WorkCreated", notification =>
        {
            receivedNotifications.Add(("connection1", notification));
        });

        connection2.On<WorkNotificationDto>("WorkCreated", notification =>
        {
            receivedNotifications.Add(("connection2", notification));
        });

        connection3.On<WorkNotificationDto>("WorkCreated", notification =>
        {
            receivedNotifications.Add(("connection3", notification));
        });

        await connection1.StartAsync();
        await connection2.StartAsync();
        await connection3.StartAsync();

        var connectionId1 = await connection1.InvokeAsync<string>("GetConnectionId");

        // Act
        var workRequest = new
        {
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = DateTime.UtcNow.Date,
            WorkTime = 8
        };

        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token1}");
        _httpClient.DefaultRequestHeaders.Remove("X-SignalR-ConnectionId");
        _httpClient.DefaultRequestHeaders.Add("X-SignalR-ConnectionId", connectionId1);

        await _httpClient.PostAsJsonAsync("/api/v1/works", workRequest);

        await Task.Delay(1000);

        // Assert
        receivedNotifications.Should().HaveCount(2, "Two other clients should receive notification");
        receivedNotifications.Should().NotContain(n => n.ConnectionName == "connection1",
            "Sender should not receive notification");
        receivedNotifications.Should().Contain(n => n.ConnectionName == "connection2");
        receivedNotifications.Should().Contain(n => n.ConnectionName == "connection3");
    }
}
