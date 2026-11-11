// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parity of the automatic absence cover and the list of alternatives against PostgreSQL: whoever the cover picks as a
/// direct replacement (free or on call, from the group or borrowed from the sister group under the same root) is
/// eligible in find_replacement on the real plan before the cover is accepted, the borrowed person marked IsBorrowed.
/// The temporary membership the cover writes into its scenario does not turn the borrowed person into a member, and
/// adding that membership again widens it instead of doubling it. Every seeded row hangs off an INTEGRATION_TEST_RAP_
/// key or one of the two fixed absence ids and is removed by that key; scenario rows are removed by their token.
/// </summary>

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.IntegrationTest.Wizard;
using EscalationTier = Klacks.ScheduleRecovery.Model.EscalationTier;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class RecoveryAlternativesParityTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_RAP_";
    private const string SickAbsenceGuid = "00000000-0000-0000-00e7-000000000001";
    private const string OnCallAbsenceGuid = "00000000-0000-0000-00e7-000000000002";
    private static readonly DateOnly Day = new(2091, 5, 9);
    private static readonly DateTime ValidFrom = new(2091, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeOnly ShiftStart = new(8, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0);

    private Guid _rootId;
    private Guid _groupId;
    private Guid _sisterGroupId;
    private Guid _absentId;
    private Guid _memberId;
    private Guid _sisterId;
    private Guid _shiftId;
    private Guid _contractId;
    private readonly List<Guid> _scenarioTokens = [];

    [SetUp]
    public async Task SeedSetUp()
    {
        await PurgeAsync();
        _scenarioTokens.Clear();
    }

    [TearDown]
    public async Task SeedTearDown()
    {
        foreach (var token in _scenarioTokens)
        {
            await PurgeScenarioTokenAsync(token);
        }

        await PurgeAsync();
    }

    [TestCase(true, false, EscalationTier.InGroupFree)]
    [TestCase(true, true, EscalationTier.InGroupOnCall)]
    [TestCase(false, false, EscalationTier.CrossGroupFree)]
    [TestCase(false, true, EscalationTier.CrossGroupOnCall)]
    public async Task TheDirectReplacementOfTheCover_IsAnEligibleAlternativeOnTheRealPlan(
        bool withGroupMember, bool onCall, EscalationTier expectedTier)
    {
        // Arrange: a sister group under the same root always has a free person; the group itself has one or none.
        await SeedAsync(withGroupMember);
        var expectedReplacementId = withGroupMember ? _memberId : _sisterId;
        if (onCall)
        {
            await PutOnCallAsync(expectedReplacementId);
        }

        // Act
        var alternatives = await SendAsync(AlternativesOnTheRealPlan());
        var outcome = await CoverAsync();

        // Assert
        var covered = outcome.Covered.ShouldHaveSingleItem();
        covered.Relocations.ShouldNotBeNull().ShouldBeEmpty();
        covered.Tier.ShouldBe((int)expectedTier);
        covered.ReplacementClientId.ShouldBe(expectedReplacementId);
        var alternative = alternatives.Eligible.SingleOrDefault(c => c.ClientId == covered.ReplacementClientId);
        alternative.ShouldNotBeNull("the person the cover picked must be an eligible alternative");
        alternative.IsBorrowed.ShouldBe(!withGroupMember);
        alternative.IsOnCall.ShouldBe(onCall);
        alternative.IsContractDayOff.ShouldBeFalse();
        alternatives.Eligible[0].ClientId.ShouldBe(covered.ReplacementClientId, "the cover takes the first alternative");
    }

    [Test]
    public async Task TheMemberOfTheSisterGroup_IsListedAfterTheGroupMember_AndMarkedBorrowed()
    {
        // Arrange
        await SeedAsync(withGroupMember: true);

        // Act
        var alternatives = await SendAsync(AlternativesOnTheRealPlan());

        // Assert
        alternatives.Eligible.Select(c => (c.ClientId, c.IsBorrowed)).ShouldBe([(_memberId, false), (_sisterId, true)]);
        alternatives.Excluded.ShouldHaveSingleItem().ClientId.ShouldBe(_absentId, "the absent person still holds the shift on the real plan");
    }

    [Test]
    public async Task TheTemporaryMembershipOfTheCover_DoesNotMakeTheBorrowedPersonAMember()
    {
        // Arrange: the cover borrows the sister and writes her temporary membership into the scenario.
        await SeedAsync(withGroupMember: false);
        var outcome = await CoverAsync();
        outcome.Covered.ShouldHaveSingleItem().ReplacementClientId.ShouldBe(_sisterId);
        (await ScenarioMembershipsAsync(outcome.Token)).ShouldHaveSingleItem();

        // Act
        var onTheRealPlan = await SendAsync(AlternativesOnTheRealPlan());

        // Assert
        var sister = onTheRealPlan.Eligible.ShouldHaveSingleItem();
        sister.ClientId.ShouldBe(_sisterId);
        sister.IsBorrowed.ShouldBeTrue();
    }

    [TestCase(1)]
    [TestCase(4)]
    public async Task ATemporaryMembershipAddedAgain_IsWidenedToTheSpan_NotDoubled(int daysLater)
    {
        // Arrange: one row per client, group and token, so a later day spans the days between as well.
        await SeedAsync(withGroupMember: false);
        var outcome = await CoverAsync();
        var nextDay = Day.AddDays(daysLater);

        // Act
        using (var scope = CreateScope())
        {
            var scenarioService = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
            await scenarioService.AddScenarioMembershipAsync(
                outcome.Token, _sisterId, _groupId, nextDay, nextDay, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CompleteAsync();
        }

        // Assert
        var membership = (await ScenarioMembershipsAsync(outcome.Token)).ShouldHaveSingleItem();
        membership.ValidFrom.ShouldBe(Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        membership.ValidUntil.ShouldBe(nextDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private FindReplacementQuery AlternativesOnTheRealPlan() => new(
        ShiftId: _shiftId,
        Date: Day,
        StartTime: ShiftStart,
        EndTime: ShiftEnd,
        GroupId: _groupId,
        AnalyseToken: null);

    private async Task<CoverAbsenceOutcome> CoverAsync()
    {
        var outcome = await SendAsync(new CoverAbsenceCommand(
            _absentId, Day, _groupId, Guid.Parse(SickAbsenceGuid),
            NotifyEscalationRoster: false, Source: ReplacementRequestSource.PlannerDialog));
        _scenarioTokens.Add(outcome.Token);
        return outcome;
    }

    private async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        using var scope = CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(request, CancellationToken.None);
    }

    private Task<List<GroupItem>> ScenarioMembershipsAsync(Guid token)
        => Context.GroupItem.AsNoTracking()
            .Where(gi => gi.AnalyseToken == token && gi.ClientId == _sisterId && gi.GroupId == _groupId)
            .ToListAsync();

    private async Task PutOnCallAsync(Guid clientId)
    {
        Context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = Guid.Parse(OnCallAbsenceGuid),
            CurrentDate = Day,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(0, 0),
        });
        await Context.SaveChangesAsync();
    }

    private async Task SeedAsync(bool withGroupMember)
    {
        _rootId = Guid.NewGuid();
        _groupId = Guid.NewGuid();
        _sisterGroupId = Guid.NewGuid();
        _absentId = Guid.NewGuid();
        _memberId = Guid.NewGuid();
        _sisterId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _contractId = Guid.NewGuid();

        Context.Group.Add(new Group { Id = _rootId, Name = Prefix + "ROOT", ValidFrom = ValidFrom });
        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = ValidFrom, Parent = _rootId, Root = _rootId });
        Context.Group.Add(new Group { Id = _sisterGroupId, Name = Prefix + "SISTER", ValidFrom = ValidFrom, Parent = _rootId, Root = _rootId });
        Context.Contract.Add(new Contract
        {
            Id = _contractId,
            Name = Prefix + "Contract",
            ValidFrom = ValidFrom,
            GuaranteedHours = 100m,
            MaximumHours = 200m,
            FullTime = 40m,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        });
        Context.Absence.Add(NewAbsence(SickAbsenceGuid, "SICK", isOnCall: false));
        Context.Absence.Add(NewAbsence(OnCallAbsenceGuid, "ONCALL", isOnCall: true));

        AddEmployee(_absentId, "Absent", _groupId);
        AddEmployee(_sisterId, "Sister", _sisterGroupId);
        if (withGroupMember)
        {
            AddEmployee(_memberId, "Member", _groupId);
        }

        Context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = Prefix + "SHIFT",
            Abbreviation = "ITRAP",
            StartShift = ShiftStart,
            EndShift = ShiftEnd,
            WorkTime = 8m,
            FromDate = new DateOnly(2091, 1, 1),
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            IsSaturday = true,
            IsSunday = true,
            Quantity = 1,
            Status = ShiftStatus.OriginalOrder,
        });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ShiftId = _shiftId, GroupId = _groupId, ValidFrom = ValidFrom });
        Context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _absentId,
            ShiftId = _shiftId,
            CurrentDate = Day,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = 8m,
        });

        await Context.SaveChangesAsync();
    }

    private void AddEmployee(Guid clientId, string name, Guid groupId)
    {
        Context.Client.Add(new Client { Id = clientId, FirstName = Prefix, Name = Prefix + name, Type = EntityTypeEnum.Employee });
        Context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = _contractId,
            FromDate = new DateOnly(2091, 1, 1),
            IsActive = true,
        });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = groupId, ValidFrom = ValidFrom });
    }

    private static Absence NewAbsence(string id, string abbreviation, bool isOnCall) => new()
    {
        Id = Guid.Parse(id),
        Name = new MultiLanguage { De = Prefix + abbreviation, En = Prefix + abbreviation },
        Abbreviation = new MultiLanguage { De = abbreviation, En = abbreviation },
        Description = new MultiLanguage { De = Prefix, En = Prefix },
        Color = "#888888",
        IsOnCall = isOnCall,
    };

    private async Task PurgeScenarioTokenAsync(Guid token)
    {
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE analyse_token = {token})");
        await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM break WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM work WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM replacement_requests WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM client_period_hours WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM group_item WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM shift WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM analyse_scenarios WHERE token = {token}");
    }

    /// <summary>
    /// Prefix-scoped hard cleanup, FK children first. Only rows carrying the INTEGRATION_TEST_RAP_ marker or one of the
    /// two absence ids of this fixture are removed.
    /// </summary>
    private async Task PurgeAsync()
    {
        var sql = $@"
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%'));
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM client_period_hours WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM replacement_requests WHERE absent_client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM analyse_scenarios WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM shift WHERE name LIKE '{Prefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%' AND parent IS NOT NULL;
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%';
            DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM contract WHERE name LIKE '{Prefix}%';
            DELETE FROM break WHERE absence_id IN ('{SickAbsenceGuid}', '{OnCallAbsenceGuid}');
            DELETE FROM absence WHERE id IN ('{SickAbsenceGuid}', '{OnCallAbsenceGuid}');
            DELETE FROM client WHERE first_name LIKE '{Prefix}%';";

        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
