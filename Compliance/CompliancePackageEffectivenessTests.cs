// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Effectiveness tests from a shipped country package down to the FOUR compliance rule types, against
/// the REAL PostgreSQL database. RegionPackageEffectivenessTests closes the same gap for the five
/// SchedulingRule limit fields (preset -> policy -> ScheduleValidationBuilder); this fixture closes it
/// for the compliance sections of the package - compliance.periodCaps, compliance.restDayRotations,
/// compliance.restrictedTimeWindows and compliance.compensatoryRest - each of which is judged by its own
/// evaluator instead of the policy. A region JSON from deploy/onprem/regions is parsed with the
/// production reader, its compliance rows are materialised VERBATIM through the same field mapping the
/// importer uses, and the real evaluator (composed from its real dependency tree over ONE transactional
/// DbContext) judges a timeline built around exactly those package values. Every asserted number is read
/// back out of the parsed package, never written as a literal, so a test can only pass when real package
/// data - not a default - reaches the detector.
///
/// Everything is written inside one transaction that is ALWAYS rolled back in TearDown, because the
/// integration tests share port 5434 with the running dev application, and every seeded row carries the
/// INTEGRATION_TEST_ marker as a second line of defence. The single documented exception is the Group
/// row of the restricted-time-window test: compliance.restrictedTimeWindows[].appliesToGroupTag is
/// matched against Group.Name verbatim, so a marked name would silently take the rule out of scope and
/// turn the test green for the wrong reason - the marker lives in Group.Description there instead.
///
/// SHARED-DATABASE NOTE (important for anyone extending this file): the dev database on 5434 already
/// carries imported compliance rows - at the time of writing exactly de.json's periodCap (rolling 24w /
/// 48h) and restDayRotation (sunday, 15 free of 52). The three rule-table evaluators read their rules
/// GLOBALLY (IPeriodCapRuleRepository/IRestDayRotationRuleRepository/IRestrictedTimeWindowRuleRepository
/// .GetAllActiveAsync), so a foreign row carrying the same package values fires on the same timeline and
/// is indistinguishable from the seeded one in the reported entry. The positive cases therefore measure
/// the DELTA across seeding the rule (baseline before, +1 after) instead of asserting a single item; the
/// negative cases can still assert emptiness, because every entry matching the value filter comes from a
/// rule carrying exactly these package values and such a rule cannot fire on a compliant timeline.
/// CompensatoryRest is the exception: its obligations are read per client
/// (ICompensatoryRestObligationRepository.GetOpenByClientAsync) and the client is a fresh Guid, so a
/// single-item assertion is sound there.
/// </summary>

using System.Globalization;
using System.Runtime.CompilerServices;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Setup;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Settings;
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
[NonParallelizable]
public sealed class CompliancePackageEffectivenessTests
{
    private const string TestMarker = "INTEGRATION_TEST_COMPLIANCE_PACKAGE_";
    private const string BlockValue = "block";
    private const string HomecareIndustry = "homecare";
    private const string CompensatoryRestPackage = "it";
    private const int DaysPerWeek = 7;
    private const decimal ShiftHours = 8m;

    private static readonly DateOnly BaseDate = new(2091, 6, 15);
    private static readonly TimeOnly WorkStart = new(8, 0);
    private static readonly TimeOnly WorkEnd = new(16, 0);

    private DataBaseContext _context = null!;
    private IDbContextTransaction _transaction = null!;
    private ServiceProvider _provider = null!;
    private Guid _clientId;
    private Guid _shiftId;

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

