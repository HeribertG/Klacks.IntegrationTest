// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
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
/// The stable refusal codes of a work booking, end to end through the real handlers against Postgres: a sealed day
/// answers DAY_SEALED, a day outside the membership OUTSIDE_MEMBERSHIP_PERIOD, and a missing mandatory qualification
/// WORK_BLOCKED_BY_CONFLICTS on the create, update, bulk and reassignment paths. Status class and English message stay
/// what they were before the codes existed, and nothing is written.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class WorkBookingRefusalCodeNpgsqlTests
{
    private const string SeedPrefix = "INTEGRATION_TEST_RefusalCode_";
    private static readonly TimeOnly ShiftStart = new(8, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0);
    private const decimal SeedWorkTime = 8m;

    private SignalRTestWebApplicationFactory _factory = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    private Guid _clientId;
    private Guid _targetClientId;
    private Guid _openShiftId;
    private Guid _qualifiedShiftId;
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
        _targetClientId = Guid.NewGuid();
        _openShiftId = Guid.NewGuid();
        _qualifiedShiftId = Guid.NewGuid();
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
    public async Task Create_OnASealedDay_IsRefusedWithDaySealed_AndTheUnchangedText()
    {
        await SeedGlobalSealAsync(_date, WorkLockLevel.Closed);

        var refusal = await Should.ThrowAsync<WorkBookingRefusedException>(
            () => SendAsync(new PostCommand<WorkResource>(NewResource(_clientId, _openShiftId))));

        refusal.ShouldBeAssignableTo<InvalidRequestException>();
        refusal.ErrorCode.ShouldBe(WorkBookingRefusalCodes.DaySealed);
        refusal.Message.ShouldBe($"Day {_date:yyyy-MM-dd} is sealed and cannot be modified.");
        refusal.Details[WorkBookingRefusalCodes.DateField].ShouldBe(_date);
        (await CountWorksAsync(_clientId)).ShouldBe(0);
    }

    [Test]
    public async Task Bulk_OnADayApproval_IsRefusedWithDaySealed()
    {
        await SeedGlobalSealAsync(_date, WorkLockLevel.Approved);

        var refusal = await Should.ThrowAsync<WorkBookingRefusedException>(
            () => SendAsync(BulkCommand(_clientId, _openShiftId)));

        refusal.ErrorCode.ShouldBe(WorkBookingRefusalCodes.DaySealed);
        (await CountWorksAsync(_clientId)).ShouldBe(0);
    }

    [Test]
    public async Task Create_AfterTheMembershipEnded_IsRefusedWithOutsideMembership_AndTheBounds()
    {
        var from = _date.AddDays(-60);
        var until = _date.AddDays(-1);
        await SeedMembershipAsync(_clientId, from, until);

        var refusal = await Should.ThrowAsync<WorkBookingRefusedException>(
            () => SendAsync(new PostCommand<WorkResource>(NewResource(_clientId, _openShiftId))));

        refusal.ErrorCode.ShouldBe(WorkBookingRefusalCodes.OutsideMembershipPeriod);
        refusal.Message.ShouldContain("outside the membership period");
        refusal.Details[WorkBookingRefusalCodes.ClientIdField].ShouldBe(_clientId);
        refusal.Details[WorkBookingRefusalCodes.DateField].ShouldBe(_date);
        refusal.Details[WorkBookingRefusalCodes.FromField].ShouldBe(from);
        refusal.Details[WorkBookingRefusalCodes.UntilField].ShouldBe(until);
        (await CountWorksAsync(_clientId)).ShouldBe(0);
    }

    [Test]
    public async Task Reassign_ToAClientOutsideItsMembership_IsRefusedWithOutsideMembership()
    {
        var workId = await SeedWorkAsync(_clientId, _openShiftId);
        await SeedMembershipAsync(_targetClientId, _date.AddDays(1), null);

        var refusal = await Should.ThrowAsync<WorkBookingRefusedException>(
            () => SendAsync(new ReassignWorkClientCommand(workId, _targetClientId)));

        refusal.ErrorCode.ShouldBe(WorkBookingRefusalCodes.OutsideMembershipPeriod);
        refusal.Details[WorkBookingRefusalCodes.UntilField].ShouldBeNull();
        (await CountWorksAsync(_targetClientId)).ShouldBe(0);
    }

    [Test]
    public async Task Create_WithoutTheMandatoryQualification_IsRefusedWithBlockedByConflicts()
    {
        var refusal = await Should.ThrowAsync<WorkWriteConflictException>(
            () => SendAsync(new PostCommand<WorkResource>(NewResource(_clientId, _qualifiedShiftId))));

        AssertBlocked(refusal, $"Work blocked: client {_clientId} would introduce ");
        refusal.Details[WorkWriteConflictCodes.ShiftIdField].ShouldBe(_qualifiedShiftId);
        (await CountWorksAsync(_clientId)).ShouldBe(0);
    }

    [Test]
    public async Task Update_OntoAShiftNeedingAMissingMandatoryQualification_IsRefusedWithBlockedByConflicts()
    {
        var workId = await SeedWorkAsync(_clientId, _openShiftId);
        var moved = NewResource(_clientId, _qualifiedShiftId);
        moved.Id = workId;

        var refusal = await Should.ThrowAsync<WorkWriteConflictException>(
            () => SendAsync(new PutCommand<WorkResource>(moved)));

        AssertBlocked(refusal, $"Work update blocked: client {_clientId} would introduce ");
        var stored = await _context.Work.AsNoTracking().SingleAsync(w => w.Id == workId);
        stored.ShiftId.ShouldBe(_openShiftId);
    }

    [Test]
    public async Task Bulk_WithoutTheMandatoryQualification_IsRefusedWithBlockedByConflicts()
    {
        var refusal = await Should.ThrowAsync<WorkWriteConflictException>(
            () => SendAsync(BulkCommand(_clientId, _qualifiedShiftId)));

        refusal.ErrorCode.ShouldBe(WorkWriteConflictCodes.BlockedByConflicts);
        refusal.Message.ShouldStartWith("Bulk write blocked: at least one row would introduce ");
        refusal.Message.ShouldEndWith("non-overridable schedule conflict(s). Nothing was committed.");
        refusal.Details[WorkWriteConflictCodes.ShiftIdField].ShouldBe(_qualifiedShiftId);
        refusal.Details[WorkWriteConflictCodes.ConflictsField].ShouldBeOfType<List<WorkConflictItem>>().ShouldNotBeEmpty();
        (await CountWorksAsync(_clientId)).ShouldBe(0);
    }

    [Test]
    public async Task Reassign_ToAClientWithoutTheMandatoryQualification_IsRefusedWithBlockedByConflicts()
    {
        var workId = await SeedWorkAsync(_clientId, _qualifiedShiftId);

        var refusal = await Should.ThrowAsync<WorkWriteConflictException>(
            () => SendAsync(new ReassignWorkClientCommand(workId, _targetClientId)));

        AssertBlocked(refusal, $"Reassignment blocked: client {_targetClientId} would introduce ");
        (await CountWorksAsync(_targetClientId)).ShouldBe(0);
    }

    private void AssertBlocked(WorkWriteConflictException refusal, string expectedStart)
    {
        refusal.ShouldBeAssignableTo<ConflictException>();
        refusal.ErrorCode.ShouldBe(WorkWriteConflictCodes.BlockedByConflicts);
        refusal.Message.ShouldStartWith(expectedStart);
        refusal.Message.ShouldEndWith($"non-overridable schedule conflict(s) on {_date:yyyy-MM-dd}. Not committed.");
        refusal.Details[WorkWriteConflictCodes.DateField].ShouldBe(_date);
        refusal.Details[WorkWriteConflictCodes.ConflictsField].ShouldBeOfType<List<WorkConflictItem>>().ShouldNotBeEmpty();
    }

    private async Task SendAsync<TResponse>(IRequest<TResponse> request)
    {
        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(request, CancellationToken.None);
    }

    private WorkResource NewResource(Guid clientId, Guid shiftId) => new()
    {
        ClientId = clientId,
        ShiftId = shiftId,
        CurrentDate = _date,
        StartTime = ShiftStart,
        EndTime = ShiftEnd,
        WorkTime = SeedWorkTime,
    };

    private BulkAddWorksCommand BulkCommand(Guid clientId, Guid shiftId) => new(new BulkAddWorksRequest
    {
        PeriodStart = _date,
        PeriodEnd = _date,
        Works = new List<BulkWorkItem>
        {
            new()
            {
                ClientId = clientId,
                ShiftId = shiftId,
                CurrentDate = _date,
                StartTime = ShiftStart,
                EndTime = ShiftEnd,
                WorkTime = SeedWorkTime,
            },
        },
    });

    private Task<int> CountWorksAsync(Guid clientId) => _context.Work
        .AsNoTracking()
        .CountAsync(w => w.ClientId == clientId && w.CurrentDate == _date && w.AnalyseToken == null);

    private async Task SeedBaseFixtureAsync()
    {
        _context.Client.Add(new Client { Id = _clientId, Name = SeedPrefix + "Client", FirstName = "Integration" });
        _context.Client.Add(new Client { Id = _targetClientId, Name = SeedPrefix + "Target", FirstName = "Integration" });

        _context.Shift.Add(new Shift
        {
            Id = _openShiftId,
            Name = SeedPrefix + "OpenShift",
            StartShift = ShiftStart,
            EndShift = ShiftEnd,
        });
        _context.Shift.Add(new Shift
        {
            Id = _qualifiedShiftId,
            Name = SeedPrefix + "QualifiedShift",
            StartShift = ShiftStart,
            EndShift = ShiftEnd,
        });

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

        _context.ShiftRequiredQualification.Add(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = _qualifiedShiftId,
            QualificationId = _qualificationId,
            IsMandatory = true,
            MinLevel = QualificationLevel.Basic,
        });

        foreach (var clientId in new[] { _clientId, _targetClientId })
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

    private async Task<Guid> SeedWorkAsync(Guid clientId, Guid shiftId)
    {
        var id = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = _date,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = SeedWorkTime,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
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

    private async Task SeedGlobalSealAsync(DateOnly date, WorkLockLevel level)
    {
        var seal = new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = date,
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
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN ({0}, {1}))",
            _clientId, _targetClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN ({0}, {1}))",
            _clientId, _targetClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE client_id IN ({0}, {1})", _clientId, _targetClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_period_hours WHERE client_id IN ({0}, {1})", _clientId, _targetClientId);
        foreach (var sealId in _sealedDayIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM sealed_day WHERE id = {0}", sealId);
        }

        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM membership WHERE client_id IN ({0}, {1})", _clientId, _targetClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_contract WHERE client_id IN ({0}, {1})", _clientId, _targetClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift_required_qualification WHERE shift_id IN ({0}, {1})", _openShiftId, _qualifiedShiftId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM qualification WHERE id = {0}", _qualificationId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id IN ({0}, {1})", _openShiftId, _qualifiedShiftId);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM contract WHERE id = {0}", _contractId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id IN ({0}, {1})", _clientId, _targetClientId);
    }
}
