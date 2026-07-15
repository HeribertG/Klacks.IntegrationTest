// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration test for the WP-P4.2 eligibility guardrail wired into the REAL PreCommitConflictChecker
/// against the real database. Three load-bearing properties: (1) the OPT-IN no-op — a shift with no
/// required qualifications produces no qualification conflict and remains placeable (this is why the
/// change is safe for the 2000+ existing flows); (2) a missing mandatory qualification blocks; (3) a
/// held, in-window, sufficient-level qualification does not block. Only the policy resolver is stubbed.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
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

namespace Klacks.IntegrationTest.Qualifications;

[TestFixture]
[Category("RealDatabase")]
public class EligibilityGuardrailTests
{
    private const string TestPrefix = "INTEGRATION_TEST_ELIG_";
    private static readonly DateOnly WorkDate = new(2099, 7, 6);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
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

        _checker = new PreCommitConflictChecker(_context, timeline, resolver, enforcementResolver, settingsReader, periodCapEvaluator, restDayRotationEvaluator, counterRuleEvaluator);
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
            DELETE FROM client_qualification WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_required_qualification WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync()
    {
        var c = new Client { Id = Guid.NewGuid(), Name = TestPrefix + "CLIENT", FirstName = "Test", Company = string.Empty, LegalEntity = false };
        await _context.Set<Client>().AddAsync(c);
        await _context.SaveChangesAsync();
        return c;
    }

    private async Task<Shift> CreateShiftAsync()
    {
        var s = new Shift
        {
            Id = Guid.NewGuid(), Name = TestPrefix + "SHIFT", Abbreviation = "TST", Description = "Eligibility test",
            Status = ShiftStatus.OriginalShift, FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0),
            ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 8m
        };
        await _context.Shift.AddAsync(s);
        await _context.SaveChangesAsync();
        return s;
    }

    private async Task<Qualification> CreateQualificationAsync()
    {
        var q = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = TestPrefix + "FIRSTAID" } };
        await _context.Set<Qualification>().AddAsync(q);
        await _context.SaveChangesAsync();
        return q;
    }

    private async Task RequireAsync(Guid shiftId, Guid qualId, QualificationLevel min, bool mandatory = true)
    {
        await _context.Set<ShiftRequiredQualification>().AddAsync(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = shiftId, QualificationId = qualId, IsMandatory = mandatory, MinLevel = min
        });
        await _context.SaveChangesAsync();
    }

    private async Task HoldAsync(Guid clientId, Guid qualId, QualificationLevel level, DateOnly? from = null, DateOnly? until = null)
    {
        await _context.Set<ClientQualification>().AddAsync(new ClientQualification
        {
            Id = Guid.NewGuid(), ClientId = clientId, QualificationId = qualId, Level = level, ValidFrom = from, ValidUntil = until
        });
        await _context.SaveChangesAsync();
    }

    private async Task<PreCommitCheckResult> CheckAsync(Guid clientId, Guid shiftId)
    {
        var row = new PlannedWorkRow(clientId, WorkDate, new TimeOnly(8, 0), new TimeOnly(16, 0), shiftId);
        return await _checker.CheckAsync(new[] { row }, null, CancellationToken.None);
    }

    [Test]
    public async Task NoRequiredQualifications_NoConflict_PlacementAllowed()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();

        var result = await CheckAsync(client.Id, shift.Id);

        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldNotContain(c => c.Comment.StartsWith("schedule.error-list.qualification"));
    }

    [Test]
    public async Task MissingMandatoryQualification_Blocks()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();
        var qual = await CreateQualificationAsync();
        await RequireAsync(shift.Id, qual.Id, QualificationLevel.Proficient);

        var result = await CheckAsync(client.Id, shift.Id);

        result.HasBlocking.ShouldBeTrue();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == QualificationValidationKeys.Missing && c.Type == ScheduleValidationType.Error);
    }

    [Test]
    public async Task HeldSufficientInWindowQualification_DoesNotBlock()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();
        var qual = await CreateQualificationAsync();
        await RequireAsync(shift.Id, qual.Id, QualificationLevel.Proficient);
        await HoldAsync(client.Id, qual.Id, QualificationLevel.Advanced, from: new(2099, 1, 1), until: new(2099, 12, 31));

        var result = await CheckAsync(client.Id, shift.Id);

        result.NewConflicts.ShouldNotContain(c => c.Comment.StartsWith("schedule.error-list.qualification"));
        result.HasBlocking.ShouldBeFalse();
    }

    [Test]
    public async Task ExpiredQualification_Blocks()
    {
        var client = await CreateClientAsync();
        var shift = await CreateShiftAsync();
        var qual = await CreateQualificationAsync();
        await RequireAsync(shift.Id, qual.Id, QualificationLevel.Proficient);
        await HoldAsync(client.Id, qual.Id, QualificationLevel.Expert, from: new(2098, 1, 1), until: new(2099, 1, 1));

        var result = await CheckAsync(client.Id, shift.Id);

        result.NewConflicts.ShouldContain(c =>
            c.Comment == QualificationValidationKeys.Expired && c.Type == ScheduleValidationType.Error);
    }
}
