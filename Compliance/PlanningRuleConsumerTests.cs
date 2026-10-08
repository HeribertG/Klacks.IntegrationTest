// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The PlanningConstraint family through its real consumers against PostgreSQL (Npgsql, never InMemory): the
/// pre-commit gate (PreCommitConflictChecker) and the period-close validation (PeriodValidationLoader) are
/// composed with the real PlanningRuleEvaluatorService, loader, repositories and Work reader; every other
/// evaluator is a non-reporting stub. Proves that only an Approved constraint acts (Proposed, Rejected and
/// Revoked never report), that a Hard finding reaches the gate as an overridable Error, and that a violation
/// that already exists does not block an unrelated write. No host is booted; every row is written inside one
/// transaction that is always rolled back, and every seeded name carries the INTEGRATION_TEST_ prefix.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.Api.Infrastructure.Services.PeriodClosing;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.Compliance;

[TestFixture]
[Category("RealDatabase")]
public class PlanningRuleConsumerTests
{
    private const string TestMarker = "INTEGRATION_TEST_PLANNING_RULE_CONSUMER_";
    private const string MaxTwoNightsJson = """{"schemaVersion":1,"kind":"Night","maxRun":2}""";

    private static readonly DateOnly Monday = new(2092, 5, 5);
    private static readonly TimeOnly NightStart = new(22, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);

