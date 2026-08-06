// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Effectiveness tests from a shipped country package down to a detector, against the REAL PostgreSQL
/// database. A region JSON from deploy/onprem/regions is parsed with the production reader, its industry
/// preset becomes the scheduling rule a contract points at, and the detectors judge a timeline built
/// around exactly that preset's limits. This closes the gap that the existing suite covered the import
/// side (hash/insert/update) and the rule side (hand-seeded rows) but never the chain in between:
/// "the values country package X ships make detector Y fire".
///
/// Everything is written inside one transaction that is ALWAYS rolled back in TearDown, because the
/// integration tests share port 5434 with the running dev application, and every seeded row carries the
/// INTEGRATION_TEST_ marker as a second line of defence. See SeedRuleFromPresetAsync for why the
/// production import service itself cannot be driven from here.
/// </summary>

using System.Runtime.CompilerServices;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Setup;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Staffs;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Repositories.Settings;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Compliance;

[TestFixture]
[Category("RealDatabase")]
public class RegionPackageEffectivenessTests
{
    private const string TestMarker = "INTEGRATION_TEST_REGION_EFFECTIVENESS_";
    private const string GermanyPackage = "de";
    private const string HomecareIndustry = "homecare";

    private static readonly DateOnly WeekMonday = new(2091, 3, 5);

    private DataBaseContext _context = null!;
    private IDbContextTransaction _transaction = null!;
    private ServiceProvider _provider = null!;
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

        _clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = _clientId,
            FirstName = TestMarker,
            Name = "RegionProbe",
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

