// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

/// <summary>
/// The booking diagnosis and the real booking must never disagree. Per refusal reason the diagnosis runs first and then
/// the real Works POST, through the real DI container against Postgres: the diagnosis' direct-entry code equals the
/// errorCode of the POST's refusal (day approval, period close, membership end, sporadic day full and range exhausted,
/// missing mandatory qualification, binding level too low), and "no direct-entry refusal" equals a successful POST
/// (nothing refuses; a non-binding level gap, which only warns; a collision, which only the assistant refuses). The
/// group-period code needs a restricted caller and is covered by WorkBookingDiagnosisVisibilityNpgsqlTests.
/// WORK_IN_CLOSED_PERIOD and WORK_SEALED_ROLE_REQUIRED can never come from a POST (they guard an existing work).
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class WorkBookingDiagnosisConsistencyNpgsqlTests
{
    private const string SeedPrefix = "INTEGRATION_TEST_DiagnosisParity_";
    private const string Booked = "BOOKED";
    private static readonly TimeOnly ShiftStart = new(8, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0);
    private const decimal SeedWorkTime = 8m;

    private SignalRTestWebApplicationFactory _factory = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    private Guid _clientId;
    private Guid _otherClientId;
    private Guid _openShiftId;
    private Guid _secondOpenShiftId;
    private Guid _qualifiedShiftId;
    private Guid _bindingLevelShiftId;
    private Guid _hintLevelShiftId;
    private Guid _sporadicDayShiftId;
    private Guid _sporadicRangeShiftId;
    private Guid _qualificationId;
    private Guid _contractId;
    private DateOnly _date;
    private readonly List<Guid> _sealedDayIds = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _clientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();
        _openShiftId = Guid.NewGuid();
        _secondOpenShiftId = Guid.NewGuid();
        _qualifiedShiftId = Guid.NewGuid();
        _bindingLevelShiftId = Guid.NewGuid();
        _hintLevelShiftId = Guid.NewGuid();
        _sporadicDayShiftId = Guid.NewGuid();
        _sporadicRangeShiftId = Guid.NewGuid();
        _qualificationId = Guid.NewGuid();
        _contractId = Guid.NewGuid();
        _sealedDayIds.Clear();

        // Far-future day per test: no clash with a real SealedDay and its global partial-unique index.
        _date = new DateOnly(2200, 1, 1).AddDays(Random.Shared.Next(0, 30000));

        await SeedBaseFixtureAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        _context?.Dispose();
    }

    [Test]
    public async Task NothingRefuses_DiagnosisSaysBookable_AndThePostSaves()
    {
        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_openShiftId);

        diagnosis.Outcome.ShouldBe(WorkBookingDiagnosisOutcome.Bookable);
        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(Booked);
    }

    [TestCase(WorkLockLevel.Closed, WorkBookingSealKind.PeriodClosed, WorkBookingRefusalReason.PeriodClosed)]
    [TestCase(WorkLockLevel.Approved, WorkBookingSealKind.DayApproval, WorkBookingRefusalReason.DayApproved)]
    public async Task SealedDay_DiagnosisCodeEqualsThePostCode(
        WorkLockLevel level, WorkBookingSealKind kind, WorkBookingRefusalReason reason)
    {
        await SeedGlobalSealAsync(level);

        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_openShiftId);

        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(WorkBookingRefusalCodes.DaySealed);
        diagnosis.Refusals[0].Reason.ShouldBe(reason);
        diagnosis.Refusals[0].Details[WorkBookingRefusalCodes.SealLevelField].ShouldBe(kind);
    }

    [Test]
    public async Task MembershipEnded_DiagnosisCodeEqualsThePostCode()
    {
        await SeedMembershipAsync(_clientId, _date.AddDays(-60), _date.AddDays(-1));

        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_openShiftId);

        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(WorkBookingRefusalCodes.OutsideMembershipPeriod);
    }

    [Test]
    public async Task SporadicDayFull_DiagnosisCodeEqualsThePostCode()
    {
        await SeedWorkAsync(_otherClientId, _sporadicDayShiftId, _date);

        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_sporadicDayShiftId);

        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(WorkWriteConflictCodes.SporadicShiftDayFull);
    }

    [Test]
    public async Task SporadicRangeExhausted_DiagnosisCodeEqualsThePostCode()
    {
        var otherDay = _date.DayOfYear > 1 ? _date.AddDays(-1) : _date.AddDays(1);
        await SeedWorkAsync(_otherClientId, _sporadicRangeShiftId, otherDay);

        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_sporadicRangeShiftId);

        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(WorkWriteConflictCodes.SporadicShiftRangeExhausted);
    }

    [Test]
    public async Task MissingMandatoryQualification_DiagnosisCodeEqualsThePostCode()
    {
        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_qualifiedShiftId);

        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(WorkWriteConflictCodes.BlockedByConflicts);
        diagnosis.Refusals.Single().Channels.Assistant.ShouldBeTrue();
    }

    [Test]
    public async Task BindingLevelTooLow_DiagnosisCodeEqualsThePostCode()
    {
        await SeedHeldQualificationAsync(QualificationLevel.Basic);

        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_bindingLevelShiftId);

        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(WorkWriteConflictCodes.BlockedByConflicts);
    }

    [Test]
    public async Task NonBindingLevelTooLow_OnlyWarns_AndThePostSaves()
    {
        await SeedHeldQualificationAsync(QualificationLevel.Basic);

        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_hintLevelShiftId);

        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(Booked);
        diagnosis.Outcome.ShouldBe(WorkBookingDiagnosisOutcome.Bookable);
    }

    [Test]
    public async Task Collision_OnlyTheAssistantRefuses_AndThePostSaves()
    {
        await SeedWorkAsync(_clientId, _secondOpenShiftId, _date);

        var (diagnosis, postCode) = await DiagnoseThenPostAsync(_openShiftId);

        AssertSameVerdict(diagnosis, postCode);
        postCode.ShouldBe(Booked);
        diagnosis.Refusals.ShouldContain(r => r.Reason == WorkBookingRefusalReason.AssistantOnlyConflict && r.Channels.Assistant);
        diagnosis.Refusals.ShouldContain(r => r.Reason == WorkBookingRefusalReason.CellOccupiedByWork && r.Channels.Grid);
    }

    private static void AssertSameVerdict(WorkBookingDiagnosis diagnosis, string postCode)
    {
        diagnosis.Outcome.ShouldNotBe(WorkBookingDiagnosisOutcome.NotFound);
        (diagnosis.DirectEntryCode ?? Booked).ShouldBe(postCode);
    }

    private async Task<(WorkBookingDiagnosis Diagnosis, string PostCode)> DiagnoseThenPostAsync(Guid shiftId)
    {
        WorkBookingDiagnosis diagnosis;
        using (var scope = _factory.Services.CreateScope())
        {
            diagnosis = await scope.ServiceProvider.GetRequiredService<IWorkBookingDiagnosisService>()
                .DiagnoseAsync(new WorkBookingDiagnosisRequest(_clientId, shiftId, _date, null), CancellationToken.None);
        }

        using var postScope = _factory.Services.CreateScope();
        try
        {
            await postScope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new PostCommand<WorkResource>(NewResource(shiftId)), CancellationToken.None);
            return (diagnosis, Booked);
        }
        catch (WorkBookingRefusedException refused)
        {
            return (diagnosis, refused.ErrorCode);
        }
        catch (WorkWriteConflictException conflict)
        {
            return (diagnosis, conflict.ConflictCode!);
        }
    }

    private WorkResource NewResource(Guid shiftId) => new()
    {
        ClientId = _clientId,
        ShiftId = shiftId,
        CurrentDate = _date,
        StartTime = ShiftStart,
        EndTime = ShiftEnd,
        WorkTime = SeedWorkTime,
    };

    private async Task SeedBaseFixtureAsync()
    {
        _context.Client.Add(new Client { Id = _clientId, Name = SeedPrefix + "Client", FirstName = "Integration" });
        _context.Client.Add(new Client { Id = _otherClientId, Name = SeedPrefix + "Other", FirstName = "Integration" });

        _context.Shift.AddRange(
            NewShift(_openShiftId, "Open"),
            NewShift(_secondOpenShiftId, "SecondOpen"),
            NewShift(_qualifiedShiftId, "Qualified"),
            NewShift(_bindingLevelShiftId, "BindingLevel"),
            NewShift(_hintLevelShiftId, "HintLevel"),
            NewSporadicShift(_sporadicDayShiftId, "SporadicDay", ShiftSporadic.Week, quantity: 7),
            NewSporadicShift(_sporadicRangeShiftId, "SporadicRange", ShiftSporadic.Year, quantity: 1));

        _context.Qualification.Add(new Qualification
        {
            Id = _qualificationId,
            Name = new MultiLanguage { De = SeedPrefix + "FirstAid" },
        });

        _context.Set<Contract>().Add(new Contract
        {
            Id = _contractId,
            Name = SeedPrefix + "Contract",
            GuaranteedHours = 168m,
            FullTime = 100m,
            ValidFrom = new DateTime(2199, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        await _context.SaveChangesAsync();

        _context.ShiftRequiredQualification.AddRange(
            Requirement(_qualifiedShiftId, QualificationLevel.Basic, levelBinding: null),
            Requirement(_bindingLevelShiftId, QualificationLevel.Advanced, levelBinding: true),
            Requirement(_hintLevelShiftId, QualificationLevel.Advanced, levelBinding: false));

        foreach (var clientId in new[] { _clientId, _otherClientId })
        {
            _context.Set<ClientContract>().Add(new ClientContract
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ContractId = _contractId,
                FromDate = new DateOnly(2199, 1, 1),
                IsActive = true,
            });
        }

        await _context.SaveChangesAsync();
    }

    private static Shift NewShift(Guid id, string suffix) => new()
    {
        Id = id,
        Name = SeedPrefix + suffix,
        StartShift = ShiftStart,
        EndShift = ShiftEnd,
    };

    private static Shift NewSporadicShift(Guid id, string suffix, ShiftSporadic scope, int quantity)
    {
        var shift = NewShift(id, suffix);
        shift.IsSporadic = true;
        shift.SporadicScope = scope;
        shift.SumEmployees = 1;
        shift.Quantity = quantity;
        return shift;
    }

    private ShiftRequiredQualification Requirement(Guid shiftId, QualificationLevel minLevel, bool? levelBinding) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shiftId,
        QualificationId = _qualificationId,
        IsMandatory = true,
        MinLevel = minLevel,
        LevelBindingOverride = levelBinding,
    };

    private async Task SeedHeldQualificationAsync(QualificationLevel level)
    {
        _context.ClientQualification.Add(new ClientQualification
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            QualificationId = _qualificationId,
            Level = level,
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedWorkAsync(Guid clientId, Guid shiftId, DateOnly date)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = SeedWorkTime,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task SeedMembershipAsync(Guid clientId, DateOnly from, DateOnly? until)
    {
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ValidUntil = until?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedGlobalSealAsync(WorkLockLevel level)
    {
        var seal = new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = _date,
            GroupId = null,
            Level = level,
            Reason = SeedPrefix + "Seal",
            SealedAt = DateTime.UtcNow,
            SealedBy = SeedPrefix + "Test",
        };
        _context.SealedDay.Add(seal);
        await _context.SaveChangesAsync();
        _sealedDayIds.Add(seal.Id);
    }

    private async Task CleanupAsync()
    {
        var clients = new object[] { _clientId, _otherClientId };
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN ({0}, {1}))", clients);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN ({0}, {1}))", clients);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM work WHERE client_id IN ({0}, {1})", clients);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client_period_hours WHERE client_id IN ({0}, {1})", clients);
        foreach (var sealId in _sealedDayIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM sealed_day WHERE id = {0}", sealId);
        }

        await _context.Database.ExecuteSqlRawAsync("DELETE FROM membership WHERE client_id IN ({0}, {1})", clients);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client_contract WHERE client_id IN ({0}, {1})", clients);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client_qualification WHERE client_id IN ({0}, {1})", clients);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift_required_qualification WHERE qualification_id = {0}", _qualificationId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM qualification WHERE id = {0}", _qualificationId);
        foreach (var shiftId in new[]
                 {
                     _openShiftId, _secondOpenShiftId, _qualifiedShiftId, _bindingLevelShiftId, _hintLevelShiftId,
                     _sporadicDayShiftId, _sporadicRangeShiftId,
                 })
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE id = {0}", shiftId);
        }

        await _context.Database.ExecuteSqlRawAsync("DELETE FROM contract WHERE id = {0}", _contractId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM client WHERE id IN ({0}, {1})", clients);
    }
}
