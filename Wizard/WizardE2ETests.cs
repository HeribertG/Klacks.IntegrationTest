// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net.Http.Json;
using FluentAssertions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// End-to-end test for the wizard pipeline: Start → SignalR progress → OnCompleted → Apply → Work rows in DB.
/// Uses the real Postgres test database (port 5434) and the in-process Klacks.Api via WebApplicationFactory.
/// Re-uses <see cref="SignalRTestWebApplicationFactory"/> from the SignalR test suite for JWT + hub routing.
/// </summary>
[TestFixture]
public class WizardE2ETests
{
    private SignalRTestWebApplicationFactory _factory = null!;
    private HttpClient _httpClient = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    private Guid _testClientId;
    private Guid _testShiftId;
    private Guid _testContractId;

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

        await SetupFixtureAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupFixtureAsync();
        _context?.Dispose();
    }

    [Test]
    public async Task WizardPipeline_StartCompletesAndApplyCreatesWork()
    {
        var userId = Guid.NewGuid().ToString();
        var token = GenerateTestToken(userId);
        _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var periodFrom = new DateOnly(2099, 1, 5);
        var periodUntil = new DateOnly(2099, 1, 7);

        var startResponse = await _httpClient.PostAsJsonAsync("/api/backend/Wizard/Start", new
        {
            periodFrom,
            periodUntil,
            agentIds = new[] { _testClientId },
            shiftIds = new[] { _testShiftId },
            analyseToken = (Guid?)null,
        });

        startResponse.EnsureSuccessStatusCode();
        var startBody = await startResponse.Content.ReadFromJsonAsync<StartResponse>();
        startBody.Should().NotBeNull();
        startBody!.JobId.Should().NotBe(Guid.Empty);

        var completion = await WaitForCompletionAsync(token, startBody.JobId, TimeSpan.FromSeconds(60));
        completion.Should().NotBeNull("wizard job did not finish within the timeout");
        completion!.JobId.Should().Be(startBody.JobId);
        completion.TokenCount.Should().BeGreaterThan(0, "GA must produce at least one token");

        var applyResponse = await _httpClient.PostAsJsonAsync("/api/backend/Wizard/Apply", new
        {
            jobId = startBody.JobId,
        });

        applyResponse.EnsureSuccessStatusCode();
        var applyBody = await applyResponse.Content.ReadFromJsonAsync<ApplyResponse>();
        applyBody.Should().NotBeNull();
        applyBody!.CreatedWorkIds.Should().NotBeEmpty();

        var createdWorks = await _context.Work
            .AsNoTracking()
            .Where(w => w.ClientId == _testClientId
                        && w.CurrentDate >= periodFrom
                        && w.CurrentDate <= periodUntil)
            .ToListAsync();

        createdWorks.Should().HaveCountGreaterThanOrEqualTo(1, "apply must persist at least one work row");
        createdWorks.Should().OnlyContain(w => w.ShiftId == _testShiftId);
    }

    [Test]
    public async Task WizardPipeline_CancelStopsRunningJob()
    {
        var userId = Guid.NewGuid().ToString();
        var token = GenerateTestToken(userId);
        _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var startResponse = await _httpClient.PostAsJsonAsync("/api/backend/Wizard/Start", new
        {
            periodFrom = new DateOnly(2099, 1, 5),
            periodUntil = new DateOnly(2099, 1, 7),
            agentIds = new[] { _testClientId },
            shiftIds = new[] { _testShiftId },
            analyseToken = (Guid?)null,
        });
        startResponse.EnsureSuccessStatusCode();
        var startBody = await startResponse.Content.ReadFromJsonAsync<StartResponse>();

        var cancelResponse = await _httpClient.PostAsJsonAsync("/api/backend/Wizard/Cancel", new
        {
            jobId = startBody!.JobId,
        });
        cancelResponse.EnsureSuccessStatusCode();
        var cancelBody = await cancelResponse.Content.ReadFromJsonAsync<CancelResponse>();
        cancelBody!.Cancelled.Should().BeTrue();
    }

    [Test]
    public async Task ApplyUnknownJob_ReturnsNotFound()
    {
        var userId = Guid.NewGuid().ToString();
        var token = GenerateTestToken(userId);
        _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await _httpClient.PostAsJsonAsync("/api/backend/Wizard/Apply", new
        {
            jobId = Guid.NewGuid(),
        });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Test]
    public async Task WizardContextBuilder_MinimalFixture_ProducesFeasibleContext()
    {
        using var scope = _factory.Services.CreateScope();
        var builder = scope.ServiceProvider
            .GetRequiredService<Klacks.Api.Application.Services.Schedules.IWizardContextBuilder>();

        var request = new Klacks.Api.Application.Services.Schedules.WizardContextRequest(
            PeriodFrom: new DateOnly(2099, 1, 5),
            PeriodUntil: new DateOnly(2099, 1, 7),
            AgentIds: new[] { _testClientId },
            ShiftIds: new[] { _testShiftId },
            AnalyseToken: null);

        var context = await builder.BuildContextAsync(request, CancellationToken.None);

        TestContext.Out.WriteLine($"Agents: {context.Agents.Count}");
        foreach (var a in context.Agents)
        {
            TestContext.Out.WriteLine($"  Agent {a.Id}: FullTime={a.FullTime} PerformsShiftWork={a.PerformsShiftWork} MaxDailyHours={a.MaxDailyHours} MinRestHours={a.MinRestHours} Mo={a.WorkOnMonday} Di={a.WorkOnTuesday} Mi={a.WorkOnWednesday}");
        }

        TestContext.Out.WriteLine($"Shifts: {context.Shifts.Count}");
        foreach (var s in context.Shifts)
        {
            TestContext.Out.WriteLine($"  Shift {s.Id}: date={s.Date} {s.StartTime}-{s.EndTime} hours={s.Hours}");
        }

        TestContext.Out.WriteLine($"ContractDays: {context.ContractDays.Count}");
        foreach (var d in context.ContractDays)
        {
            TestContext.Out.WriteLine($"  Day {d.Date}: WorksOnDay={d.WorksOnDay} PerformsShiftWork={d.PerformsShiftWork} MaximumHoursPerDay={d.MaximumHoursPerDay}");
        }

        TestContext.Out.WriteLine($"SchedulingConstants: MaxConsec={context.SchedulingMaxConsecutiveDays} MinPause={context.SchedulingMinPauseHours} MaxDaily={context.SchedulingMaxDailyHours} MaxWeekly={context.SchedulingMaxWeeklyHours}");
        TestContext.Out.WriteLine($"Commands: {context.ScheduleCommands.Count}, Breaks: {context.BreakBlockers.Count}, Locked: {context.LockedWorks.Count}, Preferences: {context.ShiftPreferences.Count}");

        var config = new Klacks.ScheduleOptimizer.TokenEvolution.TokenEvolutionConfig
        {
            PopulationSize = 20,
            MaxGenerations = 100,
            EarlyStopNoImprovementGenerations = 20,
            RandomSeed = 42,
        };

        var best = Klacks.ScheduleOptimizer.TokenEvolution.TokenEvolutionLoop.Create().Run(context, config);
        var violations = new Klacks.ScheduleOptimizer.TokenEvolution.Constraints.TokenConstraintChecker()
            .Check(best, context);

        TestContext.Out.WriteLine($"Final Stage 0 = {best.FitnessStage0}, Tokens = {best.Tokens.Count}");
        foreach (var v in violations)
        {
            TestContext.Out.WriteLine($"  [{v.Kind}] agent={v.AgentId} date={v.Date} desc={v.Description}");
        }

        context.Agents.Should().HaveCount(1, "fixture has exactly one test client");
        context.ContractDays.Should().HaveCount(3, "period covers 3 days");
    }

    private async Task<CompletionPayload?> WaitForCompletionAsync(string token, Guid jobId, TimeSpan timeout)
    {
        var hubUrl = $"{_factory.Server.BaseAddress}hubs/wizard?access_token={Uri.EscapeDataString(token)}";
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.SkipNegotiation = false;
            })
            .Build();

        var tcs = new TaskCompletionSource<CompletionPayload?>();

        connection.On<CompletionPayload>("OnCompleted", p => tcs.TrySetResult(p));
        connection.On<string>("OnFailed", _ => tcs.TrySetResult(null));
        connection.On("OnCancelled", () => tcs.TrySetResult(null));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinJob", jobId);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));

        await connection.DisposeAsync();

        return completed == tcs.Task ? await tcs.Task : null;
    }

    private async Task SetupFixtureAsync()
    {
        _testClientId = Guid.NewGuid();
        _testShiftId = Guid.NewGuid();
        _testContractId = Guid.NewGuid();

        var client = new Client
        {
            Id = _testClientId,
            Name = "TEST_Wizard",
            FirstName = "Integration",
            IsDeleted = false,
        };
        _context.Client.Add(client);

        var contract = new Contract
        {
            Id = _testContractId,
            Name = "TEST_Wizard_Contract",
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            ValidUntil = null,
            GuaranteedHours = 16,
            MaximumHours = 0,
            FullTime = 40,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            PerformsShiftWork = true,
            IsDeleted = false,
        };
        _context.Contract.Add(contract);

        var clientContract = new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _testClientId,
            ContractId = _testContractId,
            FromDate = new DateOnly(2090, 1, 1),
            UntilDate = null,
            IsActive = true,
        };
        _context.ClientContract.Add(clientContract);

        var shift = new Shift
        {
            Id = _testShiftId,
            Name = "TEST_Wizard_Shift",
            Abbreviation = "TWS",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            WorkTime = 8,
            FromDate = new DateOnly(2090, 1, 1),
            UntilDate = null,
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            Quantity = 1,
            IsDeleted = false,
        };
        _context.Shift.Add(shift);

        await _context.SaveChangesAsync();
    }

    private async Task CleanupFixtureAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE client_id = {0}", _testClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0}", _testShiftId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_contract WHERE client_id = {0}", _testClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM contract WHERE id = {0}", _testContractId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id = {0}", _testClientId);
    }

    private static string GenerateTestToken(string userId)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId),
            new(System.Security.Claims.ClaimTypes.Email, $"test{userId}@test.com"),
            new(System.Security.Claims.ClaimTypes.Name, $"TestUser{userId}"),
            new("jti", Guid.NewGuid().ToString()),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), System.Security.Claims.ClaimValueTypes.Integer64),
        };

        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(SignalRTestWebApplicationFactory.JWT_SECRET));
        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: SignalRTestWebApplicationFactory.JWT_ISSUER,
            audience: SignalRTestWebApplicationFactory.JWT_AUDIENCE,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record StartResponse(Guid JobId);

    private sealed record CancelResponse(bool Cancelled);

    private sealed record ApplyResponse(IReadOnlyList<Guid> CreatedWorkIds);

    private sealed record CompletionPayload(
        Guid JobId,
        int FinalHardViolations,
        double FinalStage1Completion,
        int TokenCount);
}
