// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration test for the propose_plan guardrail seam: the REAL CloneScenarioDataAsync tags cloned
/// works with the scenario token, and the REAL PreCommitConflictChecker (querying AnalyseToken == token)
/// must SEE those cloned works so a colliding scenario placement is blocked. This is the un-mocked
/// clone<->checker interaction that the unit tests cannot cover; without it, the checker could query an
/// empty token world and falsely pass (the silent-empty risk). Only the policy resolver is stubbed —
/// collision detection does not depend on the policy thresholds.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class ProposePlanGuardrailSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PROPOSESEAM_";
    private static readonly DateOnly PeriodFrom = new(2026, 6, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 6, 30);
    private static readonly DateOnly WorkDate = new(2026, 6, 5);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _cloneService = null!;
    private PreCommitConflictChecker _checker = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        _context = NewContext();
        _cloneService = new AnalyseScenarioService(_context);

        var timeline = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            Substitute.For<ILogger<TimelineCalculationService>>());

        var resolver = Substitute.For<ISchedulingPolicyResolver>();
        resolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new SchedulingPolicy(
                TimeSpan.FromHours(11), TimeSpan.FromHours(10), 6, TimeSpan.FromHours(50), 2));

        var enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);

        var settingsReader = Substitute.For<ISettingsReader>();

        var periodCapEvaluator = Substitute.For<IPeriodCapEvaluator>();
        periodCapEvaluator
            .EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, decimal Hours)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var restDayRotationEvaluator = Substitute.For<IRestDayRotationEvaluator>();
        restDayRotationEvaluator
            .EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var counterRuleEvaluator = Substitute.For<ICounterRuleEvaluator>();
        counterRuleEvaluator
            .EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var restrictedTimeWindowEvaluator = new RestrictedTimeWindowEvaluator(
            new Klacks.Api.Infrastructure.Repositories.Scheduling.RestrictedTimeWindowRuleRepository(_context),
            _context,
            enforcementResolver);

        _checker = new PreCommitConflictChecker(_context, timeline, resolver, new Klacks.Api.Application.Services.Schedules.ComplianceEscalationService(enforcementResolver), settingsReader, periodCapEvaluator, restDayRotationEvaluator, counterRuleEvaluator, restrictedTimeWindowEvaluator,
            NonReportingCompensatoryRestEvaluator());
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync()
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Shift> CreateShiftAsync()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TST",
            Description = "Propose-plan seam test",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            AnalyseToken = null,
            ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    private async Task CreateRealWorkAsync(Guid clientId, Guid shiftId)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = null
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Checker_SeesClonedWork_BlocksCollidingScenarioPlacement()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();
        await CreateRealWorkAsync(client.Id, shift.Id);

        var token = Guid.NewGuid();
        await _cloneService.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Overlaps the cloned 08:00-16:00 work on the same day -> collision, but ONLY if the checker
        // actually sees the token-scoped clone.
        var colliding = new PlannedWorkRow(client.Id, WorkDate, new TimeOnly(12, 0), new TimeOnly(20, 0), shift.Id);
        var result = await _checker.CheckAsync(new[] { colliding }, token, CancellationToken.None);

        result.HasBlocking.ShouldBeTrue();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.collision" && c.Type == ScheduleValidationType.Error);
    }

    [Test]
    public async Task Checker_WithWrongToken_SeesEmptyWorld_NoCollision()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();
        await CreateRealWorkAsync(client.Id, shift.Id);

        var token = Guid.NewGuid();
        await _cloneService.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        // A different token addresses a scenario world that has no cloned work -> nothing to collide with.
        var otherToken = Guid.NewGuid();
        var colliding = new PlannedWorkRow(client.Id, WorkDate, new TimeOnly(12, 0), new TimeOnly(20, 0), shift.Id);
        var result = await _checker.CheckAsync(new[] { colliding }, otherToken, CancellationToken.None);

        result.HasBlocking.ShouldBeFalse();
    }

    /// <summary>
    /// K12 reports persisted obligations, which these tests never seed - an evaluator reporting
    /// nothing keeps them about the rule they were written for. CompensatoryRestEvaluatorTests and
    /// the pre-commit K12 test cover the wiring itself.
    /// </summary>
    private static ICompensatoryRestEvaluator NonReportingCompensatoryRestEvaluator()
    {
        var evaluator = Substitute.For<ICompensatoryRestEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        return evaluator;
    }
}