    /// <summary>
    /// The safety net of every test in this fixture: the import must run on the very DbContext that
    /// holds the open transaction. If IUnitOfWork ever resolved its own context, its SaveChanges would
    /// commit presets, qualifications and macros into the shared dev database instead of being rolled
    /// back. This asserts the instance identity rather than trusting the registration.
    /// </summary>
    [Test]
    public void UnitOfWork_SharesTheTransactionalDbContext()
    {
        var unitOfWork = _provider.GetRequiredService<IUnitOfWork>();

        var contextOfUnitOfWork = typeof(UnitOfWork)
            .GetField("context", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(unitOfWork);

        contextOfUnitOfWork.ShouldBeSameAs(_context,
            "the entity import must write through the transactional context, otherwise a test run leaks into the shared dev database");
    }

    [Test]
    public async Task GermanHomecarePreset_MaxWeeklyHours_ReachesThePolicyAndFiresTheDetector()
    {
        var profile = await LoadPackageAsync(GermanyPackage);
        var preset = SinglePresetOf(profile, HomecareIndustry);
        preset.MaxWeeklyHours.ShouldNotBeNull($"the {GermanyPackage}/{HomecareIndustry} preset must ship a weekly cap for this test to mean anything");
        var cap = preset.MaxWeeklyHours.Value;

        var ruleId = await SeedRuleFromPresetAsync(preset, HomecareIndustry);
        await AssignContractAsync(ruleId);

        var policy = await _provider.GetRequiredService<ISchedulingPolicyResolver>()
            .GetForClientAsync(_clientId, WeekMonday);

        policy.MaxWeeklyHours.ShouldBe(TimeSpan.FromHours((double)cap),
            "the weekly cap of the imported preset must survive the contract resolution");

        var entriesBelow = EvaluateWeek(HoursPerDayFor(cap - 1), policy);
        entriesBelow.ShouldBeEmpty("a week below the imported cap must not be reported");

        var entriesAbove = EvaluateWeek(HoursPerDayFor(cap + 1), policy);
        var entry = entriesAbove.ShouldHaveSingleItem("a week above the imported cap must be reported exactly once");
        entry.Comment.ShouldBe(Klacks.Api.Domain.Constants.ScheduleValidationKeys.WeeklyOvertime);
        entry.CommentParams["maxHours"].ShouldBe($"{cap:F0}");
    }

    [TestCase(GermanyPackage, HomecareIndustry)]
    [TestCase("at", "security")]
    [TestCase("fr", "logistics")]
    public async Task Preset_MaxDailyHours_ReachesThePolicyAndFiresTheDetector(string countryCode, string industry)
    {
        var preset = SinglePresetOf(await LoadPackageAsync(countryCode), industry);
        preset.MaxDailyHours.ShouldNotBeNull($"{countryCode}/{industry} must ship a daily cap");
        var cap = (double)preset.MaxDailyHours!.Value;

        var policy = await ResolvePolicyForPresetAsync(preset, industry);
        policy.MaxDailyHours.ShouldBe(TimeSpan.FromHours(cap));

        SingleDayEntries(cap - 1, policy).ShouldBeEmpty("a day below the imported cap must not be reported");
        var entry = SingleDayEntries(cap + 1, policy).ShouldHaveSingleItem("a day above the imported cap must be reported");
        entry.Comment.ShouldBe(ScheduleValidationKeys.Overtime);
    }

    [TestCase(GermanyPackage, HomecareIndustry)]
    [TestCase("at", "security")]
    [TestCase("fr", "logistics")]
    public async Task Preset_MaxConsecutiveDays_ReachesThePolicyAndFiresTheDetector(string countryCode, string industry)
    {
        var preset = SinglePresetOf(await LoadPackageAsync(countryCode), industry);
        preset.MaxConsecutiveDays.ShouldNotBeNull($"{countryCode}/{industry} must ship a consecutive-day cap");
        var cap = preset.MaxConsecutiveDays!.Value;

        var policy = await ResolvePolicyForPresetAsync(preset, industry);
        policy.MaxConsecutiveDays.ShouldBe(cap);

        ConsecutiveDayEntries(cap, policy).ShouldBeEmpty("a run exactly at the cap must not be reported");
        var entry = ConsecutiveDayEntries(cap + 1, policy).ShouldHaveSingleItem("a run above the cap must be reported");
        entry.Comment.ShouldBe(ScheduleValidationKeys.ConsecutiveDays);
        entry.CommentParams["maxDays"].ShouldBe(cap.ToString());
    }

    [TestCase(GermanyPackage, HomecareIndustry)]
    [TestCase("at", "security")]
    [TestCase("fr", "logistics")]
    public async Task Preset_MinRestDays_ReachesThePolicyAndFiresTheDetector(string countryCode, string industry)
    {
        var preset = SinglePresetOf(await LoadPackageAsync(countryCode), industry);
        preset.MinRestDays.ShouldNotBeNull($"{countryCode}/{industry} must ship a rest-day quota");
        var required = preset.MinRestDays!.Value;

        var policy = await ResolvePolicyForPresetAsync(preset, industry);
        policy.MinRestDays.ShouldBe(required);

        var workDaysLeavingEnoughRest = DaysPerWeek - (int)Math.Ceiling(required);
        MinRestDayEntries(workDaysLeavingEnoughRest, policy)
            .ShouldBeEmpty("a week that still leaves the required rest days must not be reported");
        var entry = MinRestDayEntries(DaysPerWeek, policy)
            .ShouldHaveSingleItem("a fully worked week must breach any positive rest-day quota");
        entry.Comment.ShouldBe(ScheduleValidationKeys.MinRestDays);
    }

    [TestCase(GermanyPackage, HomecareIndustry)]
    [TestCase("at", "security")]
    [TestCase("fr", "logistics")]
    public async Task Preset_MinPauseHours_ReachesThePolicyAndFiresTheRestCheck(string countryCode, string industry)
    {
        var preset = SinglePresetOf(await LoadPackageAsync(countryCode), industry);
        preset.MinPauseHours.ShouldNotBeNull($"{countryCode}/{industry} must ship a minimum pause");
        var required = (double)preset.MinPauseHours!.Value;

        var policy = await ResolvePolicyForPresetAsync(preset, industry);
        policy.MinRestHours.ShouldBe(TimeSpan.FromHours(required),
            "the preset's minPauseHours is what the rest check reads as MinRestHours");

        RestViolationEntries(required + 1, policy).ShouldBeEmpty("a gap above the required pause must not be reported");
        var entry = RestViolationEntries(required - 1, policy).ShouldHaveSingleItem("a gap below the required pause must be reported");
        entry.Comment.ShouldBe(ScheduleValidationKeys.RestViolation);
    }

    /// <summary>
    /// The industry axis must actually reach the detectors: the same client and the same timeline,
    /// judged once under the strictest and once under the loosest industry preset of ONE country, must
    /// come out differently. In 24 of 30 packages all five industries carry identical values, so most
    /// countries cannot prove anything here at all.
    /// </summary>
    [Test]
    public async Task IndustryPresets_OfTheSameCountry_CarryTheirDifferenceIntoTheDetector()
    {
        var profile = await LoadPackageAsync(IndustryDifferencePackage);
        var presets = profile.IndustryProfiles!
            .Where(kv => kv.Value.SchedulingRulePresets.Count > 0)
            .Select(kv => new { Industry = kv.Key, Preset = kv.Value.SchedulingRulePresets[0] })
            .Where(x => x.Preset.MaxWeeklyHours.HasValue)
            .ToList();

        presets.Count.ShouldBeGreaterThan(1,
            $"package {IndustryDifferencePackage} must ship weekly caps in more than one industry");

        var strictest = presets.OrderBy(x => x.Preset.MaxWeeklyHours!.Value).First();
        var loosest = presets.OrderByDescending(x => x.Preset.MaxWeeklyHours!.Value).First();
        strictest.Preset.MaxWeeklyHours.ShouldNotBe(loosest.Preset.MaxWeeklyHours,
            $"package {IndustryDifferencePackage} is expected to ship differing weekly caps across industries");

        var hoursPerDay = HoursPerDayFor(strictest.Preset.MaxWeeklyHours!.Value + 1);

        var strictPolicy = await ResolvePolicyForPresetAsync(strictest.Preset, strictest.Industry);
        EvaluateWeek(hoursPerDay, strictPolicy).ShouldHaveSingleItem(
            $"the stricter {strictest.Industry} cap must flag this week");

        await ResetContractAssignmentAsync();

        var loosePolicy = await ResolvePolicyForPresetAsync(loosest.Preset, loosest.Industry);
        EvaluateWeek(hoursPerDay, loosePolicy).ShouldBeEmpty(
            $"the same week must stay unflagged under the looser {loosest.Industry} cap");
    }

    /// <summary>
    /// The shipping state: assigning a preset to a contract is a customer action, so a client without
    /// one must fall back to the settings/defaults chain and must NOT inherit any imported preset.
    /// </summary>
    [Test]
    public async Task ContractWithoutSchedulingRule_FallsBackToDefaults_AndIgnoresImportedPresets()
    {
        var preset = SinglePresetOf(await LoadPackageAsync(GermanyPackage), HomecareIndustry);
        await SeedRuleFromPresetAsync(preset, HomecareIndustry);
        await AssignContractAsync(schedulingRuleId: null);

        var policy = await _provider.GetRequiredService<ISchedulingPolicyResolver>()
            .GetForClientAsync(_clientId, WeekMonday);

        policy.MaxWeeklyHours.ShouldNotBe(TimeSpan.FromHours((double)preset.MaxWeeklyHours!.Value),
            "an unassigned preset must never reach a contract that does not reference it");
    }

    /// <summary>
    /// Documents a real hole rather than hiding it: only 6 of the 30 shipped packages fill all five
    /// limit fields, so a contract on a preset from one of the other 24 silently runs on the global
    /// default. If a package is completed later, this test turns red and must be updated - which is the
    /// point, because the fallback is invisible to the planner.
    /// </summary>
    [TestCase("pl", "security")]
    [TestCase("jp", "logistics")]
    public async Task Preset_WithoutConsecutiveDayCap_SilentlyFallsBackToTheGlobalDefault(string countryCode, string industry)
    {
        var preset = SinglePresetOf(await LoadPackageAsync(countryCode), industry);
        preset.MaxConsecutiveDays.ShouldBeNull(
            $"{countryCode}/{industry} is recorded as shipping no consecutive-day cap; update this test if the package was completed");

        var policy = await ResolvePolicyForPresetAsync(preset, industry);

        policy.MaxConsecutiveDays.ShouldBe(SchedulingPolicyDefaults.MaxConsecutiveDays,
            "an unfilled preset field must fall through to the documented default");
    }

    private async Task<SchedulingPolicy> ResolvePolicyForPresetAsync(
        RegionSetupSchedulingRulePreset preset,
        string industry)
    {
        var ruleId = await SeedRuleFromPresetAsync(preset, industry);
        await AssignContractAsync(ruleId);
        return await _provider.GetRequiredService<ISchedulingPolicyResolver>()
            .GetForClientAsync(_clientId, WeekMonday);
    }

    private static double HoursPerDayFor(decimal weeklyHours) => (double)weeklyHours / DaysPerWeek;

    // Switzerland is one of the very few packages whose industry blocks carry genuinely different values
    // for the same field (weekly cap 42 to 50). Measured across all packages, only five country/field
    // combinations differ at all - elsewhere the industries either repeat the same numbers or simply
    // leave the field empty, which is a gap rather than a difference.
    private const string IndustryDifferencePackage = "ch";
    private const int DaysPerWeek = 7;

    private List<Klacks.Api.Application.DTOs.Notifications.ScheduleValidationNotificationDto> EvaluateWeek(
        double hoursPerDay,
        SchedulingPolicy policy)
    {
        var timeline = new ClientTimeline(_clientId);
        for (var offset = 0; offset < DaysPerWeek; offset++)
        {
            var day = WeekMonday.AddDays(offset);
            timeline.AddBlock(new ScheduleBlock(
                Guid.NewGuid(),
                ScheduleBlockType.Work,
                _clientId,
                day.ToDateTime(new TimeOnly(0, 0)),
                day.ToDateTime(new TimeOnly(0, 0)).AddHours(hoursPerDay)));
        }
        timeline.SortBlocks();

        var entries = new List<Klacks.Api.Application.DTOs.Notifications.ScheduleValidationNotificationDto>();
        var (weekStart, weekEnd) = ScheduleValidationBuilder.IsoWeekOf(WeekMonday);
        ScheduleValidationBuilder.AddWeeklyOvertime(entries, timeline, TestMarker, weekStart, weekEnd, policy);
        return entries;
    }

    private List<ScheduleValidationNotificationDto> SingleDayEntries(double hours, SchedulingPolicy policy)
    {
        var timeline = TimelineOfWorkDays(1, hours);
        var entries = new List<ScheduleValidationNotificationDto>();
        ScheduleValidationBuilder.AddOvertime(entries, timeline, TestMarker, WeekMonday, WeekMonday, policy);
        return entries;
    }

    private List<ScheduleValidationNotificationDto> ConsecutiveDayEntries(int workDays, SchedulingPolicy policy)
    {
        var timeline = TimelineOfWorkDays(workDays, DefaultShiftHours);
        var entries = new List<ScheduleValidationNotificationDto>();
        var (weekStart, weekEnd) = ScheduleValidationBuilder.IsoWeekOf(WeekMonday);
        ScheduleValidationBuilder.AddConsecutiveDays(entries, timeline, TestMarker, weekStart, weekEnd, policy);
        return entries;
    }

    private List<ScheduleValidationNotificationDto> MinRestDayEntries(int workDays, SchedulingPolicy policy)
    {
        var timeline = TimelineOfWorkDays(workDays, DefaultShiftHours);
        var entries = new List<ScheduleValidationNotificationDto>();
        var (weekStart, weekEnd) = ScheduleValidationBuilder.IsoWeekOf(WeekMonday);
        ScheduleValidationBuilder.AddMinRestDays(entries, timeline, TestMarker, weekStart, weekEnd, policy);
        return entries;
    }

    /// <summary>
    /// Two shifts separated by the given gap, so the rest check can judge the pause between them.
    /// </summary>
    private List<ScheduleValidationNotificationDto> RestViolationEntries(double gapHours, SchedulingPolicy policy)
    {
        var timeline = new ClientTimeline(_clientId);
        var firstEnd = WeekMonday.ToDateTime(new TimeOnly(12, 0));
        timeline.AddBlock(new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Work, _clientId,
            WeekMonday.ToDateTime(new TimeOnly(8, 0)), firstEnd));
        timeline.AddBlock(new ScheduleBlock(
            Guid.NewGuid(), ScheduleBlockType.Work, _clientId,
            firstEnd.AddHours(gapHours), firstEnd.AddHours(gapHours + 4)));
        timeline.SortBlocks();

        var entries = new List<ScheduleValidationNotificationDto>();
        ScheduleValidationBuilder.AddRestViolations(entries, timeline, TestMarker, policy);
        return entries;
    }

