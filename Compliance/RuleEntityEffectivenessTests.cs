// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end A/B effectiveness tests for the admin-editable rule entities (mechanism B) against the
/// REAL PostgreSQL database: a rule row (PeriodCapRule / CounterRule / RestrictedTimeWindowRule) is
/// created and the production PreCommitConflictChecker - composed from its REAL dependency tree over
/// ONE shared DbContext - is asked to check planned work rows that violate the rule. Without the rule
/// no conflict is reported, with the rule it is, and the per-rule enforcement setting flips the
/// conflict between Warn and an overridable Block error. Every row (rules, settings, client) is
/// written inside a single database transaction that is ALWAYS rolled back in TearDown, so neither a
/// rule row nor a changed global setting can ever leak into the shared dev database on port 5434.
/// All seeded names additionally carry the INTEGRATION_TEST_ marker as a second line of defense.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Compliance;

[TestFixture]
[Category("RealDatabase")]
public class RuleEntityEffectivenessTests
{
    private const string TestMarker = "INTEGRATION_TEST_RULE_EFFECTIVENESS_";
    private const string WarnValue = "warn";
    private const string BlockValue = "block";

    private static readonly DateOnly CapDate = new(2091, 3, 10);

    private DataBaseContext _context = null!;
    private IDbContextTransaction _transaction = null!;
    private ServiceProvider _provider = null!;
    private IPreCommitConflictChecker _checker = null!;
    private Guid _clientId;

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _transaction = await _context.Database.BeginTransactionAsync();
        _provider = BuildRealDependencyTree(_context);
        _checker = _provider.GetRequiredService<IPreCommitConflictChecker>();

