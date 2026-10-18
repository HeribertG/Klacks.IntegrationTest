// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.WorkSchedule;

/// <summary>
/// The booking diagnosis at the visibility boundary, through the real DI container and the real group visibility
/// pipeline against Postgres, run as a Supervisor who sees one group (SupervisorVisibilityScenario): an employee of the
/// hidden group and a shift of the hidden group read exactly like missing ones (also on a sealed day, nothing of the
/// seal shows), and the real POST refuses them as "not found" too; a work on a hidden shift that occupies the cell is
/// reported without its shift name; the group-period refusal carries the same code as the real POST; the skill resolves
/// a visible shift by name through the Npgsql query and answers a hidden employee exactly like an unknown one, without
/// any trace of the hidden person; more hidden same-named shifts than the name search returns, all ordered before the
/// visible one, do not push the visible one out (the visibility filter runs in the query before its limit).
/// The hidden/unknown name comparison does not pin WHICH answer comes back: the name search falls back to the fuzzy
/// ranking, which shares the fixture prefix and the first name "Test" with every other client, so the answer depends on
/// the visible clients in the shared database (a fresh CI database resolves the VISIBLE fixture client, a database with
/// leftover "Test" clients answers with an ambiguity list). What must hold in every database: the hidden name and an
/// unknown name get the same verdict and the same text, and the hidden client's id and number never appear.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class WorkBookingDiagnosisVisibilityNpgsqlTests
{
    private const string Prefix = "INTEGRATION_TEST_DiagnosisVisibility_";
    private const string VisibleShiftSuffix = "VisibleShift";
    private const string HiddenShiftSuffix = "HiddenShift";
    private const string TwinShiftSuffix = "TwinShift";
    private const string UnknownClientSuffix = "NOBODY";
    private const string QueriedNamePlaceholder = "<name>";
    private const int HiddenTwinCount = 21;
    private static readonly TimeOnly ShiftStart = new(8, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0);

    private RealVisibilityTestWebApplicationFactory _factory = null!;
    private DataBaseContext _context = null!;
    private SupervisorVisibilityScenario _scenario = null!;
    private Shift _visibleShift = null!;
    private Shift _hiddenShift = null!;
    private DateOnly _date;
    private readonly List<Guid> _sealedDayIds = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RealVisibilityTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
        _scenario = await SupervisorVisibilityScenario.CreateAsync(Prefix, _context);
        _date = new DateOnly(2200, 1, 1).AddDays(Random.Shared.Next(0, 30000));
        _sealedDayIds.Clear();

        _visibleShift = NewShift(VisibleShiftSuffix);
        _hiddenShift = NewShift(HiddenShiftSuffix);
        _context.Shift.AddRange(_visibleShift, _hiddenShift);
        await _context.SaveChangesAsync();
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _scenario.HiddenGroup.Id, ShiftId = _hiddenShift.Id });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task HiddenEmployee_IsNotFound_LikeAMissingOne_AndThePostRefusesItAsNotFound()
    {
        var hidden = await DiagnoseAsync(_scenario.HiddenClient.Id, _visibleShift.Id);
        var missing = await DiagnoseAsync(Guid.NewGuid(), _visibleShift.Id);

        hidden.ShouldBe(WorkBookingDiagnosis.NotFound);
        missing.ShouldBe(WorkBookingDiagnosis.NotFound);
        await Should.ThrowAsync<KeyNotFoundException>(() => PostAsync(_scenario.HiddenClient.Id, _visibleShift.Id));
    }

    [Test]
    public async Task HiddenShift_IsNotFound_EvenOnASealedDay_AndNothingOfTheSealShows()
    {
        await SeedGlobalSealAsync();

        var hidden = await DiagnoseAsync(_scenario.VisibleClient.Id, _hiddenShift.Id);
        var visible = await DiagnoseAsync(_scenario.VisibleClient.Id, _visibleShift.Id);

        hidden.ShouldBe(WorkBookingDiagnosis.NotFound);
        hidden.Refusals.ShouldBeEmpty();
        hidden.ShiftName.ShouldBeNull();
        visible.DirectEntryCode.ShouldBe(WorkBookingRefusalCodes.DaySealed);
        await Should.ThrowAsync<KeyNotFoundException>(() => PostAsync(_scenario.VisibleClient.Id, _hiddenShift.Id));
    }

    [Test]
    public async Task OccupyingWorkOnAHiddenShift_IsReportedWithoutItsShiftName()
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _scenario.VisibleClient.Id,
            ShiftId = _hiddenShift.Id,
            CurrentDate = _date,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = 8m,
        });
        await _context.SaveChangesAsync();

        var diagnosis = await DiagnoseAsync(_scenario.VisibleClient.Id, _visibleShift.Id);

        var occupied = diagnosis.Refusals.Single(r => r.Reason == WorkBookingRefusalReason.CellOccupiedByWork);
        occupied.Remedy.ShouldBe(WorkBookingRemedy.AskForGroupVisibility);
        occupied.Details.ShouldNotContainKey(WorkWriteConflictCodes.ShiftNameField);
        JsonSerializer.Serialize(diagnosis).ShouldNotContain(HiddenShiftSuffix);
    }

    [Test]
    public async Task OutsideTheGroupPeriod_DiagnosisCodeEqualsThePostCode()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE group_item SET valid_until = {0} WHERE client_id = {1} AND group_id = {2}",
            _date.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            _scenario.VisibleClient.Id,
            _scenario.VisibleGroup.Id);

        var diagnosis = await DiagnoseAsync(_scenario.VisibleClient.Id, _visibleShift.Id);
        var refusal = await Should.ThrowAsync<WorkBookingRefusedException>(
            () => PostAsync(_scenario.VisibleClient.Id, _visibleShift.Id));

        refusal.ErrorCode.ShouldBe(WorkBookingRefusalCodes.OutsideGroupPeriod);
        diagnosis.DirectEntryCode.ShouldBe(refusal.ErrorCode);
    }

    [Test]
    public async Task Skill_ResolvesAVisibleShiftByName_AndAnswersAHiddenEmployeeLikeAnUnknownOne()
    {
        var hiddenNumber = await _context.Client.AsNoTracking()
            .Where(c => c.Id == _scenario.HiddenClient.Id)
            .Select(c => c.IdNumber)
            .SingleAsync();

        var visible = await RunSkillAsync(_scenario.VisibleClient.Name, Prefix + VisibleShiftSuffix);
        var hidden = await RunSkillAsync(_scenario.HiddenClient.Name, Prefix + VisibleShiftSuffix);
        var unknown = await RunSkillAsync(Prefix + UnknownClientSuffix, Prefix + VisibleShiftSuffix);
        var hiddenShift = await RunSkillAsync(_scenario.VisibleClient.Name, Prefix + HiddenShiftSuffix);

        visible.Success.ShouldBeTrue(visible.Message);
        hidden.Success.ShouldBe(unknown.Success, $"hidden: {hidden.Message} | unknown: {unknown.Message}");
        var hiddenAnswer = WithoutQueriedName(hidden, _scenario.HiddenClient.Name);
        hiddenAnswer.ShouldBe(WithoutQueriedName(unknown, Prefix + UnknownClientSuffix));
        hiddenAnswer.ShouldNotContain(_scenario.HiddenClient.Id.ToString());
        hiddenAnswer.ShouldNotContain($"(#{hiddenNumber})");
        hiddenShift.Success.ShouldBeFalse();
        hiddenShift.Message.ShouldBe(ShiftResolver.NameNotFoundMessage(Prefix + HiddenShiftSuffix));
    }

    [Test]
    public async Task Skill_FindsTheVisibleShift_BehindMoreHiddenSameNamedShiftsThanTheSearchLimit()
    {
        var hiddenTwins = Enumerable.Range(0, HiddenTwinCount)
            .Select(index =>
            {
                var twin = NewShift(TwinShiftSuffix);
                twin.FromDate = new DateOnly(2190, 1, 1).AddDays(index);
                return twin;
            })
            .ToList();
        var visibleTwin = NewShift(TwinShiftSuffix);
        _context.Shift.AddRange(hiddenTwins);
        _context.Shift.Add(visibleTwin);
        await _context.SaveChangesAsync();
        _context.GroupItem.AddRange(hiddenTwins.Select(twin =>
            new GroupItem { Id = Guid.NewGuid(), GroupId = _scenario.HiddenGroup.Id, ShiftId = twin.Id }));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await RunSkillAsync(_scenario.VisibleClient.Name, Prefix + TwinShiftSuffix);

        result.Success.ShouldBeTrue(result.Message);
        hiddenTwins.ShouldAllBe(twin => twin.FromDate < visibleTwin.FromDate);
        hiddenTwins.ShouldAllBe(twin => !result.Message.Contains(twin.Id.ToString()));
    }

    private async Task<WorkBookingDiagnosis> DiagnoseAsync(Guid clientId, Guid shiftId)
    {
        using var principal = ExecutionPrincipal.Begin(Guid.Parse(_scenario.Supervisor.Id));
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkBookingDiagnosisService>()
            .DiagnoseAsync(new WorkBookingDiagnosisRequest(clientId, shiftId, _date, null), CancellationToken.None);
    }

    private async Task PostAsync(Guid clientId, Guid shiftId)
    {
        using var principal = ExecutionPrincipal.Begin(Guid.Parse(_scenario.Supervisor.Id));
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new PostCommand<WorkResource>(new WorkResource
            {
                ClientId = clientId,
                ShiftId = shiftId,
                CurrentDate = _date,
                StartTime = ShiftStart,
                EndTime = ShiftEnd,
                WorkTime = 8m,
            }),
            CancellationToken.None);
    }

    private async Task<SkillResult> RunSkillAsync(string lastName, string shiftName)
    {
        using var principal = ExecutionPrincipal.Begin(Guid.Parse(_scenario.Supervisor.Id));
        using var scope = _factory.Services.CreateScope();
        var skill = ActivatorUtilities.CreateInstance<DiagnoseWorkBookingSkill>(scope.ServiceProvider);
        var context = new SkillExecutionContext
        {
            UserId = Guid.Parse(_scenario.Supervisor.Id),
            TenantId = Guid.NewGuid(),
            UserName = "supervisor",
            UserLanguage = "en",
            UserPermissions = Permissions.PlannerFloor.ToList(),
        };
        return await skill.ExecuteAsync(context, new Dictionary<string, object>
        {
            ["firstName"] = "Test",
            ["lastName"] = lastName,
            ["shiftName"] = shiftName,
            ["date"] = _date.ToString("yyyy-MM-dd"),
        });
    }

    private static string WithoutQueriedName(SkillResult result, string queriedLastName) =>
        $"{result.Message} {JsonSerializer.Serialize(result.Data)}".Replace(queriedLastName, QueriedNamePlaceholder);

    private Shift NewShift(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = Prefix + suffix,
        Status = ShiftStatus.OriginalShift,
        FromDate = new DateOnly(2199, 1, 1),
        StartShift = ShiftStart,
        EndShift = ShiftEnd,
    };

    private async Task SeedGlobalSealAsync()
    {
        var seal = new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = _date,
            GroupId = null,
            Level = WorkLockLevel.Closed,
            Reason = Prefix + "Seal",
            SealedAt = DateTime.UtcNow,
            SealedBy = Prefix + "Test",
        };
        _context.SealedDay.Add(seal);
        await _context.SaveChangesAsync();
        _sealedDayIds.Add(seal.Id);
    }

    private async Task CleanupAsync()
    {
        var pattern = Prefix.Replace("'", "''");
        var clientFilter = $"SELECT id FROM client WHERE starts_with(name, '{pattern}')";
        var shiftFilter = $"SELECT id FROM shift WHERE starts_with(name, '{pattern}')";
        await _context.Database.ExecuteSqlRawAsync($@"
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN ({clientFilter}));
            DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN ({clientFilter}));
            DELETE FROM work WHERE client_id IN ({clientFilter});
            DELETE FROM client_period_hours WHERE client_id IN ({clientFilter});
            DELETE FROM group_item WHERE shift_id IN ({shiftFilter});
            DELETE FROM shift WHERE starts_with(name, '{pattern}');
            DELETE FROM sealed_day WHERE starts_with(reason, '{pattern}');");
        await SupervisorVisibilityScenario.CleanupAsync(_context, Prefix);
    }
}
