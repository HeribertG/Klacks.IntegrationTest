// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net.Http.Json;
using Klacks.Api.Application.DTOs.Schedules.HolisticHarmonizer;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Behavioral LLM smoke test for Wizard 3 (Holistic Harmonizer). Seeds a small fragmented plan
/// (4 clients, 3 shifts, scattered Work rows on the main schedule, AnalyseToken=null), then drives
/// the REAL holistic harmonizer end-to-end via POST /HolisticHarmonizer/Start + SignalR OnCompleted,
/// with the real LLM-backed IPlanProposalProvider.
///
/// This is the only NON-deterministic wizard test: the holistic stage asks a configured vision LLM
/// for plan mutations, so the exact result varies per run and cannot be regression-asserted. The
/// smoke therefore only checks invariants that hold for ANY run: the job COMPLETES (OnCompleted, not
/// OnFailed/OnCancelled) and is NO-REGRESSION (FitnessAfter >= FitnessBefore — the engine's
/// score-greedy acceptance guarantees the working bitmap is monotonic non-decreasing).
///
/// It is [Explicit] + [Category("Llm")] + [Category("RealDatabase")]: it makes real LLM API calls
/// (provider keys live in the Dev DB 5434 llm_providers), costs money, needs network, and is slow
/// (120s job budget + LLM latency). If no usable LLM provider is configured the engine fails the
/// pre-flight ping; the test then Assert.Ignores instead of failing obscurely.
/// </summary>
[TestFixture]
[Explicit("Wizard-3 holistic harmonizer LLM smoke — makes real LLM calls against a configured provider in the Dev DB (5434); non-deterministic, costs money, local-only")]
[Category("Llm")]
[Category("RealDatabase")]
public class HolisticHarmonizerLlmSmokeTests
{
    private const string HubPath = "hubs/holistic-harmonizer";

    private static readonly DateOnly PeriodFrom = new(2099, 1, 5);
    private static readonly DateOnly PeriodUntil = new(2099, 1, 18);
    private const int ClientCount = 4;

    private static readonly TimeOnly[] ShiftStart = { new(7, 0), new(15, 0), new(23, 0) };
    private static readonly TimeOnly[] ShiftEnd = { new(15, 0), new(23, 0), new(7, 0) };

