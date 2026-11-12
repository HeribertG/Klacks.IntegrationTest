// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration test for the lead and trail of a planned work row against the REAL PreCommitConflictChecker on Npgsql: the
/// briefing and debriefing it maps to must make a neighbouring work that only the lead or the trail touches collide, while
/// the same row without lead and trail does not. Only the policy resolver is stubbed.
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
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
[Category("RealDatabase")]
public class PlannedPaddingCheckerTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PADDING_";
    private static readonly DateOnly WorkDate = new(2026, 6, 5);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private PreCommitConflictChecker _checker = null!;
    private IPeriodCapEvaluator _periodCapEvaluator = null!;

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

        var timeline = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            Substitute.For<ILogger<TimelineCalculationService>>());

        var resolver = Substitute.For<ISchedulingPolicyResolver>();
        var policy = new SchedulingPolicy(
            TimeSpan.FromHours(11), TimeSpan.FromHours(10), 6, TimeSpan.FromHours(50), 2);
        resolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(policy);
        resolver.GetForClientsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>())
            .Returns(call => (IReadOnlyDictionary<Guid, SchedulingPolicy>)call.Arg<IReadOnlyList<Guid>>()
                .ToDictionary(clientId => clientId, _ => policy));

        var enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);

        var settingsReader = Substitute.For<ISettingsReader>();

        var periodCapEvaluator = _periodCapEvaluator = Substitute.For<IPeriodCapEvaluator>();
        periodCapEvaluator
            .EvaluatePlannedForClientsAsync(Arg.Any<IReadOnlyList<ClientPlannedHours>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var restDayRotationEvaluator = Substitute.For<IRestDayRotationEvaluator>();
        restDayRotationEvaluator
            .EvaluatePlannedRowsAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var counterRuleEvaluator = Substitute.For<ICounterRuleEvaluator>();
        counterRuleEvaluator
            .EvaluatePlannedRowsAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var restrictedTimeWindowEvaluator = new RestrictedTimeWindowEvaluator(
            new Klacks.Api.Infrastructure.Repositories.Scheduling.RestrictedTimeWindowRuleRepository(_context),
            _context,
            enforcementResolver);

        var holidayWorkEvaluator = Substitute.For<IHolidayWorkEvaluator>();
        holidayWorkEvaluator
            .EvaluateForClientsAsync(Arg.Any<IReadOnlyList<ClientWorkDates>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        _checker = new PreCommitConflictChecker(_context, new Klacks.Api.Infrastructure.Repositories.Associations.ShiftRequiredQualificationRepository(_context, NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<Klacks.Api.Domain.Models.Associations.ShiftRequiredQualification>>()), timeline, resolver, new Klacks.Api.Application.Services.Schedules.ComplianceEscalationService(enforcementResolver), settingsReader, periodCapEvaluator, restDayRotationEvaluator, counterRuleEvaluator, restrictedTimeWindowEvaluator,
            NonReportingCompensatoryRestEvaluator(), holidayWorkEvaluator, NonReportingPlanningRuleEvaluator(), NoDayDirectives());
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

    private async Task CreateWorkAsync(Guid clientId, Guid shiftId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 7m,
            AnalyseToken = null
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task TrailOfAPlannedNight_CollidesWithTheNextMorningsWork_ButTheBareRowDoesNot()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();
        await CreateWorkAsync(client.Id, shift.Id, WorkDate.AddDays(1), new TimeOnly(6, 30), new TimeOnly(14, 0));

        var bare = new PlannedWorkRow(client.Id, WorkDate, new TimeOnly(22, 0), new TimeOnly(6, 0), shift.Id);
        var padded = bare with { TrailHours = 2m };

        var bareResult = await _checker.CheckAsync(new[] { bare }, null, CancellationToken.None);
        var paddedResult = await _checker.CheckAsync(new[] { padded }, null, CancellationToken.None);

        bareResult.NewConflicts.ShouldNotContain(c => c.Comment == "schedule.error-list.collision");
        paddedResult.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.collision" && c.Type == ScheduleValidationType.Error);
    }

    [Test]
    public async Task ExtraPaidHoursOfAPlannedRow_CountOnThePeriodCap_ButAVacatedRowGivesBackOnlyItsHours()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();
        var planned = new PlannedWorkRow(
            client.Id, WorkDate, new TimeOnly(8, 0), new TimeOnly(16, 0), shift.Id, 0m, 0m, 1.5m);
        var vacated = new PlannedRemovalRow(client.Id, WorkDate, new TimeOnly(10, 0), new TimeOnly(14, 0));

        await _checker.CheckAsync(new[] { planned }, new[] { vacated }, null, CancellationToken.None);

        await _periodCapEvaluator.Received(1).EvaluatePlannedForClientsAsync(
            Arg.Is<IReadOnlyList<ClientPlannedHours>>(clients =>
                clients.Count == 1
                && clients[0].ClientId == client.Id
                && clients[0].PlannedHours.Count == 2
                && clients[0].PlannedHours.Any(h => h.Hours == 9.5m)
                && clients[0].PlannedHours.Any(h => h.Hours == -4m)),
            (Guid?)null,
            CancellationToken.None);
    }

    [Test]
    public async Task LeadOfAPlannedEarlyShift_CollidesWithThePreviousNightsWork_ButTheBareRowDoesNot()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();
        await CreateWorkAsync(client.Id, shift.Id, WorkDate.AddDays(-1), new TimeOnly(22, 0), new TimeOnly(7, 0));

        var bare = new PlannedWorkRow(client.Id, WorkDate, new TimeOnly(8, 0), new TimeOnly(16, 0), shift.Id);
        var padded = bare with { LeadHours = 1.5m };

        var bareResult = await _checker.CheckAsync(new[] { bare }, null, CancellationToken.None);
        var paddedResult = await _checker.CheckAsync(new[] { padded }, null, CancellationToken.None);

        bareResult.NewConflicts.ShouldNotContain(c => c.Comment == "schedule.error-list.collision");
        paddedResult.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.collision" && c.Type == ScheduleValidationType.Error);
    }

    /// <summary>
    /// K12 reports persisted obligations, which these tests never seed - an evaluator reporting
    /// nothing keeps them about the rule they were written for. CompensatoryRestEvaluatorTests and
    /// the pre-commit K12 test cover the wiring itself.
    /// </summary>
    private static ICompensatoryRestEvaluator NonReportingCompensatoryRestEvaluator()
    {
        var evaluator = Substitute.For<ICompensatoryRestEvaluator>();
        evaluator.EvaluateForClientsAsync(Arg.Any<IReadOnlyList<ClientAsOfDate>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        return evaluator;
    }

    private static IPlanningRuleEvaluatorService NonReportingPlanningRuleEvaluator()
    {
        var evaluator = Substitute.For<IPlanningRuleEvaluatorService>();
        evaluator.EvaluatePlannedChangeAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        return evaluator;
    }

    private static Klacks.Api.Application.Interfaces.Schedules.IDayDirectiveConflictEvaluator NoDayDirectives()
    {
        var evaluator = Substitute.For<Klacks.Api.Application.Interfaces.Schedules.IDayDirectiveConflictEvaluator>();
        evaluator.EvaluatePlannedAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        return evaluator;
    }
}