        _clientId = await AddClientAsync("ComplianceProbe");
        _shiftId = await AddShiftAsync("UNTAGGED");
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
    /// The safety net of every test in this fixture: the evaluators, repositories and the reconciler must
    /// all run on the very DbContext that holds the open transaction, otherwise a seeded rule, work row or
    /// obligation would be committed into the shared dev database instead of being rolled back. Asserts
    /// the instance identity rather than trusting the registration.
    /// </summary>
    [Test]
    public void ResolvedEvaluator_SharesTheTransactionalDbContext()
    {
        var evaluator = _provider.GetRequiredService<IRestDayRotationEvaluator>();

        var contextOfEvaluator = typeof(RestDayRotationEvaluator)
            .GetField("_context", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(evaluator);

        contextOfEvaluator.ShouldBeSameAs(_context,
            "the evaluators must read through the transactional context, otherwise a test run leaks into the shared dev database");
    }

    // ---------------------------------------------------------------------------------------------
    // compliance.periodCaps -> PeriodCapEvaluator (K6 rolling-average mode)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A rolling-average cap shipped by a package must reach the PeriodCapEvaluator with its own window
    /// width and its own weekly average, and must judge the timeline against exactly those two numbers.
    /// The characteristic package fields proven here are windowWeeks and maxAverageWeeklyHours: both are
    /// read out of the parsed JSON and compared against the evaluator's own CommentParams, so a row
    /// carrying defaults instead of package data cannot pass.
    /// </summary>
    [TestCase("de")]
    [TestCase("fr")]
    [TestCase("pl")]
    public async Task PackageRollingAverageCap_ReachesThePeriodCapEvaluator(string countryCode)
    {
        var profile = await LoadPackageAsync(countryCode);
        var cap = RollingCapOf(profile, countryCode);
        var weeks = cap.WindowWeeks!.Value;
        var average = cap.MaxAverageWeeklyHours!.Value;

        var mode = await SeedEnforcementAsync(profile, SettingKeys.ComplianceEnforcementRollingAverage,
            profile.Compliance!.Enforcement?.Rules?.RollingAverage);

        var evaluator = _provider.GetRequiredService<IPeriodCapEvaluator>();
        var overCap = weeks * (average + 1m);
        var atCap = weeks * average;

        var before = MyRollingAverageEntries(await EvaluatePlannedHoursAsync(evaluator, overCap), cap);

        await SeedRollingAverageRuleAsync(cap, countryCode, schedulingRuleId: null);

        var after = MyRollingAverageEntries(await EvaluatePlannedHoursAsync(evaluator, overCap), cap);
        (after.Count - before.Count).ShouldBe(1,
            $"seeding {countryCode}'s rolling-average cap must add exactly one report for a week average above it");

        // Every entry left by the filter comes from a rule carrying exactly these package values, so they
        // are indistinguishable by construction and asserting on the first one is equivalent.
        var entry = after[0];
        entry.Comment.ShouldBe(ScheduleValidationKeys.RollingAverage);
        entry.ClientId.ShouldBe(_clientId);
        entry.CommentParams["windowWeeks"].ShouldBe(weeks.ToString(CultureInfo.InvariantCulture),
            "the window width must be the one the package ships, not a default");
        entry.CommentParams["capHours"].ShouldBe(average.ToString("F0", CultureInfo.InvariantCulture),
            "the weekly average must be the one the package ships, not a default");
        entry.CommentParams["actualHours"].ShouldBe((average + 1m).ToString("F1", CultureInfo.InvariantCulture));
        ShouldMatchPackageSeverity(entry, mode, ComplianceRuleNames.RollingAverage);

        MyRollingAverageEntries(await EvaluatePlannedHoursAsync(evaluator, atCap), cap).ShouldBeEmpty(
            "a window average exactly at the package's cap must not be reported");
    }

    // ---------------------------------------------------------------------------------------------
    // compliance.restDayRotations -> RestDayRotationEvaluator (K10)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A rest-day rotation shipped by a package must reach the RestDayRotationEvaluator with its own
    /// weekday, its own window width and its own minimum free count, and must flip exactly at that count.
    /// Only de and pl ship this section at all. The characteristic package fields proven here are
    /// dayOfWeek, minFree and windowWeeks, all read out of the parsed JSON.
    /// </summary>
    [TestCase("de")]
    [TestCase("pl")]
    public async Task PackageRestDayRotation_ReachesTheRestDayRotationEvaluator(string countryCode)
    {
        var profile = await LoadPackageAsync(countryCode);
        var rotations = profile.Compliance?.RestDayRotations;
        rotations.ShouldNotBeNull($"package {countryCode} must ship compliance.restDayRotations for this test to mean anything");
        var rotation = rotations!.ShouldHaveSingleItem($"package {countryCode} is expected to ship exactly one rest-day rotation");

        var dayOfWeek = Enum.Parse<DayOfWeek>(rotation.DayOfWeek!, ignoreCase: true);
        var windowWeeks = rotation.WindowWeeks!.Value;
        var minFree = rotation.MinFree!.Value;

        var mode = await SeedEnforcementAsync(profile, SettingKeys.ComplianceEnforcementRestDayRotation,
            profile.Compliance!.Enforcement?.Rules?.RestDayRotation);

        // Anchoring the evaluation on an occurrence of the rule's own weekday keeps both inspected anchor
        // dates (the day itself and the following day) on the SAME window end, so one rule reports once.
        var windowEnd = MostRecentOccurrenceOnOrBefore(dayOfWeek, BaseDate);
        var windowStart = windowEnd.AddDays(-DaysPerWeek * (windowWeeks - 1));

        // One occurrence more than the package tolerates: exactly minFree - 1 stay free.
        var occupiedDays = OccurrencesFrom(windowStart, windowWeeks - minFree + 1);
        var occupiedIds = await AddWorkDaysAsync(_clientId, occupiedDays);

        var evaluator = _provider.GetRequiredService<IRestDayRotationEvaluator>();
        var before = MyRestDayEntries(await evaluator.EvaluateAsync(_clientId, TestMarker, windowEnd), rotation);

        await SeedRestDayRotationRuleAsync(rotation, countryCode, schedulingRuleId: null);

        var after = MyRestDayEntries(await evaluator.EvaluateAsync(_clientId, TestMarker, windowEnd), rotation);
        (after.Count - before.Count).ShouldBe(1,
            $"seeding {countryCode}'s rest-day rotation must add exactly one report for a window with too few free days");

        var entry = after[0];
        entry.Comment.ShouldBe(ScheduleValidationKeys.RestDayRotation);
        entry.ClientId.ShouldBe(_clientId);
        entry.Date.ShouldBe(windowEnd);
        entry.CommentParams["dayOfWeek"].ShouldBe(dayOfWeek.ToString(),
            "the weekday must be the one the package ships, not a default");
        entry.CommentParams["minFree"].ShouldBe(minFree.ToString(CultureInfo.InvariantCulture),
            "the minimum free count must be the one the package ships, not a default");
        entry.CommentParams["windowWeeks"].ShouldBe(windowWeeks.ToString(CultureInfo.InvariantCulture),
            "the window width must be the one the package ships, not a default");
        entry.CommentParams["actualFree"].ShouldBe((minFree - 1).ToString(CultureInfo.InvariantCulture));
        ShouldMatchPackageSeverity(entry, mode, ComplianceRuleNames.RestDayRotation);

        await RemoveWorkAsync(occupiedIds[^1]);

        MyRestDayEntries(await evaluator.EvaluateAsync(_clientId, TestMarker, windowEnd), rotation).ShouldBeEmpty(
            "a window that leaves exactly the package's minimum of free days must not be reported");
    }

    // ---------------------------------------------------------------------------------------------
    // compliance.restrictedTimeWindows -> RestrictedTimeWindowEvaluator (K16)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A restricted time window shipped by a package must reach the RestrictedTimeWindowEvaluator with its
    /// own season, its own daily window and its own group tag. Only ae and sa ship this section, and both
    /// ship enforcement restrictedTimeWindow = block, so this is also the one type whose package-driven
    /// severity is an Error rather than a Warning. Three negative probes prove that all three package
    /// axes are load-bearing: a slot outside the daily window, a slot outside the season, and the same
    /// slot on a shift that is NOT tagged with the package's appliesToGroupTag.
    /// </summary>
    [TestCase("ae")]
    [TestCase("sa")]
    public async Task PackageRestrictedTimeWindow_ReachesTheRestrictedTimeWindowEvaluator(string countryCode)
    {
        var profile = await LoadPackageAsync(countryCode);
        var windows = profile.Compliance?.RestrictedTimeWindows;
        windows.ShouldNotBeNull($"package {countryCode} must ship compliance.restrictedTimeWindows for this test to mean anything");
        var window = windows!.ShouldHaveSingleItem($"package {countryCode} is expected to ship exactly one restricted time window");

        var (fromMonth, fromDay) = ParseMonthDay(window.SeasonFrom!);
        var (toMonth, toDay) = ParseMonthDay(window.SeasonTo!);
        var dailyStart = ParseTimeOfDay(window.DailyStart!);
        var dailyEnd = ParseTimeOfDay(window.DailyEnd!);
        var groupTag = window.AppliesToGroupTag ?? string.Empty;
        groupTag.ShouldNotBeNullOrWhiteSpace($"package {countryCode} is expected to scope its window by a group tag");
        ((fromMonth * 100) + fromDay).ShouldBeLessThan((toMonth * 100) + toDay,
            "this test picks its in/out-of-season dates assuming a season that does not wrap across new year");

        var mode = await SeedEnforcementAsync(profile, SettingKeys.ComplianceEnforcementRestrictedTimeWindow,
            profile.Compliance!.Enforcement?.Rules?.RestrictedTimeWindow);

        var taggedShiftId = await AddShiftAsync("TAGGED");
        await LinkShiftToGroupTagAsync(taggedShiftId, groupTag);

        var inSeason = new DateOnly(BaseDate.Year, fromMonth, fromDay);
        var outOfSeason = new DateOnly(BaseDate.Year, toMonth, toDay).AddDays(1);
        var evaluator = _provider.GetRequiredService<IRestrictedTimeWindowEvaluator>();

        var before = MyRestrictedWindowEntries(
            await EvaluatePlannedSlotAsync(evaluator, inSeason, dailyStart, dailyEnd, taggedShiftId), window);

        await SeedRestrictedTimeWindowRuleAsync(window, countryCode);

        var after = MyRestrictedWindowEntries(
            await EvaluatePlannedSlotAsync(evaluator, inSeason, dailyStart, dailyEnd, taggedShiftId), window);
        (after.Count - before.Count).ShouldBe(1,
            $"seeding {countryCode}'s restricted time window must add exactly one report for a slot inside it");

        var entry = after[0];
        entry.Comment.ShouldBe(ScheduleValidationKeys.RestrictedTimeWindow);
        entry.ClientId.ShouldBe(_clientId);
        entry.CommentParams["dailyStart"].ShouldBe(window.DailyStart,
            "the daily window start must be the one the package ships, not a default");
        entry.CommentParams["dailyEnd"].ShouldBe(window.DailyEnd,
            "the daily window end must be the one the package ships, not a default");
        entry.CommentParams["seasonFrom"].ShouldBe(window.SeasonFrom,
            "the season start must be the one the package ships, not a default");
        entry.CommentParams["seasonTo"].ShouldBe(window.SeasonTo);
        entry.CommentParams["groupTag"].ShouldBe(groupTag,
            "the group tag must be the one the package ships, not a default");
        ShouldMatchPackageSeverity(entry, mode, ComplianceRuleNames.RestrictedTimeWindow);

        var afterWindow = dailyEnd.AddHours(3);
        MyRestrictedWindowEntries(
            await EvaluatePlannedSlotAsync(evaluator, inSeason, dailyEnd, afterWindow, taggedShiftId), window)
            .ShouldBeEmpty("a slot starting where the package's daily window ends must not be reported");

        MyRestrictedWindowEntries(
            await EvaluatePlannedSlotAsync(evaluator, outOfSeason, dailyStart, dailyEnd, taggedShiftId), window)
            .ShouldBeEmpty("the same slot one day after the package's season must not be reported");

        MyRestrictedWindowEntries(
            await EvaluatePlannedSlotAsync(evaluator, inSeason, dailyStart, dailyEnd, _shiftId), window)
            .ShouldBeEmpty($"a shift outside the package's '{groupTag}' group tag must not be reported");
    }

    // ---------------------------------------------------------------------------------------------
    // compliance.compensatoryRest -> CompensatoryRestEvaluator (K12)
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The compensatory-rest section is settings-based, not an entity import: the package's enabled flag
    /// and deadlineDays become COMPLIANCE_COMPENSATORY_REST_* settings, the reconciler turns a rest
    /// shortfall into an obligation whose deadline is triggerDate + the package's deadlineDays, and the
    /// evaluator reports it. it.json is the only package shipping the section. Two package values are
    /// proven at once, and they come from two different sections: deadlineDays (compliance.compensatoryRest)
    /// drives the obligation's due date, and the it/homecare preset's minPauseHours (industryProfiles)
    /// drives the standard-rest snapshot the shortfall is measured against.
    /// </summary>
    [Test]
    public async Task PackageCompensatoryRest_ReachesTheCompensatoryRestEvaluator()
    {
        var profile = await LoadPackageAsync(CompensatoryRestPackage);
        var compensatoryRest = profile.Compliance?.CompensatoryRest;
        compensatoryRest.ShouldNotBeNull($"package {CompensatoryRestPackage} must ship compliance.compensatoryRest");
        compensatoryRest!.Enabled.ShouldBe(true, "the section is only effective when the package enables it");
        var deadlineDays = compensatoryRest.DeadlineDays!.Value;

        var preset = SinglePresetOf(profile, HomecareIndustry);
        preset.MinPauseHours.ShouldNotBeNull($"the {CompensatoryRestPackage}/{HomecareIndustry} preset must ship a minimum pause");
        var minPause = preset.MinPauseHours!.Value;

        var mode = await SeedEnforcementAsync(profile, SettingKeys.ComplianceEnforcementCompensatoryRest,
            profile.Compliance!.Enforcement?.Rules?.CompensatoryRest);
        await SeedCompensatoryRestSettingsAsync(compensatoryRest);

        var ruleId = await SeedRuleFromPresetAsync(preset, HomecareIndustry);
        await AssignContractAsync(_clientId, ruleId);

        var policy = await _provider.GetRequiredService<ISchedulingPolicyResolver>()
            .GetForClientAsync(_clientId, BaseDate);
        policy.MinRestHours.ShouldBe(TimeSpan.FromHours((double)minPause),
            "the preset's minPauseHours is what the compensatory-rest reconciler reads as the standard rest");

        // A gap of one hour below the package's minimum pause: the shortfall is the smallest amount that
        // still triggers, so the assertion below reads directly off the package number.
        var shortfall = 1m;
        var gapHours = minPause - shortfall;
        await AddWorkAsync(_clientId, BaseDate, new TimeOnly(8, 0), new TimeOnly(20, 0));
        var secondStart = new TimeOnly(20, 0).AddHours((double)gapHours);
        await AddWorkAsync(_clientId, BaseDate.AddDays(1), secondStart, secondStart.AddHours(4));

        await _provider.GetRequiredService<ICompensatoryRestObligationReconciler>()
            .ReconcileAsync(_clientId, BaseDate, BaseDate.AddDays(1));

        var obligation = (await _context.CompensatoryRestObligation
            .AsNoTracking()
            .Where(o => o.ClientId == _clientId && !o.IsDeleted)
            .ToListAsync())
            .ShouldHaveSingleItem("the rest shortfall must produce exactly one obligation for this client");
        obligation.TriggerDate.ShouldBe(BaseDate);
        obligation.DueDate.ShouldBe(BaseDate.AddDays(deadlineDays),
            "the deadline must be the package's deadlineDays after the trigger, not a default");
        obligation.StandardRestHours.ShouldBe(minPause,
            "the snapshot must be the preset's minPauseHours the package ships, not a default");
        obligation.ShortfallHours.ShouldBe(shortfall);

        var evaluator = _provider.GetRequiredService<ICompensatoryRestEvaluator>();

        var due = (await evaluator.EvaluateAsync(_clientId, TestMarker, obligation.DueDate))
            .ShouldHaveSingleItem("on the deadline the open obligation must be reported as due");
        due.Comment.ShouldBe(ScheduleValidationKeys.CompensatoryRestDue);
        due.CommentParams["dueDate"].ShouldBe(BaseDate.AddDays(deadlineDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        due.CommentParams["shortfallHours"].ShouldBe(shortfall.ToString("F1", CultureInfo.InvariantCulture));
        ShouldMatchPackageSeverity(due, mode, ComplianceRuleNames.CompensatoryRest);

        var overdue = (await evaluator.EvaluateAsync(_clientId, TestMarker, obligation.DueDate.AddDays(1)))
            .ShouldHaveSingleItem("one day past the package's deadline the obligation must be reported as overdue");
        overdue.Comment.ShouldBe(ScheduleValidationKeys.CompensatoryRestOverdue);
    }

    /// <summary>
    /// Counter-probe of the type above: with the identical package settings in place, a rest gap that
    /// satisfies the preset's minPauseHours must produce no obligation and therefore nothing to report.
    /// </summary>
    [Test]
    public async Task PackageCompensatoryRest_WithSufficientRest_ReportsNothing()
    {
        var profile = await LoadPackageAsync(CompensatoryRestPackage);
        var preset = SinglePresetOf(profile, HomecareIndustry);
        var minPause = preset.MinPauseHours!.Value;

        await SeedEnforcementAsync(profile, SettingKeys.ComplianceEnforcementCompensatoryRest,
            profile.Compliance!.Enforcement?.Rules?.CompensatoryRest);
        await SeedCompensatoryRestSettingsAsync(profile.Compliance!.CompensatoryRest!);

        var ruleId = await SeedRuleFromPresetAsync(preset, HomecareIndustry);
        await AssignContractAsync(_clientId, ruleId);

        await AddWorkAsync(_clientId, BaseDate, new TimeOnly(8, 0), new TimeOnly(20, 0));
        var secondStart = new TimeOnly(20, 0).AddHours((double)(minPause + 1m));
        await AddWorkAsync(_clientId, BaseDate.AddDays(1), secondStart, secondStart.AddHours(4));

        await _provider.GetRequiredService<ICompensatoryRestObligationReconciler>()
            .ReconcileAsync(_clientId, BaseDate, BaseDate.AddDays(1));

        (await _context.CompensatoryRestObligation.AsNoTracking()
            .CountAsync(o => o.ClientId == _clientId && !o.IsDeleted))
            .ShouldBe(0, "a rest gap above the package's minimum pause must not create an obligation");

        (await _provider.GetRequiredService<ICompensatoryRestEvaluator>()
            .EvaluateAsync(_clientId, TestMarker, BaseDate.AddDays(10)))
            .ShouldBeEmpty("without an obligation the evaluator must report nothing");
    }

    // ---------------------------------------------------------------------------------------------
    // Industry scoping (SchedulingRuleId binding) against the real resolution chain
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The industry axis of a compliance row must survive the REAL contract resolution chain, not just a
    /// mocked IClientContractDataProvider: a PeriodCapRule bound to industry rule A must judge only the
    /// client whose active contract references rule A, and must leave a client on rule B untouched even
    /// though both are handed the identical timeline.
    /// </summary>
    [Test]
    public async Task ScopedPeriodCapRule_JudgesOnlyTheContractOfItsOwnIndustryRule()
    {
        var profile = await LoadPackageAsync("de");
        var cap = RollingCapOf(profile, "de");
        var (ruleA, ruleB) = await SeedTwoIndustryRulesAsync(profile);

        var clientOnA = _clientId;
        await AssignContractAsync(clientOnA, ruleA);
        var clientOnB = await AddClientAsync("ScopedProbeB");
        await AssignContractAsync(clientOnB, ruleB);

        var evaluator = _provider.GetRequiredService<IPeriodCapEvaluator>();
        var overCap = cap.WindowWeeks!.Value * (cap.MaxAverageWeeklyHours!.Value + 1m);

        var beforeA = MyRollingAverageEntries(await EvaluatePlannedHoursAsync(evaluator, overCap, clientOnA), cap);
        var beforeB = MyRollingAverageEntries(await EvaluatePlannedHoursAsync(evaluator, overCap, clientOnB), cap);

        await SeedRollingAverageRuleAsync(cap, "de", schedulingRuleId: ruleA);

        var afterA = MyRollingAverageEntries(await EvaluatePlannedHoursAsync(evaluator, overCap, clientOnA), cap);
        var afterB = MyRollingAverageEntries(await EvaluatePlannedHoursAsync(evaluator, overCap, clientOnB), cap);

        (afterA.Count - beforeA.Count).ShouldBe(1,
            "the client whose contract references the bound scheduling rule must be reported");
        (afterB.Count - beforeB.Count).ShouldBe(0,
            "a client on a different industry rule must not be judged by a rule bound to another one");
    }

    /// <summary>
    /// Same proof for the K10 rule type, which owns its own copy of the industry-scope resolution
    /// (RestDayRotationEvaluator.ResolveApplicableRulesAsync): identical persisted work rows for both
    /// clients, one bound rule, only the matching contract is reported.
    /// </summary>
    [Test]
    public async Task ScopedRestDayRotationRule_JudgesOnlyTheContractOfItsOwnIndustryRule()
    {
        var profile = await LoadPackageAsync("pl");
        var rotation = profile.Compliance!.RestDayRotations!.Single();
        var dayOfWeek = Enum.Parse<DayOfWeek>(rotation.DayOfWeek!, ignoreCase: true);
        var windowWeeks = rotation.WindowWeeks!.Value;
        var minFree = rotation.MinFree!.Value;

        var (ruleA, ruleB) = await SeedTwoIndustryRulesAsync(profile);
        var clientOnA = _clientId;
        await AssignContractAsync(clientOnA, ruleA);
        var clientOnB = await AddClientAsync("ScopedRotationB");
        await AssignContractAsync(clientOnB, ruleB);

        var windowEnd = MostRecentOccurrenceOnOrBefore(dayOfWeek, BaseDate);
        var windowStart = windowEnd.AddDays(-DaysPerWeek * (windowWeeks - 1));
        var occupiedDays = OccurrencesFrom(windowStart, windowWeeks - minFree + 1);
        await AddWorkDaysAsync(clientOnA, occupiedDays);
        await AddWorkDaysAsync(clientOnB, occupiedDays);

        var evaluator = _provider.GetRequiredService<IRestDayRotationEvaluator>();
        var beforeA = MyRestDayEntries(await evaluator.EvaluateAsync(clientOnA, TestMarker, windowEnd), rotation);
        var beforeB = MyRestDayEntries(await evaluator.EvaluateAsync(clientOnB, TestMarker, windowEnd), rotation);

        await SeedRestDayRotationRuleAsync(rotation, "pl", schedulingRuleId: ruleA);

        var afterA = MyRestDayEntries(await evaluator.EvaluateAsync(clientOnA, TestMarker, windowEnd), rotation);
        var afterB = MyRestDayEntries(await evaluator.EvaluateAsync(clientOnB, TestMarker, windowEnd), rotation);

        (afterA.Count - beforeA.Count).ShouldBe(1,
            "the client whose contract references the bound scheduling rule must be reported");
        (afterB.Count - beforeB.Count).ShouldBe(0,
            "a client on a different industry rule must not be judged by a rule bound to another one");
    }

    // ---------------------------------------------------------------------------------------------
    // Entry filters: every filter pins the package's own values, so a matching entry can only come from
    // a rule carrying exactly those values (this fixture's own row, or a foreign row imported from the
    // same package into the shared dev database).
    // ---------------------------------------------------------------------------------------------

    private static List<ScheduleValidationNotificationDto> MyRollingAverageEntries(
        IEnumerable<ScheduleValidationNotificationDto> entries, RegionSetupPeriodCap cap) =>
        entries
            .Where(e => e.Comment == ScheduleValidationKeys.RollingAverage
                && e.CommentParams.GetValueOrDefault("windowWeeks") == cap.WindowWeeks!.Value.ToString(CultureInfo.InvariantCulture)
                && e.CommentParams.GetValueOrDefault("capHours") == cap.MaxAverageWeeklyHours!.Value.ToString("F0", CultureInfo.InvariantCulture))
            .ToList();

    private static List<ScheduleValidationNotificationDto> MyRestDayEntries(
        IEnumerable<ScheduleValidationNotificationDto> entries, RegionSetupRestDayRotation rotation) =>
        entries
            .Where(e => e.Comment == ScheduleValidationKeys.RestDayRotation
                && e.CommentParams.GetValueOrDefault("dayOfWeek") == Enum.Parse<DayOfWeek>(rotation.DayOfWeek!, ignoreCase: true).ToString()
                && e.CommentParams.GetValueOrDefault("minFree") == rotation.MinFree!.Value.ToString(CultureInfo.InvariantCulture)
                && e.CommentParams.GetValueOrDefault("windowWeeks") == rotation.WindowWeeks!.Value.ToString(CultureInfo.InvariantCulture))
            .ToList();

    private static List<ScheduleValidationNotificationDto> MyRestrictedWindowEntries(
        IEnumerable<ScheduleValidationNotificationDto> entries, RegionSetupRestrictedTimeWindow window) =>
        entries
            .Where(e => e.Comment == ScheduleValidationKeys.RestrictedTimeWindow
                && e.CommentParams.GetValueOrDefault("dailyStart") == window.DailyStart
                && e.CommentParams.GetValueOrDefault("dailyEnd") == window.DailyEnd
                && e.CommentParams.GetValueOrDefault("seasonFrom") == window.SeasonFrom
                && e.CommentParams.GetValueOrDefault("seasonTo") == window.SeasonTo
                && e.CommentParams.GetValueOrDefault("groupTag") == (window.AppliesToGroupTag ?? string.Empty))
            .ToList();

    private Task<List<ScheduleValidationNotificationDto>> EvaluatePlannedHoursAsync(
        IPeriodCapEvaluator evaluator, decimal hours, Guid? clientId = null)
    {
        var target = clientId ?? _clientId;
        return evaluator.EvaluatePlannedAsync(target, TestMarker, [(BaseDate, hours)]);
    }

    private Task<List<ScheduleValidationNotificationDto>> EvaluatePlannedSlotAsync(
        IRestrictedTimeWindowEvaluator evaluator, DateOnly date, TimeOnly start, TimeOnly end, Guid shiftId)
    {
        return evaluator.EvaluatePlannedAsync(_clientId, TestMarker, [(date, start, end, (Guid?)shiftId)]);
    }

    private static void ShouldMatchPackageSeverity(
        ScheduleValidationNotificationDto entry, string packageMode, string complianceRuleName)
    {
        if (string.Equals(packageMode, BlockValue, StringComparison.OrdinalIgnoreCase))
        {
            entry.Type.ShouldBe(ScheduleValidationType.Error,
                $"the package puts {complianceRuleName} in block mode, so the breach must be an error");
            entry.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(complianceRuleName);
        }
        else
        {
            entry.Type.ShouldBe(ScheduleValidationType.Warning,
                $"the package puts {complianceRuleName} in warn mode, so the breach must stay a warning");
            entry.CommentParams.ContainsKey(ComplianceRuleNames.EnforcementRuleParamKey).ShouldBeFalse();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Verbatim seeding. The production import path (RegionSetupService.ApplyEntityImportsAsync) is
    // deliberately NOT used, for the same reasons RegionPackageEffectivenessTests documents: it wraps its
    // work in ExecuteInTransactionAsync and commits, so it cannot be enclosed in the test transaction that
    // protects the shared dev database, and its rows would carry real package keys that must never be
    // cleaned up by name (incident 2026-07-03). The field mapping below is the same one
    // ApplyPeriodCapDecisions / ApplyRestDayRotationDecisions / ApplyRestrictedTimeWindowDecisions use.
    // ---------------------------------------------------------------------------------------------

    private async Task SeedRollingAverageRuleAsync(RegionSetupPeriodCap cap, string countryCode, Guid? schedulingRuleId)
    {
        // BuildRollingAverageDesired maps a rolling entry onto placeholder Period/Scope/CapHours values and
        // carries only WindowWeeks/MaxAverageWeeklyHours; mirroring that keeps the row a real import result.
        _context.PeriodCapRule.Add(new PeriodCapRule
        {
            Id = Guid.NewGuid(),
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = 0m,
            WarnAtPercent = cap.WarnAtPercent,
            CustomPeriodWeeks = null,
            RollingWindowWeeks = cap.WindowWeeks,
            MaxAverageWeeklyHours = cap.MaxAverageWeeklyHours,
            SchedulingRuleId = schedulingRuleId,
            ImportSourceKey = $"{TestMarker}{countryCode}:rolling:{cap.WindowWeeks}w",
            ImportContentHash = string.Empty,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedRestDayRotationRuleAsync(
        RegionSetupRestDayRotation rotation, string countryCode, Guid? schedulingRuleId)
    {
        _context.RestDayRotationRule.Add(new RestDayRotationRule
        {
            Id = Guid.NewGuid(),
            DayOfWeek = Enum.Parse<DayOfWeek>(rotation.DayOfWeek!, ignoreCase: true),
            MinFreeCount = rotation.MinFree!.Value,
            WindowWeeks = rotation.WindowWeeks!.Value,
            SchedulingRuleId = schedulingRuleId,
            ImportSourceKey = $"{TestMarker}{countryCode}:{rotation.DayOfWeek}:{rotation.WindowWeeks}w",
            ImportContentHash = string.Empty,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedRestrictedTimeWindowRuleAsync(RegionSetupRestrictedTimeWindow window, string countryCode)
    {
        var (fromMonth, fromDay) = ParseMonthDay(window.SeasonFrom!);
        var (toMonth, toDay) = ParseMonthDay(window.SeasonTo!);

        _context.RestrictedTimeWindowRule.Add(new RestrictedTimeWindowRule
        {
            Id = Guid.NewGuid(),
            SeasonFromMonth = fromMonth,
            SeasonFromDay = fromDay,
            SeasonToMonth = toMonth,
            SeasonToDay = toDay,
            DailyStart = ParseTimeOfDay(window.DailyStart!),
            DailyEnd = ParseTimeOfDay(window.DailyEnd!),
            AppliesToGroupTag = window.AppliesToGroupTag ?? string.Empty,
            ImportSourceKey = $"{TestMarker}{countryCode}:rtw:{window.SeasonFrom}:{window.DailyStart}",
            ImportContentHash = string.Empty,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedCompensatoryRestSettingsAsync(RegionSetupCompensatoryRest compensatoryRest)
    {
        // AddBool/AddInt of RegionSetupService write exactly these two string forms.
        await UpsertSettingAsync(SettingKeys.ComplianceCompensatoryRestEnabled,
            compensatoryRest.Enabled!.Value ? "true" : "false");
        await UpsertSettingAsync(SettingKeys.ComplianceCompensatoryRestDeadlineDays,
            compensatoryRest.DeadlineDays!.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Writes the package's enforcement configuration the way the importer does - the default mode always,
    /// the per-rule override only when the package actually ships one - and returns the mode the resolver
    /// must end up with for this rule.
    /// </summary>
    private async Task<string> SeedEnforcementAsync(RegionSetupProfile profile, string ruleSettingKey, string? perRuleValue)
    {
        var defaultMode = profile.Compliance?.Enforcement?.DefaultMode;
        defaultMode.ShouldNotBeNullOrWhiteSpace("the package must ship compliance.enforcement.defaultMode");
        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementDefaultMode, defaultMode!);

        if (!string.IsNullOrWhiteSpace(perRuleValue))
        {
            await UpsertSettingAsync(ruleSettingKey, perRuleValue!);
        }

        return perRuleValue ?? defaultMode!;
    }

    private async Task<(Guid RuleA, Guid RuleB)> SeedTwoIndustryRulesAsync(RegionSetupProfile profile)
    {
        var blocks = profile.IndustryProfiles!
            .Where(kv => kv.Value.SchedulingRulePresets.Count > 0)
            .Take(2)
            .ToList();
        blocks.Count.ShouldBe(2, "the industry-scoping proof needs two industry blocks with a preset");

        var ruleA = await SeedRuleFromPresetAsync(blocks[0].Value.SchedulingRulePresets[0], blocks[0].Key);
        var ruleB = await SeedRuleFromPresetAsync(blocks[1].Value.SchedulingRulePresets[0], blocks[1].Key);
        return (ruleA, ruleB);
    }

    private async Task<Guid> SeedRuleFromPresetAsync(RegionSetupSchedulingRulePreset preset, string industry)
    {
        var rule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = TestMarker + industry + preset.Name,
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

    // ---------------------------------------------------------------------------------------------
    // Fixture data
    // ---------------------------------------------------------------------------------------------

    private async Task<Guid> AddClientAsync(string name)
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = clientId,
            FirstName = TestMarker,
            Name = name,
            Type = EntityTypeEnum.Employee,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
        return clientId;
    }

    private async Task<Guid> AddShiftAsync(string suffix)
    {
        var shiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = shiftId,
            Name = TestMarker + suffix,
            Abbreviation = "ITCPE",
            StartShift = WorkStart,
            EndShift = WorkEnd,
            WorkTime = ShiftHours,
            FromDate = BaseDate.AddYears(-2),
            UntilDate = null,
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            IsSaturday = true,
            IsSunday = true,
            Quantity = 1,
            CuttingAfterMidnight = false,
            Status = ShiftStatus.OriginalOrder,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
        return shiftId;
    }

    /// <summary>
    /// Links a shift to a group whose NAME is the package's appliesToGroupTag verbatim - the evaluator
    /// matches the tag against Group.Name, so the marker cannot live there; it goes into Description
    /// instead and the always-rolled-back transaction remains the protection for this one row.
    /// </summary>
    private async Task LinkShiftToGroupTagAsync(Guid shiftId, string groupTag)
    {
        var groupId = Guid.NewGuid();
        _context.Group.Add(new Group
        {
            Id = groupId,
            Name = groupTag,
            Description = TestMarker + "GROUPTAG",
            ValidFrom = new DateTime(BaseDate.Year - 2, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = null,
            Root = null,
            IsDeleted = false,
        });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftId,
            ClientId = null,
            ValidFrom = null,
            ValidUntil = null,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Occupies the given days for a client in one round trip - the 52-week window of de.json needs
    /// dozens of rows, which must not become dozens of SaveChanges calls.
    /// </summary>
    private async Task<List<Guid>> AddWorkDaysAsync(Guid clientId, IEnumerable<DateOnly> days)
    {
        var ids = new List<Guid>();
        foreach (var day in days)
        {
            ids.Add(AddWork(clientId, day, WorkStart, WorkEnd));
        }

        await _context.SaveChangesAsync();
        return ids;
    }

    private async Task<Guid> AddWorkAsync(Guid clientId, DateOnly date, TimeOnly startTime, TimeOnly endTime)
    {
        var workId = AddWork(clientId, date, startTime, endTime);
        await _context.SaveChangesAsync();
        return workId;
    }

    private Guid AddWork(Guid clientId, DateOnly date, TimeOnly startTime, TimeOnly endTime)
    {
        var workId = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = workId,
            ClientId = clientId,
            CurrentDate = date,
            ShiftId = _shiftId,
            StartTime = startTime,
            EndTime = endTime,
            WorkTime = ShiftHours,
            ParentWorkId = null,
            AnalyseToken = null,
            IsDeleted = false,
        });
        return workId;
    }

    private async Task RemoveWorkAsync(Guid workId)
    {
        var work = await _context.Work.FirstAsync(w => w.Id == workId);
        _context.Work.Remove(work);
        await _context.SaveChangesAsync();
    }

    private async Task AssignContractAsync(Guid clientId, Guid? schedulingRuleId)
    {
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = TestMarker + "Contract",
            SchedulingRuleId = schedulingRuleId,
            ValidFrom = DateTime.SpecifyKind(BaseDate.AddYears(-2).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            IsDeleted = false,
        };
        _context.Contract.Add(contract);
        _context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contract.Id,
            FromDate = BaseDate.AddYears(-2),
            UntilDate = null,
            IsActive = true,
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

    // ---------------------------------------------------------------------------------------------
    // Package access and small parsers, mirroring RegionSetupService's own parsing of these fields.
    // ---------------------------------------------------------------------------------------------

    private static async Task<RegionSetupProfile> LoadPackageAsync(string countryCode)
    {
        var path = Path.Combine(RegionsDirectory(), $"{countryCode}.json");
        File.Exists(path).ShouldBeTrue($"country package {countryCode}.json must exist at {path}");
        return await RegionSetupFileReader.ReadProfileAsync(path);
    }

    private static RegionSetupPeriodCap RollingCapOf(RegionSetupProfile profile, string countryCode)
    {
        var caps = profile.Compliance?.PeriodCaps;
        caps.ShouldNotBeNull($"package {countryCode} must ship compliance.periodCaps for this test to mean anything");
        var rolling = caps!.FirstOrDefault(c => c.WindowWeeks.HasValue && c.MaxAverageWeeklyHours.HasValue);
        rolling.ShouldNotBeNull($"package {countryCode} is expected to ship a rolling-average period cap");
        return rolling!;
    }

    private static RegionSetupSchedulingRulePreset SinglePresetOf(RegionSetupProfile profile, string industry)
    {
        profile.IndustryProfiles.ShouldNotBeNull();
        profile.IndustryProfiles!.TryGetValue(industry, out var block).ShouldBeTrue($"package must ship the {industry} industry block");
        return block!.SchedulingRulePresets.ShouldHaveSingleItem($"the {industry} block is expected to ship exactly one preset");
    }

    private static (int Month, int Day) ParseMonthDay(string value)
    {
        var parts = value.Split('-');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static TimeOnly ParseTimeOfDay(string value) =>
        TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);

    private static List<DateOnly> OccurrencesFrom(DateOnly windowStart, int count) =>
        Enumerable.Range(0, count).Select(index => windowStart.AddDays(DaysPerWeek * index)).ToList();

    private static DateOnly MostRecentOccurrenceOnOrBefore(DayOfWeek dayOfWeek, DateOnly date)
    {
        var delta = ((int)date.DayOfWeek - (int)dayOfWeek + DaysPerWeek) % DaysPerWeek;
        return date.AddDays(-delta);
    }

    private static string RegionsDirectory([CallerFilePath] string callerFilePath = "")
    {
        var testProjectDirectory = Directory.GetParent(Path.GetDirectoryName(callerFilePath)!)!.FullName;
        var repositoryRoot = Directory.GetParent(testProjectDirectory)!.FullName;
        return Path.Combine(repositoryRoot, "Klacks.Api", "deploy", "onprem", "regions");
    }

    /// <summary>
    /// Composes the four evaluators and the compensatory-rest reconciler from their REAL implementations,
    /// all sharing the single transactional DbContext, so every read sees the uncommitted rule, work,
    /// obligation and setting rows. Only the dependencies the exercised read paths never touch are
    /// substituted: the SignalR work-notification service and the group filter service (both only used by
    /// write / group-scoped flows of PeriodHoursService).
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
        services.AddSingleton<ISettingsChangeVersion, SettingsChangeVersion>();
        services.AddSingleton<IClientContractDataProvider, ClientContractDataProvider>();
        services.AddSingleton<ISchedulingPolicyResolver, SchedulingPolicyResolver>();
        services.AddSingleton<IComplianceEnforcementResolver, ComplianceEnforcementResolver>();
        services.AddSingleton<IOvertimeConfigResolver, OvertimeConfigResolver>();
        services.AddSingleton<IClientWorkHoursProvider, ClientWorkHoursProvider>();
        services.AddSingleton<IClientMembershipStartResolver, ClientMembershipStartResolver>();
        services.AddSingleton<IWeekConfiguration, Klacks.Api.Infrastructure.Services.WeekConfiguration>();
        services.AddSingleton<IPeriodCapRuleRepository, PeriodCapRuleRepository>();
        services.AddSingleton<IRestDayRotationRuleRepository, RestDayRotationRuleRepository>();
        services.AddSingleton<IRestrictedTimeWindowRuleRepository, RestrictedTimeWindowRuleRepository>();
        services.AddSingleton<ICompensatoryRestObligationRepository, CompensatoryRestObligationRepository>();
        services.AddSingleton<IPeriodHoursService, PeriodHoursService>();
        services.AddSingleton<IPeriodCapEvaluator, PeriodCapEvaluator>();
        services.AddSingleton<IRestDayRotationEvaluator, RestDayRotationEvaluator>();
        services.AddSingleton<IRestrictedTimeWindowEvaluator, RestrictedTimeWindowEvaluator>();
        services.AddSingleton<ICompensatoryRestEvaluator, CompensatoryRestEvaluator>();
        services.AddSingleton<ICompensatoryRestObligationReconciler, CompensatoryRestObligationReconciler>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Minimal ISettingsReader over the SAME transactional DbContext, so the enforcement resolver and the
    /// compensatory-rest gate read the uncommitted setting values written by the test.
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