        _clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = _clientId,
            FirstName = TestMarker,
            Name = "RuleProbe",
            Type = EntityTypeEnum.Employee,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _provider?.Dispose();
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }

        _context?.Dispose();
    }

    [Test]
    public async Task PeriodCapRule_MonthlyTotalCap_RulePresenceDrivesConflict()
    {
        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementPeriodCap, WarnValue);
        var plannedRows = new List<PlannedWorkRow>
        {
            new(_clientId, CapDate, new TimeOnly(8, 0), new TimeOnly(20, 0)),
        };

        var without = await _checker.CheckAsync(plannedRows);
        MyPeriodCapEntries(without).ShouldBeEmpty("without the rule no period-cap conflict may be reported");

        await AddPeriodCapRuleAsync(capHours: 10m);

        var with = await _checker.CheckAsync(plannedRows);
        var entry = MyPeriodCapEntries(with).ShouldHaveSingleItem(
            "a 12h placement against a 10h monthly cap must be reported");
        entry.Type.ShouldBe(ScheduleValidationType.Warning, "warn mode must report a warning, not an error");
        entry.ClientId.ShouldBe(_clientId);
        entry.CommentParams["actualHours"].ShouldBe("12.0");
        entry.CommentParams["period"].ShouldBe(nameof(PeriodCapPeriod.Month));
        entry.CommentParams.ContainsKey(ComplianceRuleNames.EnforcementRuleParamKey).ShouldBeFalse();
    }

    [Test]
    public async Task PeriodCapRule_BlockSetting_EscalatesToOverridableError()
    {
        await AddPeriodCapRuleAsync(capHours: 10m);
        var plannedRows = new List<PlannedWorkRow>
        {
            new(_clientId, CapDate, new TimeOnly(8, 0), new TimeOnly(20, 0)),
        };

        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementPeriodCap, WarnValue);
        var warnEntry = MyPeriodCapEntries(await _checker.CheckAsync(plannedRows)).ShouldHaveSingleItem();
        warnEntry.Type.ShouldBe(ScheduleValidationType.Warning);

        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementPeriodCap, BlockValue);
        var result = await _checker.CheckAsync(plannedRows);
        var blockEntry = MyPeriodCapEntries(result).ShouldHaveSingleItem();
        blockEntry.Type.ShouldBe(ScheduleValidationType.Error, "block mode must escalate the period-cap breach to an error");
        blockEntry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.PeriodCap);
        result.HasOverridableBlocking.ShouldBeTrue("a block-mode compliance error must be supervisor-overridable");
    }

    [Test]
    public async Task CounterRule_NightShiftThreshold_RulePresenceDrivesConflict()
    {
        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementCounterRule, WarnValue);
        await UpsertSettingAsync(SettingKeys.SurchargeNightStart, "23:00");
        await UpsertSettingAsync(SettingKeys.SurchargeNightEnd, "06:00");
        var plannedRows = new List<PlannedWorkRow>
        {
            new(_clientId, CapDate, new TimeOnly(22, 0), new TimeOnly(6, 0)),
            new(_clientId, CapDate.AddDays(1), new TimeOnly(22, 0), new TimeOnly(6, 0)),
        };

        var without = await _checker.CheckAsync(plannedRows);
        MyCounterEntries(without).ShouldBeEmpty("without the rule no counter conflict may be reported");

        await AddCounterRuleAsync(threshold: 2, enforcement: null);

        var with = await _checker.CheckAsync(plannedRows);
        var entry = MyCounterEntries(with).ShouldHaveSingleItem(
            "the second night shift in the month must reach the threshold of 2");
        entry.Type.ShouldBe(ScheduleValidationType.Warning);
        entry.CommentParams["event"].ShouldBe(nameof(CounterEventType.NightShift));
        entry.CommentParams["count"].ShouldBe("2");
    }

    [Test]
    public async Task CounterRule_PerRuleEnforcementOverride_WinsOverGlobalWarn()
    {
        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementCounterRule, WarnValue);
        await UpsertSettingAsync(SettingKeys.SurchargeNightStart, "23:00");
        await UpsertSettingAsync(SettingKeys.SurchargeNightEnd, "06:00");
        await AddCounterRuleAsync(threshold: 2, enforcement: RuleEnforcementMode.Block);
        var plannedRows = new List<PlannedWorkRow>
        {
            new(_clientId, CapDate, new TimeOnly(22, 0), new TimeOnly(6, 0)),
            new(_clientId, CapDate.AddDays(1), new TimeOnly(22, 0), new TimeOnly(6, 0)),
        };

        var result = await _checker.CheckAsync(plannedRows);

        var entry = MyCounterEntries(result).ShouldHaveSingleItem();
        entry.Type.ShouldBe(ScheduleValidationType.Error,
            "the rule's own Enforcement=Block must win over the global counterRule=warn setting");
        entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.CounterRule);
    }

    [Test]
    public async Task RestrictedTimeWindowRule_RulePresenceDrivesConflict_AndBlockSettingEscalates()
    {
        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementRestrictedTimeWindow, WarnValue);
        var plannedRows = new List<PlannedWorkRow>
        {
            new(_clientId, CapDate, new TimeOnly(12, 30), new TimeOnly(14, 0)),
        };

        var without = await _checker.CheckAsync(plannedRows);
        MyRestrictedWindowEntries(without).ShouldBeEmpty(
            "without the rule a 12:30-14:00 placement must not be flagged");

        await AddRestrictedTimeWindowRuleAsync();

        var warnResult = await _checker.CheckAsync(plannedRows);
        var warnEntry = MyRestrictedWindowEntries(warnResult).ShouldHaveSingleItem(
            "a placement inside the 12:00-15:00 forbidden window must be reported");
        warnEntry.Type.ShouldBe(ScheduleValidationType.Warning);

        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementRestrictedTimeWindow, BlockValue);
        var blockResult = await _checker.CheckAsync(plannedRows);
        var blockEntry = MyRestrictedWindowEntries(blockResult).ShouldHaveSingleItem();
        blockEntry.Type.ShouldBe(ScheduleValidationType.Error, "block mode must escalate the window breach to an error");
        blockEntry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey]
            .ShouldBe(ComplianceRuleNames.RestrictedTimeWindow);

        var outside = await _checker.CheckAsync(new List<PlannedWorkRow>
        {
            new(_clientId, CapDate, new TimeOnly(16, 0), new TimeOnly(20, 0)),
        });
        MyRestrictedWindowEntries(outside).ShouldBeEmpty(
            "a placement outside the daily window must not be flagged even while the rule is active");
    }

    private List<ScheduleValidationNotificationDto> MyPeriodCapEntries(PreCommitCheckResult result) =>
        result.NewConflicts
            .Where(c => c.ClientId == _clientId
                && c.Comment == ScheduleValidationKeys.PeriodCap
                && c.CommentParams.GetValueOrDefault("capHours") == "10")
            .ToList();

    private List<ScheduleValidationNotificationDto> MyCounterEntries(PreCommitCheckResult result) =>
        result.NewConflicts
            .Where(c => c.ClientId == _clientId
                && c.Comment == ScheduleValidationKeys.CounterRule
                && c.CommentParams.GetValueOrDefault("threshold") == "2")
            .ToList();

    private List<ScheduleValidationNotificationDto> MyRestrictedWindowEntries(PreCommitCheckResult result) =>
        result.NewConflicts
            .Where(c => c.ClientId == _clientId
                && c.Comment == ScheduleValidationKeys.RestrictedTimeWindow
                && c.CommentParams.GetValueOrDefault("dailyStart") == "12:00"
                && c.CommentParams.GetValueOrDefault("dailyEnd") == "15:00")
            .ToList();

    private async Task AddPeriodCapRuleAsync(decimal capHours)
    {
        _context.PeriodCapRule.Add(new PeriodCapRule
        {
            Id = Guid.NewGuid(),
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = capHours,
            SchedulingRuleId = null,
            ImportSourceKey = $"{TestMarker}PCR_{Guid.NewGuid():N}",
            ImportContentHash = string.Empty,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddCounterRuleAsync(int threshold, RuleEnforcementMode? enforcement)
    {
        _context.CounterRule.Add(new CounterRule
        {
            Id = Guid.NewGuid(),
            EventType = CounterEventType.NightShift,
            Period = CounterPeriod.Month,
            Threshold = threshold,
            Enforcement = enforcement,
            SchedulingRuleId = null,
            ImportSourceKey = $"{TestMarker}CR_{Guid.NewGuid():N}",
            ImportContentHash = string.Empty,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddRestrictedTimeWindowRuleAsync()
    {
        _context.RestrictedTimeWindowRule.Add(new RestrictedTimeWindowRule
        {
            Id = Guid.NewGuid(),
            SeasonFromMonth = 1,
            SeasonFromDay = 1,
            SeasonToMonth = 12,
            SeasonToDay = 31,
            DailyStart = new TimeOnly(12, 0),
            DailyEnd = new TimeOnly(15, 0),
            AppliesToGroupTag = string.Empty,
            ImportSourceKey = $"{TestMarker}RTW_{Guid.NewGuid():N}",
            ImportContentHash = string.Empty,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    private async Task UpsertSettingAsync(string type, string value)
    {
        var existing = await _context.Settings.FirstOrDefaultAsync(s => s.Type == type);
        if (existing is null)
        {
            _context.Settings.Add(new Klacks.Api.Domain.Models.Settings.Settings
            {
                Id = Guid.NewGuid(),
                Type = type,
                Value = value,
            });
        }
        else
        {
            existing.Value = value;
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Composes the production PreCommitConflictChecker from its REAL implementations, all sharing the
    /// single transactional DbContext, so every read sees the uncommitted rule/setting rows. Only the
    /// two dependencies that are never touched by the exercised read paths are substituted: the
    /// SignalR work-notification service and the group filter service (both only used by write /
    /// group-scoped flows of PeriodHoursService).
    /// </summary>
    private static ServiceProvider BuildRealDependencyTree(DataBaseContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<ScheduleTimeOptions>(_ => { });
        services.AddSingleton(context);
        services.AddSingleton(Substitute.For<IWorkNotificationService>());
        services.AddSingleton(Substitute.For<IClientGroupFilterService>());
        services.AddSingleton<ISettingsReader, TransactionalSettingsReader>();
        services.AddSingleton<ITimelineCalculationService, TimelineCalculationService>();
        services.AddSingleton<IClientContractDataProvider, ClientContractDataProvider>();
        services.AddSingleton<ISchedulingPolicyResolver, SchedulingPolicyResolver>();
        services.AddSingleton<IComplianceEnforcementResolver, ComplianceEnforcementResolver>();
        services.AddSingleton<IOvertimeConfigResolver, OvertimeConfigResolver>();
        services.AddSingleton<IClientWorkHoursProvider, ClientWorkHoursProvider>();
        services.AddSingleton<IClientMembershipStartResolver, ClientMembershipStartResolver>();
        services.AddSingleton<IWeekConfiguration, Klacks.Api.Infrastructure.Services.WeekConfiguration>();
        services.AddSingleton<IPeriodCapRuleRepository, PeriodCapRuleRepository>();
        services.AddSingleton<ICounterRuleRepository, CounterRuleRepository>();
        services.AddSingleton<IRestDayRotationRuleRepository, RestDayRotationRuleRepository>();
        services.AddSingleton<IRestrictedTimeWindowRuleRepository, RestrictedTimeWindowRuleRepository>();
        services.AddSingleton<IPeriodHoursService, PeriodHoursService>();
        services.AddSingleton<IPeriodCapEvaluator, PeriodCapEvaluator>();
        services.AddSingleton<IRestDayRotationEvaluator, RestDayRotationEvaluator>();
        services.AddSingleton<ICounterRuleEvaluator, CounterRuleEvaluator>();
        services.AddSingleton<IRestrictedTimeWindowEvaluator, RestrictedTimeWindowEvaluator>();
        services.AddSingleton<IComplianceEscalationService, ComplianceEscalationService>();
        // K12 reads persisted obligations, which this fixture never seeds; the real evaluator would
        // report nothing anyway, but registering it keeps the dependency tree resolvable.
        services.AddSingleton<ICompensatoryRestObligationRepository, CompensatoryRestObligationRepository>();
        services.AddSingleton<ICompensatoryRestEvaluator, CompensatoryRestEvaluator>();
        services.AddSingleton<Klacks.Api.Application.Interfaces.IHolidayCalculatorCache, Klacks.Api.Infrastructure.Scripting.HolidayCalculatorCache>();
        services.AddSingleton<IHolidayWorkExemptionRuleRepository, HolidayWorkExemptionRuleRepository>();
        services.AddSingleton<IClientHolidayCalendarResolver, ClientHolidayCalendarResolver>();
        services.AddSingleton<IHolidayWorkEvaluator, HolidayWorkEvaluator>();
        services.AddSingleton<IPreCommitConflictChecker, PreCommitConflictChecker>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Minimal ISettingsReader over the SAME transactional DbContext, so the enforcement resolver
    /// reads the uncommitted setting values written by the test.
    /// </summary>
    private sealed class TransactionalSettingsReader : ISettingsReader
    {
        private readonly DataBaseContext _context;

        public TransactionalSettingsReader(DataBaseContext context)
        {
            _context = context;
        }

        public Task<Klacks.Api.Domain.Models.Settings.Settings?> GetSetting(string type)
        {
            return _context.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Type == type);
        }

        public async Task<IReadOnlyDictionary<string, string>> GetSettingsByTypesAsync(IEnumerable<string> types)
        {
            var typeList = types.ToList();
            return await _context.Settings
                .AsNoTracking()
                .Where(s => typeList.Contains(s.Type))
                .ToDictionaryAsync(s => s.Type, s => s.Value);
        }
    }
}
