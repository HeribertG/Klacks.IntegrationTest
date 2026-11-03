// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The replacement write guard over the REAL PreCommitConflictChecker and a real database: a substitute who works six
/// hours of his own on the day may take four hours of replacement (ten hours, the daily limit), but not the five hours a
/// widened replacement then has. The limit counts the day in one piece; a check of the added hour alone would never
/// reach it. The daily limit is in block mode, so the overtime finding refuses the write. Every row carries an
/// INTEGRATION_TEST_ prefixed key, the cleanup deletes nothing else.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
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
public class ReplacementWriteGuardRealCheckerTests
{
    private const string TestPrefix = "INTEGRATION_TEST_RWGUARD_";
    private static readonly DateOnly Day = new(2099, 8, 12);
    private static readonly TimeOnly OwnStart = new(8, 0);
    private static readonly TimeOnly OwnEnd = new(14, 0);
    private static readonly TimeOnly ReplacementStart = new(14, 0);
    private static readonly TimeOnly FourHoursEnd = new(18, 0);
    private static readonly TimeOnly FiveHoursEnd = new(19, 0);
    private static readonly TimeOnly ParentEnd = new(20, 0);
    private const decimal OwnHours = 6m;
    private const decimal FourHours = 4m;
    private const decimal FiveHours = 5m;

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ReplacementWriteGuard _guard = null!;
    private Guid _substituteId;
    private Guid _ownerId;
    private Guid _shiftId;
    private Work _parentWork = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _substituteId = Guid.NewGuid();
        _ownerId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();

        _context.Client.AddRange(
            new Client { Id = _substituteId, Name = TestPrefix + "SUB", FirstName = "Test", Company = string.Empty },
            new Client { Id = _ownerId, Name = TestPrefix + "OWNER", FirstName = "Test", Company = string.Empty });
        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = TestPrefix + "SHIFT",
            Abbreviation = "RWG",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = OwnStart,
            EndShift = ParentEnd,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 12m,
        });
        await _context.SaveChangesAsync();
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _substituteId,
            ShiftId = _shiftId,
            CurrentDate = Day,
            StartTime = OwnStart,
            EndTime = OwnEnd,
            WorkTime = OwnHours,
            Information = TestPrefix + "OWN",
        });
        await _context.SaveChangesAsync();

        _parentWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _ownerId,
            ShiftId = _shiftId,
            CurrentDate = Day,
            StartTime = OwnStart,
            EndTime = ParentEnd,
        };
        _guard = NewGuard();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task FourHoursOfReplacementOnSixHoursOfOwnWork_ReachTheDailyLimitWithoutExceedingIt()
    {
        await _guard.EnsureAllowedAsync(
            Replacement(FourHoursEnd, FourHours), _parentWork, false, null, CancellationToken.None);
    }

    [Test]
    public async Task WideningTheReplacementFromFourToFiveHours_IsRefused_BecauseTheDayThenHasElevenHours()
    {
        var stored = Replacement(FourHoursEnd, FourHours);

        var exception = await Should.ThrowAsync<ConflictException>(() => _guard.EnsureAllowedAsync(
            Replacement(FiveHoursEnd, FiveHours), _parentWork, false, stored, CancellationToken.None));

        exception.Message.ShouldContain("blocked");
    }

    [Test]
    public async Task ChangingOnlyTheTextOfTheStoredFourHours_IsNotCheckedAgain()
    {
        var stored = Replacement(FourHoursEnd, FourHours);
        var updated = Replacement(FourHoursEnd, FourHours);
        updated.Description = "only the text changed";

        await _guard.EnsureAllowedAsync(updated, _parentWork, false, stored, CancellationToken.None);
    }

    [Test]
    public async Task ShorteningTheStoredReplacement_IsNotChecked()
    {
        var stored = Replacement(FiveHoursEnd, FiveHours);

        await _guard.EnsureAllowedAsync(
            Replacement(FourHoursEnd, FourHours), _parentWork, false, stored, CancellationToken.None);
    }

    private WorkChange Replacement(TimeOnly end, decimal hours) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = _parentWork.Id,
        Type = WorkChangeType.ReplacementWithin,
        ReplaceClientId = _substituteId,
        StartTime = ReplacementStart,
        EndTime = end,
        ChangeTime = hours,
        Description = TestPrefix + "CHANGE",
    };

    private ReplacementWriteGuard NewGuard()
    {
        var timeline = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()),
            Substitute.For<ILogger<TimelineCalculationService>>());

        var resolver = Substitute.For<ISchedulingPolicyResolver>();
        resolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new SchedulingPolicy(
                TimeSpan.FromHours(11), TimeSpan.FromHours(10), 6, TimeSpan.FromHours(50), 2));

        var enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Block);

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

        var holidayWorkEvaluator = Substitute.For<IHolidayWorkEvaluator>();
        holidayWorkEvaluator
            .EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var compensatoryRestEvaluator = Substitute.For<ICompensatoryRestEvaluator>();
        compensatoryRestEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var planningRuleEvaluator = Substitute.For<IPlanningRuleEvaluatorService>();
        planningRuleEvaluator.EvaluatePlannedChangeAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var dayDirectives = Substitute.For<IDayDirectiveConflictEvaluator>();
        dayDirectives.EvaluatePlannedAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var checker = new PreCommitConflictChecker(
            _context,
            new Klacks.Api.Infrastructure.Repositories.Associations.ShiftRequiredQualificationRepository(
                _context, Substitute.For<ILogger<Klacks.Api.Domain.Models.Associations.ShiftRequiredQualification>>()),
            timeline, resolver, new ComplianceEscalationService(enforcementResolver), Substitute.For<ISettingsReader>(),
            periodCapEvaluator, restDayRotationEvaluator, counterRuleEvaluator, restrictedTimeWindowEvaluator,
            compensatoryRestEvaluator, holidayWorkEvaluator, planningRuleEvaluator, dayDirectives);

        var clientRepository = Substitute.For<IClientRepository>();
        clientRepository.GetTypeAndDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Employee, "Substitute"));

        return new ReplacementWriteGuard(clientRepository, checker, Substitute.For<ISupervisorOverrideAuthorizer>(), Substitute.For<IContainerWorkChangeGuard>());
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
            DELETE FROM work WHERE information LIKE '{TestPrefix}%'
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
