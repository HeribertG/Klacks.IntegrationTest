// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The two accept refusals no override lifts, end to end on the real database: a stale cover of an absence built on the
/// real plan is refused as not overridable (also with OverrideStale), and so is every scenario built on such a cover (a
/// further cover, a planning run applied on it), while a cover built on a plain source scenario keeps the overridable
/// warning and a cover on a cover is accepted as long as the real plan is unchanged; and no accept deletes a real absence the scenario does not carry (same person, day and type),
/// whatever overrides are set. Each guard is proven on its own: the hand-planned shift case has no missing absence, the
/// old-scenario and removed-absence cases are no covers. The origin of a cover is set by the cover only, a plain scenario
/// and the source of a cover stay Other. Every row hangs off an INTEGRATION_TEST_CAX_ key and is removed by that prefix
/// or by the tokens of the scenarios the test created.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class CoverAcceptAbsenceGuardTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_CAX_";
    private const string WorkingMacro = """
        import hour
        import nightrate

        DIM Bonus
        Bonus = hour * nightrate

        OUTPUT 1, Round(Bonus, 2)
        """;
    private const string SickGuid = "00000000-0000-0000-00e9-000000000021";
    private const string HolidayGuid = "00000000-0000-0000-00e9-000000000022";
    private const int ClockGapMilliseconds = 50;

    private static readonly DateOnly Day = new(2090, 9, 12);
    private static readonly DateTime ValidFrom = new(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private Guid _groupId;
    private Guid _ruleId;
    private Guid _absentAId;
    private Guid _absentBId;
    private Guid _standInId;
    private Guid _standIn2Id;
    private Guid _recipientId;
    private Guid _shiftAId;
    private Guid _shiftBId;
    private readonly List<Guid> _scenarioTokens = [];

    [SetUp]
    public async Task SeedSetUp()
    {
        await PurgeAsync();
        _scenarioTokens.Clear();
        await SeedAsync();
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

    [Test]
    public async Task TwoSickOnTheSameDay_SecondCoverAfterTheFirstIsAccepted_IsRefusedEvenWithOverrideStale()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        await AddWorkAsync(_absentBId, _shiftBId);

        var coverA = await CoverAsync(_absentAId);
        var coverB = await CoverAsync(_absentBId);
        coverA.Covered.ShouldHaveSingleItem();
        coverB.Covered.ShouldHaveSingleItem();

        await AcceptAsync(coverA.ScenarioId);
        await Task.Delay(ClockGapMilliseconds);

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => AcceptAsync(coverB.ScenarioId, overrideStale: true));

        ex.Overridable.ShouldBeFalse();
        ex.ConflictDetails![ScenarioStaleException.OverridableField].ShouldBe(false);
        ex.CoverRedo.ShouldNotBeNull();
        ex.CoverRedo!.ClientId.ShouldBe(_absentBId);
        ex.CoverRedo.AbsenceId.ShouldBe(Guid.Parse(SickGuid));
        ex.CoverRedo.FromDate.ShouldBe(Day);
        ex.CoverRedo.UntilDate.ShouldBe(Day);

        (await LiveRealAbsenceCountAsync(_absentAId)).ShouldBe(1, "the accepted cover's absence must stay");
        var coverWork = await Context.Work.AsNoTracking()
            .SingleAsync(w => w.AnalyseToken == null && w.ShiftId == _shiftAId && w.CurrentDate == Day);
        coverWork.ClientId.ShouldNotBe(_absentAId, "the shift of A must stay with the person who covers it");
        (await StatusOfAsync(coverB.ScenarioId)).ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task CoverOnTheRealPlan_ThenAShiftPlannedByHand_IsRefusedByTheOriginRuleAlone()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        var cover = await CoverAsync(_absentAId);
        cover.Covered.ShouldHaveSingleItem();

        await Task.Delay(ClockGapMilliseconds);
        var handWorkId = await AddWorkAsync(_recipientId, _shiftBId);

        using (var scope = CreateScope())
        {
            var scenario = await LoadScenarioAsync(scope, cover.ScenarioId);
            scenario.Origin.ShouldBe(AnalyseScenarioOrigin.AbsenceCover);
            scenario.SourceToken.ShouldBeNull();
            await Should.NotThrowAsync(() => scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>()
                .EnsureNoRealAbsenceDroppedAsync(scenario, CancellationToken.None));
        }

        var ex = await Should.ThrowAsync<ScenarioStaleException>(() => AcceptAsync(cover.ScenarioId, overrideStale: true, overrideBlock: true));

        ex.Overridable.ShouldBeFalse();
        (await Context.Work.AsNoTracking().CountAsync(w => w.Id == handWorkId && !w.IsDeleted)).ShouldBe(1);
        (await StatusOfAsync(cover.ScenarioId)).ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task CoverOnAStaleRealPlanCover_IsRefusedAsNotOverridable_EvenWithOverrideStale()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        await AddWorkAsync(_absentBId, _shiftBId);
        var rootCover = await CoverAsync(_absentAId);

        await Task.Delay(ClockGapMilliseconds);
        var coverOnRoot = await CoverAsync(_absentBId, rootCover.Token);

        await Task.Delay(ClockGapMilliseconds);
        var handWorkId = await AddWorkAsync(_recipientId, _shiftBId);

        using (var scope = CreateScope())
        {
            var scenario = await LoadScenarioAsync(scope, coverOnRoot.ScenarioId);
            scenario.SourceToken.ShouldBe(rootCover.Token);
        }

        var ex = await Should.ThrowAsync<ScenarioStaleException>(
            () => AcceptAsync(coverOnRoot.ScenarioId, overrideStale: true, overrideBlock: true));

        ex.Overridable.ShouldBeFalse();
        ex.ConflictDetails![ScenarioStaleException.OverridableField].ShouldBe(false);
        (await Context.Work.AsNoTracking().CountAsync(w => w.Id == handWorkId && !w.IsDeleted)).ShouldBe(1);
        (await StatusOfAsync(coverOnRoot.ScenarioId)).ShouldBe(AnalyseScenarioStatus.Active);
        (await StatusOfAsync(rootCover.ScenarioId)).ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task PlanningRunAppliedOnAStaleRealPlanCover_IsRefusedAsNotOverridable()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        var rootCover = await CoverAsync(_absentAId);

        await Task.Delay(ClockGapMilliseconds);
        var applied = await CreatePlainScenarioAsync(rootCover.Token);
        applied.Origin.ShouldBe(AnalyseScenarioOrigin.Other);

        await Task.Delay(ClockGapMilliseconds);
        await AddWorkAsync(_recipientId, _shiftBId);

        var ex = await Should.ThrowAsync<ScenarioStaleException>(
            () => AcceptAsync(applied.Id, overrideStale: true, overrideBlock: true));

        ex.Overridable.ShouldBeFalse();
        (await StatusOfAsync(applied.Id)).ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task CoverOnARealPlanCover_WhileTheRealPlanIsUnchanged_IsAcceptedWithoutOverride()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        await AddWorkAsync(_absentBId, _shiftBId);
        var rootCover = await CoverAsync(_absentAId);

        await Task.Delay(ClockGapMilliseconds);
        var coverOnRoot = await CoverAsync(_absentBId, rootCover.Token);

        await AcceptAsync(coverOnRoot.ScenarioId);

        (await StatusOfAsync(coverOnRoot.ScenarioId)).ShouldBe(AnalyseScenarioStatus.Accepted);
        (await LiveRealAbsenceCountAsync(_absentAId)).ShouldBe(1, "the last cover of the chain carries the first cover's absence");
        (await LiveRealAbsenceCountAsync(_absentBId)).ShouldBe(1);
    }

    [Test]
    public async Task OlderPlainScenario_AfterAnAcceptedCover_IsRefusedByTheAbsenceRule_EvenWithOverrides()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        var older = await CreatePlainScenarioAsync(sourceToken: null);
        older.Origin.ShouldBe(AnalyseScenarioOrigin.Other);

        await Task.Delay(ClockGapMilliseconds);
        var cover = await CoverAsync(_absentAId);
        await AcceptAsync(cover.ScenarioId);
        await Task.Delay(ClockGapMilliseconds);

        var ex = await Should.ThrowAsync<ScenarioDropsRealAbsenceException>(
            () => AcceptAsync(older.Id, overrideStale: true, overrideBlock: true));

        ex.DroppedAbsenceCount.ShouldBe(1);
        var dropped = ex.DroppedAbsences.ShouldHaveSingleItem();
        dropped.ClientId.ShouldBe(_absentAId);
        dropped.Date.ShouldBe(Day);
        dropped.AbsenceId.ShouldBe(Guid.Parse(SickGuid));
        dropped.ClientName.ShouldContain("AbsentA");
        (await LiveRealAbsenceCountAsync(_absentAId)).ShouldBe(1);
        (await StatusOfAsync(older.Id)).ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task CoverOnASourceScenario_StaysOverridable_AndKeepsTheRealAbsencesItCarries()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        await AddRealAbsenceAsync(_recipientId, HolidayGuid);
        var source = await CreatePlainScenarioAsync(sourceToken: null);

        var cover = await CoverAsync(_absentAId, source.Token);
        cover.Covered.ShouldHaveSingleItem();

        await Task.Delay(ClockGapMilliseconds);
        await AddWorkAsync(_standIn2Id, _shiftBId);

        using (var scope = CreateScope())
        {
            var scenario = await LoadScenarioAsync(scope, cover.ScenarioId);
            scenario.Origin.ShouldBe(AnalyseScenarioOrigin.AbsenceCover);
            scenario.SourceToken.ShouldBe(source.Token);
        }

        (await StatusOfAsync(source.Id)).ShouldBe(AnalyseScenarioStatus.Active);
        (await Context.AnalyseScenarios.AsNoTracking().SingleAsync(s => s.Id == source.Id)).Origin
            .ShouldBe(AnalyseScenarioOrigin.Other, "the source of a cover is no cover");

        var stale = await Should.ThrowAsync<ScenarioStaleException>(() => AcceptAsync(cover.ScenarioId));
        stale.Overridable.ShouldBeTrue();

        await AcceptAsync(cover.ScenarioId, overrideStale: true);

        (await StatusOfAsync(cover.ScenarioId)).ShouldBe(AnalyseScenarioStatus.Accepted);
        (await LiveRealAbsenceCountAsync(_recipientId)).ShouldBe(1, "the real absence the cover carries must survive the accept");
        (await LiveRealAbsenceCountAsync(_absentAId)).ShouldBe(1);
    }

    [Test]
    public async Task ScenarioWithARemovedAbsence_IsRefused_WithEveryOverride()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        await AddRealAbsenceAsync(_recipientId, HolidayGuid);
        var scenario = await CreatePlainScenarioAsync(sourceToken: null);

        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE break SET is_deleted = true, deleted_time = now() WHERE analyse_token = {scenario.Token} AND client_id = {_recipientId}");

        var ex = await Should.ThrowAsync<ScenarioDropsRealAbsenceException>(
            () => AcceptAsync(scenario.Id, overrideStale: true, overrideBlock: true));

        ex.DroppedAbsences.ShouldHaveSingleItem().ClientId.ShouldBe(_recipientId);
        (await LiveRealAbsenceCountAsync(_recipientId)).ShouldBe(1);
        (await StatusOfAsync(scenario.Id)).ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task ScenarioCarryingEveryRealAbsence_IsAccepted()
    {
        await AddWorkAsync(_absentAId, _shiftAId);
        await AddRealAbsenceAsync(_recipientId, HolidayGuid);
        var scenario = await CreatePlainScenarioAsync(sourceToken: null);

        await AcceptAsync(scenario.Id);

        (await StatusOfAsync(scenario.Id)).ShouldBe(AnalyseScenarioStatus.Accepted);
        (await LiveRealAbsenceCountAsync(_recipientId)).ShouldBe(1);
    }

    private async Task<CoverAbsenceOutcome> CoverAsync(Guid clientId, Guid? sourceToken = null)
    {
        using var scope = CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new CoverAbsenceCommand(clientId, Day, _groupId, Guid.Parse(SickGuid), NotifyEscalationRoster: false, AnalyseToken: sourceToken),
            CancellationToken.None);
        _scenarioTokens.Add(outcome.Token);
        return outcome;
    }

    private async Task AcceptAsync(Guid scenarioId, bool overrideStale = false, bool overrideBlock = false)
    {
        using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new AcceptAnalyseScenarioCommand(scenarioId, overrideBlock, OverrideStale: overrideStale), CancellationToken.None);
    }

    private async Task<AnalyseScenario> CreatePlainScenarioAsync(Guid? sourceToken)
    {
        using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var token = Guid.NewGuid();
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = Prefix + "PLAIN",
            Token = token,
            GroupId = _groupId,
            FromDate = Day,
            UntilDate = Day,
            Status = AnalyseScenarioStatus.Active,
            SourceToken = sourceToken,
        };
        context.AnalyseScenarios.Add(scenario);
        await scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>()
            .CloneScenarioDataAsync(_groupId, Day, Day, token, null, sourceToken, CancellationToken.None);
        await context.SaveChangesAsync();
        _scenarioTokens.Add(token);
        return scenario;
    }

    private static async Task<AnalyseScenario> LoadScenarioAsync(IServiceScope scope, Guid scenarioId)
        => await scope.ServiceProvider.GetRequiredService<DataBaseContext>().AnalyseScenarios
            .AsNoTracking()
            .SingleAsync(s => s.Id == scenarioId);

    private async Task<AnalyseScenarioStatus> StatusOfAsync(Guid scenarioId)
        => (await Context.AnalyseScenarios.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == scenarioId)).Status;

    private Task<int> LiveRealAbsenceCountAsync(Guid clientId)
        => Context.Break.AsNoTracking().CountAsync(b => b.AnalyseToken == null && !b.IsDeleted
            && b.ClientId == clientId && b.CurrentDate == Day && b.ParentWorkId == null);

    private async Task SeedAsync()
    {
        _groupId = Guid.NewGuid();
        _ruleId = Guid.NewGuid();
        _absentAId = Guid.NewGuid();
        _absentBId = Guid.NewGuid();
        _standInId = Guid.NewGuid();
        _standIn2Id = Guid.NewGuid();
        _recipientId = Guid.NewGuid();

        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = ValidFrom });
        Context.SchedulingRules.Add(new SchedulingRule { Id = _ruleId, Name = Prefix + "RULE", MinPauseHours = 1m });
        AddEmployee(_absentAId, "AbsentA");
        AddEmployee(_absentBId, "AbsentB");
        AddEmployee(_standInId, "StandIn");
        AddEmployee(_standIn2Id, "StandIn2");
        AddEmployee(_recipientId, "Recipient");
        Context.Absence.AddRange(NewAbsence(SickGuid, "CXS"), NewAbsence(HolidayGuid, "CXH"));
        await Context.SaveChangesAsync();

        _shiftAId = await AddShiftAsync("A");
        _shiftBId = await AddShiftAsync("B");
    }

    private async Task<Guid> AddWorkAsync(Guid clientId, Guid shiftId)
    {
        var workId = Guid.NewGuid();
        Context.Work.Add(new Work
        {
            Id = workId,
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
        });
        await Context.SaveChangesAsync();
        return workId;
    }

    private async Task AddRealAbsenceAsync(Guid clientId, string absenceGuid)
    {
        Context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = Guid.Parse(absenceGuid),
            CurrentDate = Day,
            StartTime = TimeOnly.MinValue,
            EndTime = TimeOnly.MinValue,
            WorkTime = 8m,
        });
        await Context.SaveChangesAsync();
    }

    private async Task<Guid> AddShiftAsync(string suffix)
    {
        var macroId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        Context.Set<Macro>().Add(new Macro { Id = macroId, Name = Prefix + "MACRO", Type = 0, Content = WorkingMacro });
        Context.Shift.Add(new Shift
        {
            Id = shiftId,
            ShiftType = ShiftType.IsTask,
            Name = Prefix + "SHIFT_" + suffix,
            Abbreviation = "ITCX" + suffix,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            WorkTime = 8m,
            FromDate = new DateOnly(2090, 1, 1),
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            IsSaturday = true,
            IsSunday = true,
            Quantity = 1,
            Status = ShiftStatus.OriginalOrder,
            MacroId = macroId,
        });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = _groupId, ValidFrom = ValidFrom });
        await Context.SaveChangesAsync();
        return shiftId;
    }

    private void AddEmployee(Guid clientId, string name)
    {
        var contractId = Guid.NewGuid();
        Context.Client.Add(new Client { Id = clientId, FirstName = Prefix, Name = name, Type = EntityTypeEnum.Employee });
        Context.Contract.Add(new Contract
        {
            Id = contractId,
            Name = Prefix + name,
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
            SchedulingRuleId = _ruleId,
            NightRate = 0.1m,
        });
        Context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contractId,
            FromDate = new DateOnly(2090, 1, 1),
            IsActive = true,
        });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = _groupId, ValidFrom = ValidFrom });
    }

    private static Absence NewAbsence(string id, string abbreviation)
    {
        var name = new Klacks.Api.Domain.Common.MultiLanguage();
        name.SetValue("en", Prefix + abbreviation);
        var shortName = new Klacks.Api.Domain.Common.MultiLanguage();
        shortName.SetValue("en", abbreviation);
        var description = new Klacks.Api.Domain.Common.MultiLanguage();
        description.SetValue("en", Prefix);
        return new Absence
        {
            Id = Guid.Parse(id),
            Name = name,
            Abbreviation = shortName,
            Description = description,
            Color = "#888888",
        };
    }

    private async Task PurgeScenarioTokenAsync(Guid token)
    {
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM client_shift_preference WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM expenses WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE analyse_token = {token})");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM break WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work WHERE analyse_token = {token} AND parent_work_id IS NOT NULL");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM work WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM replacement_requests WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM client_period_hours WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM group_item WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM shift WHERE analyse_token = {token}");
        await Context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM analyse_scenarios WHERE token = {token}");
    }

    private async Task PurgeAsync()
    {
        var sql = $@"
            DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%'));
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%'));
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%') AND parent_work_id IS NOT NULL;
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM client_period_hours WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM replacement_requests WHERE absent_client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM analyse_scenarios WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM client_shift_preference WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM shift WHERE name LIKE '{Prefix}%';
            DELETE FROM macro WHERE name LIKE '{Prefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%';
            DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM contract WHERE name LIKE '{Prefix}%';
            DELETE FROM scheduling_rules WHERE name LIKE '{Prefix}%';
            DELETE FROM break WHERE absence_id IN ('{SickGuid}', '{HolidayGuid}');
            DELETE FROM absence WHERE id IN ('{SickGuid}', '{HolidayGuid}');
            DELETE FROM client WHERE first_name LIKE '{Prefix}%';
        ";
        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