    private ClientTimeline TimelineOfWorkDays(int workDays, double hoursPerDay)
    {
        var timeline = new ClientTimeline(_clientId);
        for (var offset = 0; offset < workDays; offset++)
        {
            var day = WeekMonday.AddDays(offset);
            timeline.AddBlock(new ScheduleBlock(
                Guid.NewGuid(), ScheduleBlockType.Work, _clientId,
                day.ToDateTime(new TimeOnly(0, 0)),
                day.ToDateTime(new TimeOnly(0, 0)).AddHours(hoursPerDay)));
        }
        timeline.SortBlocks();
        return timeline;
    }

    private const double DefaultShiftHours = 4;

    private static async Task<RegionSetupProfile> LoadPackageAsync(string countryCode)
    {
        var path = Path.Combine(RegionsDirectory(), $"{countryCode}.json");
        File.Exists(path).ShouldBeTrue($"country package {countryCode}.json must exist at {path}");
        return await RegionSetupFileReader.ReadProfileAsync(path);
    }

    private static RegionSetupSchedulingRulePreset SinglePresetOf(RegionSetupProfile profile, string industry)
    {
        profile.IndustryProfiles.ShouldNotBeNull();
        profile.IndustryProfiles.TryGetValue(industry, out var block).ShouldBeTrue($"package must ship the {industry} industry block");
        return block!.SchedulingRulePresets.ShouldHaveSingleItem($"the {industry} block is expected to ship exactly one preset");
    }

