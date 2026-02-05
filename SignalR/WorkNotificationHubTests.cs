using FluentAssertions;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Presentation.DTOs.Notifications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace Klacks.IntegrationTest.SignalR;

public class SignalRTestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JWT_SECRET = "tqXc2HF1RDsi/N1LMkGIVrgFSVuJ9PBmFg/QrgzqlfQ=";
    public const string JWT_ISSUER = "https://localhost:44371";
    public const string JWT_AUDIENCE = "https://localhost:44371";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            });

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JWT_SECRET)),
                    ValidateIssuer = true,
                    ValidIssuer = JWT_ISSUER,
                    ValidateAudience = true,
                    ValidAudience = JWT_AUDIENCE,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(5)
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });
        });
    }
}

[TestFixture]
public class WorkNotificationHubTests
{
    private SignalRTestWebApplicationFactory _factory = null!;
    private HttpClient _httpClient = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    private Guid _testClientId;
    private Guid _testShiftId;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        _factory = new SignalRTestWebApplicationFactory();
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

    private static string GenerateTestToken(string userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Email, $"test{userId}@test.com"),
            new(ClaimTypes.Name, $"TestUser{userId}"),
            new(ClaimTypes.GivenName, "Test"),
            new(ClaimTypes.Surname, "User"),
            new("jti", Guid.NewGuid().ToString()),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SignalRTestWebApplicationFactory.JWT_SECRET));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: SignalRTestWebApplicationFactory.JWT_ISSUER,
            audience: SignalRTestWebApplicationFactory.JWT_AUDIENCE,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HubConnection CreateHubConnection(string token)
    {
        var hubUrl = $"{_factory.Server.BaseAddress}hubs/work-notifications?access_token={Uri.EscapeDataString(token)}";

        return new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.SkipNegotiation = false;
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

        var periodStart = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var startDateStr = periodStart.ToString("yyyy-MM-dd");
        var endDateStr = periodEnd.ToString("yyyy-MM-dd");

        await connection1.InvokeAsync("JoinScheduleGroup", startDateStr, endDateStr);
        await connection2.InvokeAsync("JoinScheduleGroup", startDateStr, endDateStr);

        // Act
        var workRequest = new
        {
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WorkTime = 8,
            PeriodStart = startDateStr,
            PeriodEnd = endDateStr
        };

        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token1}");
        _httpClient.DefaultRequestHeaders.Remove("X-SignalR-ConnectionId");
        _httpClient.DefaultRequestHeaders.Add("X-SignalR-ConnectionId", connectionId1);

        var response = await _httpClient.PostAsJsonAsync("/api/backend/Works", workRequest);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        TestContext.WriteLine($"Response: {responseContent}");
        TestContext.WriteLine($"Expected group: schedule_{startDateStr}_{endDateStr}");

        await Task.Delay(2000);

        // Assert
        receivedByConnection1.Should().BeNull("Sender should not receive their own notification");
        receivedByConnection2.Should().NotBeNull($"Other clients should receive the notification. Response was: {responseContent}");
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
            CurrentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WorkTime = 8,
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

        var periodStart = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var startDateStr = periodStart.ToString("yyyy-MM-dd");
        var endDateStr = periodEnd.ToString("yyyy-MM-dd");

        await connection1.InvokeAsync("JoinScheduleGroup", startDateStr, endDateStr);
        await connection2.InvokeAsync("JoinScheduleGroup", startDateStr, endDateStr);

        // Act
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token1}");
        _httpClient.DefaultRequestHeaders.Remove("X-SignalR-ConnectionId");
        _httpClient.DefaultRequestHeaders.Add("X-SignalR-ConnectionId", connectionId1);

        var response = await _httpClient.DeleteAsync($"/api/backend/Works/{workId}?periodStart={startDateStr}&periodEnd={endDateStr}");
        response.EnsureSuccessStatusCode();

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

        var receivedNotifications = new ConcurrentBag<(string ConnectionName, WorkNotificationDto Notification)>();

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

        var periodStart = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);
        var startDateStr = periodStart.ToString("yyyy-MM-dd");
        var endDateStr = periodEnd.ToString("yyyy-MM-dd");

        await connection1.InvokeAsync("JoinScheduleGroup", startDateStr, endDateStr);
        await connection2.InvokeAsync("JoinScheduleGroup", startDateStr, endDateStr);
        await connection3.InvokeAsync("JoinScheduleGroup", startDateStr, endDateStr);

        // Act
        var workRequest = new
        {
            ClientId = _testClientId,
            ShiftId = _testShiftId,
            CurrentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            WorkTime = 8,
            PeriodStart = startDateStr,
            PeriodEnd = endDateStr
        };

        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token1}");
        _httpClient.DefaultRequestHeaders.Remove("X-SignalR-ConnectionId");
        _httpClient.DefaultRequestHeaders.Add("X-SignalR-ConnectionId", connectionId1);

        var response = await _httpClient.PostAsJsonAsync("/api/backend/Works", workRequest);
        response.EnsureSuccessStatusCode();

        await Task.Delay(1000);

        // Assert
        receivedNotifications.Should().HaveCount(2, "Two other clients should receive notification");
        receivedNotifications.Should().NotContain(n => n.ConnectionName == "connection1",
            "Sender should not receive notification");
        receivedNotifications.Should().Contain(n => n.ConnectionName == "connection2");
        receivedNotifications.Should().Contain(n => n.ConnectionName == "connection3");
    }
}