    private DataBaseContext _context = null!;
    private IDbContextTransaction _transaction = null!;
    private PlanningRuleEvaluatorService _planningRuleEvaluator = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _transaction = await _context.Database.BeginTransactionAsync();
        _planningRuleEvaluator = BuildPlanningRuleEvaluator();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }

        _context?.Dispose();
    }

    [TestCase(RuleApprovalStatus.Proposed)]
    [TestCase(RuleApprovalStatus.Rejected)]
    [TestCase(RuleApprovalStatus.Revoked)]
    public async Task NotApprovedHardConstraint_ActsNowhere(RuleApprovalStatus status)
    {
        var clientId = await AddClientAsync("not-approved");
        await AddConstraintAsync(status);
        var shiftId = await AddShiftAsync();
        await AddNightsAsync(clientId, shiftId, Monday, 3);

        var gate = await BuildChecker().CheckAsync([new PlannedWorkRow(clientId, Monday.AddDays(3), NightStart, NightEnd, shiftId)]);
        var issues = await BuildPeriodLoader().LoadAsync(Monday, Monday.AddDays(6), groupId: null);

        gate.NewConflicts.ShouldNotContain(c => c.Comment == ScheduleValidationKeys.PlanningRule);
        issues.ShouldNotContain(i => i.MessageKey == ScheduleValidationKeys.PlanningRule);
    }

    [Test]
    public async Task ApprovedHardConstraint_BlocksTheWorseningWrite_AndShowsInThePeriodClose()
    {
        var clientId = await AddClientAsync("approved");
        var constraint = await AddConstraintAsync(RuleApprovalStatus.Approved);
        var shiftId = await AddShiftAsync();
        await AddNightsAsync(clientId, shiftId, Monday, 2);

        var gate = await BuildChecker().CheckAsync([new PlannedWorkRow(clientId, Monday.AddDays(2), NightStart, NightEnd, shiftId)]);

        var blocked = gate.NewConflicts.ShouldHaveSingleItem(string.Join(", ", gate.NewConflicts.Select(c => c.Comment)));
        blocked.Comment.ShouldBe(ScheduleValidationKeys.PlanningRule);
        blocked.Type.ShouldBe(ScheduleValidationType.Error);
        blocked.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.PlanningRule);
        blocked.CommentParams[PlanningRuleNotificationMapper.RuleIdParam].ShouldBe(constraint.Id.ToString());
        gate.HasOverridableBlocking.ShouldBeTrue();

        await AddNightsAsync(clientId, shiftId, Monday.AddDays(2), 1);
        var issue = (await BuildPeriodLoader().LoadAsync(Monday, Monday.AddDays(6), groupId: null))
            .Where(i => i.MessageKey == ScheduleValidationKeys.PlanningRule)
            .ShouldHaveSingleItem();
        issue.Code.ShouldBe("PlanningRule");
        issue.ClientId.ShouldBe(clientId);
        issue.Severity.ShouldBe(ScheduleValidationType.Error);
        issue.MessageParams[PlanningRuleNotificationMapper.ObservedParam].ShouldBe("3");
    }

    [Test]
    public async Task ApprovedHardConstraint_ExistingViolation_DoesNotBlockAnUnrelatedWrite()
    {
        var clientId = await AddClientAsync("existing");
        await AddConstraintAsync(RuleApprovalStatus.Approved);
        var shiftId = await AddShiftAsync();
        await AddNightsAsync(clientId, shiftId, Monday, 4);

        var gate = await BuildChecker().CheckAsync(
            [new PlannedWorkRow(clientId, Monday.AddDays(5), new TimeOnly(8, 0), new TimeOnly(16, 0), shiftId)]);

        gate.NewConflicts.ShouldNotContain(c => c.Comment == ScheduleValidationKeys.PlanningRule);
    }

    [Test]
    public async Task InvalidHardConstraint_DoesNotBlockUnrelatedWrites_ValidRuleStillBlocks_AndTheCloseShowsIt()
    {
        var clientId = await AddClientAsync("invalid");
        var invalid = await AddConstraintAsync(RuleApprovalStatus.Approved, """{"schemaVersion":9}""");
        await AddConstraintAsync(RuleApprovalStatus.Approved);
        var shiftId = await AddShiftAsync();
        await AddNightsAsync(clientId, shiftId, Monday, 2);
        var checker = BuildChecker();

        var unrelated = await checker.CheckAsync(
            [new PlannedWorkRow(clientId, Monday.AddDays(5), new TimeOnly(8, 0), new TimeOnly(16, 0), shiftId)]);
        var violating = await checker.CheckAsync([new PlannedWorkRow(clientId, Monday.AddDays(2), NightStart, NightEnd, shiftId)]);
        var issues = await BuildPeriodLoader().LoadAsync(Monday, Monday.AddDays(6), groupId: null);

        unrelated.HasBlocking.ShouldBeFalse();
        unrelated.NewConflicts.ShouldContain(c => c.Comment == ScheduleValidationKeys.PlanningRuleInvalid
            && c.CommentParams[PlanningRuleNotificationMapper.RuleIdParam] == invalid.Id.ToString());
        violating.HasOverridableBlocking.ShouldBeTrue();
        violating.NewConflicts.ShouldContain(c => c.Comment == ScheduleValidationKeys.PlanningRule);
        issues.ShouldContain(i => i.MessageKey == ScheduleValidationKeys.PlanningRuleInvalid && i.Severity == ScheduleValidationType.Error);
    }

    private PlanningRuleEvaluatorService BuildPlanningRuleEvaluator()
    {
        var dataReader = new PlanningRuleDataReader(_context);
        var enforcement = Substitute.For<IComplianceEnforcementResolver>();
        enforcement.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);
        var contracts = Substitute.For<IClientContractDataProvider>();
        contracts.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());
        var hierarchy = new GroupClientService(
            _context, new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }), NullLogger<GroupClientService>.Instance);
        var loader = new PlanningRuleSetLoader(
            new CounterRuleRepository(_context),
            new PlanningConstraintRepository(_context),
            new PlanningConstraintValidator(),
            enforcement,
            contracts,
            hierarchy,
            dataReader,
            new PlanningRuleCarryInLoader(dataReader),
            NullSettingsReader(),
            new PlanningConstraintPresence(new PlanningConstraintRepository(_context), new MemoryCache(new MemoryCacheOptions())),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PlanningRuleSetLoader>.Instance);
        return new PlanningRuleEvaluatorService(loader, dataReader);
    }

    private PreCommitConflictChecker BuildChecker()
    {
        var enforcement = Substitute.For<IComplianceEnforcementResolver>();
        enforcement.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);
        var periodCap = Substitute.For<IPeriodCapEvaluator>();
        periodCap.EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, decimal Hours)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var restDayRotation = Substitute.For<IRestDayRotationEvaluator>();
        restDayRotation.EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var counterRule = Substitute.For<ICounterRuleEvaluator>();
        counterRule.EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var restrictedTimeWindow = Substitute.For<IRestrictedTimeWindowEvaluator>();
        restrictedTimeWindow.EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, Guid? ShiftId)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var compensatoryRest = Substitute.For<ICompensatoryRestEvaluator>();
        compensatoryRest.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var holidayWork = Substitute.For<IHolidayWorkEvaluator>();
        holidayWork.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        return new PreCommitConflictChecker(
            _context,
            new Klacks.Api.Infrastructure.Repositories.Associations.ShiftRequiredQualificationRepository(_context, NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<Klacks.Api.Domain.Models.Associations.ShiftRequiredQualification>>()),
            NewTimelineService(),
            LenientPolicyResolver(),
            new ComplianceEscalationService(enforcement),
            Substitute.For<ISettingsReader>(),
            periodCap,
            restDayRotation,
            counterRule,
            restrictedTimeWindow,
            compensatoryRest,
            holidayWork,
            _planningRuleEvaluator);
    }

    private PeriodValidationLoader BuildPeriodLoader()
    {
        var periodCap = Substitute.For<IPeriodCapEvaluator>();
        periodCap.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var restDayRotation = Substitute.For<IRestDayRotationEvaluator>();
        restDayRotation.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var counterRule = Substitute.For<ICounterRuleEvaluator>();
        counterRule.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var restrictedTimeWindow = Substitute.For<IRestrictedTimeWindowEvaluator>();
        restrictedTimeWindow.EvaluateRangeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var compensatoryRest = Substitute.For<ICompensatoryRestEvaluator>();
        compensatoryRest.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var holidayWork = Substitute.For<IHolidayWorkEvaluator>();
        holidayWork.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        return new PeriodValidationLoader(
            _context,
            NewTimelineService(),
            LenientPolicyResolver(),
            periodCap,
            restDayRotation,
            counterRule,
            restrictedTimeWindow,
            Substitute.For<ICompensatoryRestObligationReconciler>(),
            compensatoryRest,
            holidayWork,
            _planningRuleEvaluator);
    }

    private static TimelineCalculationService NewTimelineService()
        => new(Options.Create(new ScheduleTimeOptions()), Substitute.For<ILogger<TimelineCalculationService>>());

    private static ISchedulingPolicyResolver LenientPolicyResolver()
    {
        var policy = new SchedulingPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(24), 999, TimeSpan.FromHours(168), 0);
        var resolver = Substitute.For<ISchedulingPolicyResolver>();
        resolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(policy);
        resolver.GetForClientsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>())
            .Returns(new Dictionary<Guid, SchedulingPolicy>());
        return resolver;
    }

    private async Task<PlanningConstraint> AddConstraintAsync(RuleApprovalStatus status, string json = MaxTwoNightsJson)
    {
        var constraint = new PlanningConstraint
        {
            Id = Guid.NewGuid(),
            Kind = PlanningConstraintKind.MaxConsecutiveOfKind,
            Severity = PlanningConstraintSeverity.Hard,
            Weight = 1d,
            ScopeType = PlanningConstraintScopeType.Global,
            ParametersJson = json,
            Origin = RuleOrigin.Admin,
            ApprovalStatus = status,
            Paraphrase = TestMarker + status,
        };
        _context.PlanningConstraint.Add(constraint);
        await _context.SaveChangesAsync();
        return constraint;
    }

    private async Task<Guid> AddClientAsync(string suffix)
    {
        var client = new Client { Id = Guid.NewGuid(), FirstName = TestMarker + suffix, Name = "ConsumerProbe", Type = EntityTypeEnum.Employee };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();
        return client.Id;
    }

    private async Task<Guid> AddShiftAsync()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestMarker + "shift",
            Abbreviation = "IPR",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = Monday.AddYears(-1),
            StartShift = NightStart,
            EndShift = NightEnd,
        };
        _context.Shift.Add(shift);
        await _context.SaveChangesAsync();
        return shift.Id;
    }

    private async Task AddNightsAsync(Guid clientId, Guid shiftId, DateOnly first, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _context.Work.Add(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ShiftId = shiftId,
                CurrentDate = first.AddDays(i),
                StartTime = NightStart,
                EndTime = NightEnd,
                WorkTime = 8m,
                LockLevel = WorkLockLevel.None,
            });
        }

        await _context.SaveChangesAsync();
    }

    private static Klacks.Api.Domain.Interfaces.Settings.ISettingsReader NullSettingsReader()
    {
        var reader = Substitute.For<Klacks.Api.Domain.Interfaces.Settings.ISettingsReader>();
        reader.GetSetting(Arg.Any<string>()).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);
        return reader;
    }
}