    /// <summary>
    /// Materialises a shipped preset as the scheduling rule a contract can point at, carrying the values
    /// verbatim from the country package.
    /// The production import path (RegionSetupService.ApplyEntityImportsAsync) is deliberately NOT used:
    /// it wraps its work in ExecuteInTransactionAsync and commits, so it cannot be enclosed in the test
    /// transaction that protects the shared dev database on port 5434 - and its rows would carry real
    /// package names, which must never be cleaned up by name (incident 2026-07-03). The import mechanics
    /// themselves stay covered by RegionSetupServiceTests; what is proven here is that the values a
    /// package ships reach the policy and make the detectors fire.
    /// </summary>
    private async Task<Guid> SeedRuleFromPresetAsync(RegionSetupSchedulingRulePreset preset, string industry)
    {
        var rule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = TestMarker + preset.Name,
            Industry = industry,
            MaxDailyHours = preset.MaxDailyHours,
            MaxWeeklyHours = preset.MaxWeeklyHours,
            MinPauseHours = preset.MinPauseHours,
            MinRestDays = preset.MinRestDays,
            MaxConsecutiveDays = preset.MaxConsecutiveDays,
            IsDeleted = false,
        };
        _context.SchedulingRules.Add(rule);
        await _context.SaveChangesAsync();
        return rule.Id;
    }

    /// <summary>
    /// Removes the client's contract assignments so a second preset can be judged in the same test
    /// without the first one still resolving.
    /// </summary>
    private async Task ResetContractAssignmentAsync()
    {
        var links = await _context.ClientContract.Where(cc => cc.ClientId == _clientId).ToListAsync();
        _context.ClientContract.RemoveRange(links);
        await _context.SaveChangesAsync();
    }

    private async Task AssignContractAsync(Guid? schedulingRuleId)
    {
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = TestMarker + "Contract",
            SchedulingRuleId = schedulingRuleId,
            ValidFrom = DateTime.SpecifyKind(WeekMonday.AddYears(-1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            IsDeleted = false,
        };
        _context.Contract.Add(contract);
        _context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            ContractId = contract.Id,
            FromDate = WeekMonday.AddYears(-1),
            UntilDate = null,
            IsActive = true,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    private static string RegionsDirectory([CallerFilePath] string callerFilePath = "")
    {
        var testProjectDirectory = Directory.GetParent(Path.GetDirectoryName(callerFilePath)!)!.FullName;
        var repositoryRoot = Directory.GetParent(testProjectDirectory)!.FullName;
        return Path.Combine(repositoryRoot, "Klacks.Api", "deploy", "onprem", "regions");
    }

    private static ServiceProvider BuildRealDependencyTree(DataBaseContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(context);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddSingleton<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<ISchedulingRuleImportRepository, SchedulingRuleImportRepository>();
        services.AddSingleton<ISchedulingRuleRateRevisionImportRepository, SchedulingRuleRateRevisionImportRepository>();
        services.AddSingleton<IQualificationImportRepository, QualificationImportRepository>();
        services.AddSingleton(Substitute.For<Klacks.Api.Infrastructure.Interfaces.IMacroCache>());
        services.AddSingleton<IMacroImportRepository, MacroImportRepository>();
        // Group lookups only matter for group-tag-scoped compliance rows, which the preset tests here
        // never touch; the real repository would drag in the whole group service facade.
        services.AddSingleton(Substitute.For<IGroupRepository>());
        services.AddSingleton<IPeriodCapRuleRepository, PeriodCapRuleRepository>();
        services.AddSingleton<IRestDayRotationRuleRepository, RestDayRotationRuleRepository>();
        services.AddSingleton<ICounterRuleRepository, CounterRuleRepository>();
        services.AddSingleton<IRestrictedTimeWindowRuleRepository, RestrictedTimeWindowRuleRepository>();

        // Dependencies of SettingsRepository that the entity import never exercises.
        services.AddSingleton(Substitute.For<Klacks.Api.Domain.Interfaces.CalendarSelections.ICalendarRuleFilterService>());
        services.AddSingleton(Substitute.For<Klacks.Api.Domain.Interfaces.CalendarSelections.ICalendarRuleSortingService>());
        services.AddSingleton(Substitute.For<Klacks.Api.Domain.Interfaces.CalendarSelections.ICalendarRulePaginationService>());
        services.AddSingleton(Substitute.For<IMacroManagementService>());
        services.AddSingleton(Substitute.For<ILanguagePluginService>());
        services.AddSingleton(Substitute.For<ICalendarSelectionRepository>());
        services.AddSingleton(Substitute.For<IMacroScriptValidator>());
        services.AddSingleton(Substitute.For<IDomainEventDispatcher>());

        services.AddSingleton<RegionSetupService>();
        services.AddSingleton<IRegionEntityImportService>(sp => sp.GetRequiredService<RegionSetupService>());

        services.AddSingleton<ISettingsReader, TransactionalSettingsReader>();
        services.AddSingleton<IClientContractDataProvider, ClientContractDataProvider>();
        services.AddSingleton<ISchedulingPolicyResolver, SchedulingPolicyResolver>();
        services.AddSingleton<IOvertimeConfigResolver, OvertimeConfigResolver>();
        services.AddSingleton<IClientWorkHoursProvider, ClientWorkHoursProvider>();
        services.AddSingleton<IClientMembershipStartResolver, ClientMembershipStartResolver>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Minimal ISettingsReader over the SAME transactional DbContext, so resolvers see the values the
    /// import wrote inside the open transaction.
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