    private SignalRTestWebApplicationFactory _factory = null!;
    private HttpClient _httpClient = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    private readonly List<Guid> _clientIds = new();
    private readonly List<Guid> _shiftIds = new();
    private Guid _contractId;

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
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        await SeedFragmentedPlanAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        _context?.Dispose();
    }

    [Test]
    public async Task HolisticHarmonizer_OnSeededPlan_CompletesAndDoesNotRegress()
    {
        var userId = Guid.NewGuid().ToString();
        var token = GenerateTestToken(userId);
        _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var startResponse = await _httpClient.PostAsJsonAsync("/api/backend/HolisticHarmonizer/Start",
            new HolisticHarmonizerRunRequest(
                PeriodFrom: PeriodFrom,
                PeriodUntil: PeriodUntil,
                AgentIds: _clientIds,
                AnalyseToken: null,
                Language: "en"));

        startResponse.EnsureSuccessStatusCode();
        var startBody = await startResponse.Content.ReadFromJsonAsync<StartHolisticHarmonizerResponse>();
        startBody.ShouldNotBeNull();
        startBody!.JobId.ShouldNotBe(Guid.Empty);

        var outcome = await WaitForOutcomeAsync(token, startBody.JobId, TimeSpan.FromMinutes(4));

        if (outcome.Failed is { } reason && LooksLikeMissingLlm(reason))
        {
            Assert.Ignore($"No usable LLM provider configured (holistic pre-flight failed): {reason}");
        }

        outcome.Cancelled.ShouldBeFalse("holistic job was cancelled / hit its time budget before completing");
        outcome.Failed.ShouldBeNull($"holistic job failed: {outcome.Failed}");
        outcome.Completed.ShouldNotBeNull("holistic job produced no OnCompleted result within the timeout");

        // Non-deterministic-safe invariant: score-greedy acceptance means the plan never gets worse.
        outcome.Completed!.FitnessAfter.ShouldBeGreaterThanOrEqualTo(
            outcome.Completed.FitnessBefore - 1e-6,
            "holistic harmonizer must never regress the plan (score-greedy guarantees monotonic fitness)");

        TestContext.Out.WriteLine(
            $"Holistic smoke: fitnessBefore={outcome.Completed.FitnessBefore:F4} "
            + $"fitnessAfter={outcome.Completed.FitnessAfter:F4} batches={outcome.Completed.Batches.Count}");
    }

    private sealed class HolisticOutcome
    {
        public HolisticHarmonizerRunResponse? Completed { get; set; }
        public string? Failed { get; set; }
        public bool Cancelled { get; set; }
    }

    private async Task<HolisticOutcome> WaitForOutcomeAsync(string token, Guid jobId, TimeSpan timeout)
    {
        var hubUrl = $"{_factory.Server.BaseAddress}{HubPath}?access_token={Uri.EscapeDataString(token)}";
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.SkipNegotiation = false;
            })
            .Build();

        var outcome = new HolisticOutcome();
        var tcs = new TaskCompletionSource<bool>();

        connection.On<HolisticHarmonizerRunResponse>("OnCompleted", r =>
        {
            outcome.Completed = r;
            tcs.TrySetResult(true);
        });
        connection.On<string>("OnFailed", reason =>
        {
            outcome.Failed = reason;
            tcs.TrySetResult(true);
        });
        connection.On("OnCancelled", () =>
        {
            outcome.Cancelled = true;
            tcs.TrySetResult(true);
        });

        await connection.StartAsync();
        await connection.InvokeAsync("JoinJob", jobId);

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout));

        await connection.DisposeAsync();

        return outcome;
    }

    private static bool LooksLikeMissingLlm(string reason)
    {
        var r = reason.ToLowerInvariant();
        return r.Contains("pre-flight")
            || r.Contains("preflight")
            || r.Contains("model offline")
            || r.Contains("no model")
            || r.Contains("api key")
            || r.Contains("provider")
            || r.Contains("unhealthy")
            || r.Contains("not configured");
    }

    private async Task SeedFragmentedPlanAsync()
    {
        _contractId = Guid.NewGuid();
        var contract = new Contract
        {
            Id = _contractId,
            Name = "TEST_Holistic_Contract",
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            ValidUntil = null,
            GuaranteedHours = 80,
            MaximumHours = 0,
            FullTime = 40,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
            PerformsShiftWork = true,
            IsDeleted = false,
        };
        _context.Contract.Add(contract);

        for (var i = 0; i < ClientCount; i++)
        {
            var clientId = Guid.NewGuid();
            _clientIds.Add(clientId);
            _context.Client.Add(new Client
            {
                Id = clientId,
                Name = $"TEST_Holistic_{i}",
                FirstName = "Smoke",
                IsDeleted = false,
            });
            _context.ClientContract.Add(new ClientContract
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ContractId = _contractId,
                FromDate = new DateOnly(2090, 1, 1),
                UntilDate = null,
                IsActive = true,
            });
        }

        for (var s = 0; s < ShiftStart.Length; s++)
        {
            var shiftId = Guid.NewGuid();
            _shiftIds.Add(shiftId);
            _context.Shift.Add(new Shift
            {
                Id = shiftId,
                Name = $"TEST_Holistic_Shift_{s}",
                Abbreviation = $"H{s}",
                StartShift = ShiftStart[s],
                EndShift = ShiftEnd[s],
                WorkTime = 8,
                FromDate = new DateOnly(2090, 1, 1),
                UntilDate = null,
                IsMonday = true,
                IsTuesday = true,
                IsWednesday = true,
                IsThursday = true,
                IsFriday = true,
                IsSaturday = true,
                IsSunday = true,
                Quantity = 1,
                CuttingAfterMidnight = s == 2,
                IsDeleted = false,
            });
        }

        // Fragmented plan on the main schedule: each client works scattered days (gaps to consolidate),
        // rotating across the three shift types so the bitmap is non-trivial for the harmonizer.
        var periodDays = PeriodUntil.DayNumber - PeriodFrom.DayNumber + 1;
        for (var i = 0; i < ClientCount; i++)
        {
            for (var d = i; d < periodDays; d += 2)
            {
                var date = PeriodFrom.AddDays(d);
                var s = (d + i) % ShiftStart.Length;
                _context.Work.Add(new Work
                {
                    Id = Guid.NewGuid(),
                    ClientId = _clientIds[i],
                    ShiftId = _shiftIds[s],
                    CurrentDate = date,
                    StartTime = ShiftStart[s],
                    EndTime = ShiftEnd[s],
                    WorkTime = 8,
                    Surcharges = 0,
                    LockLevel = WorkLockLevel.None,
                    AnalyseToken = null,
                    IsDeleted = false,
                });
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task CleanupAsync()
    {
        foreach (var clientId in _clientIds)
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM work WHERE client_id = {0}", clientId);
        }
        foreach (var shiftId in _shiftIds)
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM shift WHERE id = {0}", shiftId);
        }
        foreach (var clientId in _clientIds)
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM client_contract WHERE client_id = {0}", clientId);
        }
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM contract WHERE id = {0}", _contractId);
        foreach (var clientId in _clientIds)
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM client WHERE id = {0}", clientId);
        }
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

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: SignalRTestWebApplicationFactory.JWT_ISSUER,
            audience: SignalRTestWebApplicationFactory.JWT_AUDIENCE,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
