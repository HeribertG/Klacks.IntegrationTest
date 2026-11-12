// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Gate before the cover accept refuses every stale override: a cover built on the real plan must stay acceptable without
/// overrideStale through the normal flow - accepted right away, after the planner switched the stand-in to an alternative
/// or confirmed the proposed stand-in, and after a swap chain moved more than one work. The detector is asked first and
/// every real row stamped after the scenario's creation is listed in the failure message, so a red case names the writer.
/// A positive control edits one real work in scope and expects the refusal, proving the gate was armed. Every row hangs
/// off an INTEGRATION_TEST_CAG_ key and is removed by that prefix only.
/// </summary>

using System.Globalization;
using System.Text;
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
public sealed class CoverAcceptNotStaleGateTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_CAG_";
    private const string WorkingMacro = """
        import hour
        import nightrate

        DIM Bonus
        Bonus = hour * nightrate

        OUTPUT 1, Round(Bonus, 2)
        """;
    private const string AbsenceGuid = "00000000-0000-0000-00e9-000000000001";
    private const string UnpaidAbsenceGuid = "00000000-0000-0000-00e9-000000000002";
    private const decimal AbsentRate = 0.1m;
    private const decimal StandInRate = 0.5m;
    private const decimal SeededSurcharges = 99m;
    private const int RecordableWindowDays = 30;

    private static readonly DateOnly Day = new(2090, 9, 12);
    private static readonly DateTime ValidFrom = new(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private Guid _groupId;
    private Guid _absentId;
    private Guid _standInId;
    private Guid _recipientId;
    private Guid _workId;
    private Guid _ruleId;
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

    [Test]
    public async Task ACover_AcceptedRightAway_IsNotStale()
    {
        await SeedWholeWorkAsync(Day);

        var outcome = await CoverAsync(Day);
        outcome.Covered.ShouldHaveSingleItem().Kind.ShouldBe(ReplacementRequestKind.Move);

        await ShouldAcceptWithoutStaleOverrideAsync(outcome);
    }

    [Test]
    public async Task ACover_WhoseStandInWasSwitchedToAnAlternative_IsNotStale()
    {
        var day = NextTuesdayInTheRecordableWindow();
        await SeedWholeWorkAsync(day);
        _recipientId = Guid.NewGuid();
        AddEmployee(_recipientId, "Recipient", AbsentRate);
        await Context.SaveChangesAsync();

        var outcome = await CoverAsync(day);
        var proposal = outcome.Covered.ShouldHaveSingleItem();
        proposal.Kind.ShouldBe(ReplacementRequestKind.Move);
        var alternative = proposal.ReplacementClientId == _standInId ? _recipientId : _standInId;

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
                new RecordReplacementContactCommand(new RecordReplacementContactRequest(
                    _absentId, alternative, proposal.ShiftId, day, proposal.StartTime!.Value, proposal.EndTime!.Value,
                    _groupId, Guid.Parse(AbsenceGuid), outcome.Token, ReplacementRequestOutcome.Accepted)),
                CancellationToken.None);
        }

        var clone = await Context.Work.AsNoTracking()
            .SingleAsync(w => w.AnalyseToken == outcome.Token && w.ClonedFromWorkId == _workId);
        clone.ClientId.ShouldBe(alternative, "the switch must have happened, otherwise this case tests nothing");

        await ShouldAcceptWithoutStaleOverrideAsync(outcome);
    }

    [Test]
    public async Task ACover_WhoseProposedStandInWasConfirmed_IsNotStale()
    {
        var day = NextTuesdayInTheRecordableWindow();
        await SeedWholeWorkAsync(day);

        var outcome = await CoverAsync(day);
        var proposal = outcome.Covered.ShouldHaveSingleItem();
        proposal.RequestId.ShouldNotBeNull();

        using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
                new SetReplacementRequestOutcomeCommand(proposal.RequestId!.Value, ReplacementRequestOutcome.Accepted),
                CancellationToken.None);
        }

        await ShouldAcceptWithoutStaleOverrideAsync(outcome);
    }

    [Test]
    public async Task ACoverWithASwapChain_ThatMovedTwoWorks_IsNotStale()
    {
        await SeedSwapPeopleAsync();
        var coverShiftId = await AddShiftAsync(new TimeOnly(8, 0), new TimeOnly(16, 0));
        var hopShiftId = await AddShiftAsync(new TimeOnly(14, 0), new TimeOnly(22, 0));
        await BlacklistAsync(_recipientId, coverShiftId);
        await AddSwapWorksAsync(Day, coverShiftId, hopShiftId);

        var outcome = await CoverAsync(Day);
        outcome.Covered.ShouldHaveSingleItem().Kind.ShouldBe(ReplacementRequestKind.Move);

        var cover = await ScenarioWorkOfShiftAsync(outcome.Token, coverShiftId, Day);
        cover.ClientId.ShouldBe(_standInId);
        cover.MovedFromClientId.ShouldBe(_absentId);
        var hop = await ScenarioWorkOfShiftAsync(outcome.Token, hopShiftId, Day);
        hop.ClientId.ShouldBe(_recipientId, "the chain must move the stand-in's own work on, otherwise it is no chain");
        hop.MovedFromClientId.ShouldBe(_standInId);

        await ShouldAcceptWithoutStaleOverrideAsync(outcome);
    }

    [Test]
    public async Task ACover_WhoseRealWorkWasEditedAfterwards_IsStale_TheGateIsArmed()
    {
        await SeedWholeWorkAsync(Day);

        var outcome = await CoverAsync(Day);
        outcome.Covered.ShouldHaveSingleItem();

        using (var scope = CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
            var real = await context.Work.SingleAsync(w => w.Id == _workId);
            real.EndTime = new TimeOnly(15, 0);
            await context.SaveChangesAsync();
        }

        var staleness = await DetectStalenessAsync(outcome.ScenarioId);
        staleness.ShouldNotBeNull("an edit of the real work after the cover must be detected");
        staleness.ChangedEntryCount.ShouldBeGreaterThanOrEqualTo(1);

        using var acceptScope = CreateScope();
        await Should.ThrowAsync<ScenarioStaleException>(() => acceptScope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new AcceptAnalyseScenarioCommand(outcome.ScenarioId), CancellationToken.None));
    }

    private async Task ShouldAcceptWithoutStaleOverrideAsync(CoverAbsenceOutcome outcome)
    {
        var staleness = await DetectStalenessAsync(outcome.ScenarioId);
        if (staleness is not null)
        {
            Assert.Fail(await DescribeStalenessAsync(outcome.ScenarioId, staleness));
        }

        try
        {
            using var scope = CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new AcceptAnalyseScenarioCommand(outcome.ScenarioId), CancellationToken.None);
        }
        catch (ScenarioStaleException stale)
        {
            Assert.Fail("accept refused as stale: " + await DescribeStalenessAsync(outcome.ScenarioId, stale.Staleness));
        }

        var accepted = await Context.AnalyseScenarios.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.Id == outcome.ScenarioId);
        accepted.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
    }

    private async Task<ScenarioStaleness?> DetectStalenessAsync(Guid scenarioId)
    {
        using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var scenario = await context.AnalyseScenarios.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == scenarioId);
        scenario.CreateTime.ShouldNotBeNull("without a creation time the detector skips the check and every case is green");
        return await scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>()
            .FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None);
    }

    /// <summary>
    /// Lists every real row of the seeded people stamped after the scenario's creation, with the stamp that exceeded it.
    /// </summary>
    private async Task<string> DescribeStalenessAsync(Guid scenarioId, ScenarioStaleness staleness)
    {
        var scenario = await Context.AnalyseScenarios.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == scenarioId);
        var since = scenario.CreateTime!.Value;
        var people = new[] { _absentId, _standInId, _recipientId };
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"changedEntryCount={staleness.ChangedEntryCount}, lastChangeUtc={staleness.LastChangeUtc:O}, since={since:O}, scope={scenario.FromDate}..{scenario.UntilDate}; ");

        var works = await Context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == null && people.Contains(w.ClientId)
                && (w.CreateTime > since || w.UpdateTime > since || w.DeletedTime > since))
            .ToListAsync();
        foreach (var w in works)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"[Work {w.Id} client={w.ClientId} day={w.CurrentDate} parent={w.ParentWorkId} {Stamps(w, since)} by={w.CurrentUserUpdated}] ");
        }

        var workIds = await Context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == null && people.Contains(w.ClientId))
            .Select(w => w.Id)
            .ToListAsync();
        var changes = await Context.WorkChange.IgnoreQueryFilters().AsNoTracking()
            .Where(wc => wc.AnalyseToken == null && workIds.Contains(wc.WorkId)
                && (wc.CreateTime > since || wc.UpdateTime > since || wc.DeletedTime > since))
            .ToListAsync();
        foreach (var wc in changes)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"[WorkChange {wc.Id} work={wc.WorkId} type={wc.Type} {Stamps(wc, since)} by={wc.CurrentUserUpdated}] ");
        }

        var expenses = await Context.Expenses.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.AnalyseToken == null && workIds.Contains(e.WorkId)
                && (e.CreateTime > since || e.UpdateTime > since || e.DeletedTime > since))
            .ToListAsync();
        foreach (var e in expenses)
        {
            text.Append(CultureInfo.InvariantCulture, $"[Expense {e.Id} work={e.WorkId} {Stamps(e, since)} by={e.CurrentUserUpdated}] ");
        }

        var breaks = await Context.Break.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.AnalyseToken == null && people.Contains(b.ClientId)
                && (b.CreateTime > since || b.UpdateTime > since || b.DeletedTime > since))
            .ToListAsync();
        foreach (var b in breaks)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"[Break {b.Id} client={b.ClientId} day={b.CurrentDate} absence={b.AbsenceId} parent={b.ParentWorkId} {Stamps(b, since)} by={b.CurrentUserUpdated}] ");
        }

        var notes = await Context.ScheduleNotes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.AnalyseToken == null && people.Contains(n.ClientId)
                && (n.CreateTime > since || n.UpdateTime > since || n.DeletedTime > since))
            .ToListAsync();
        foreach (var n in notes)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"[ScheduleNote {n.Id} client={n.ClientId} day={n.CurrentDate} {Stamps(n, since)} by={n.CurrentUserUpdated}] ");
        }

        return text.ToString();
    }

    private static string Stamps(Klacks.Api.Domain.Common.BaseEntity row, DateTime since)
    {
        var stamps = new List<string>();
        if (row.CreateTime > since)
        {
            stamps.Add(string.Create(CultureInfo.InvariantCulture, $"created={row.CreateTime:O}"));
        }

        if (row.UpdateTime > since)
        {
            stamps.Add(string.Create(CultureInfo.InvariantCulture, $"updated={row.UpdateTime:O}"));
        }

        if (row.DeletedTime > since)
        {
            stamps.Add(string.Create(CultureInfo.InvariantCulture, $"deleted={row.DeletedTime:O}"));
        }

        return string.Join(" ", stamps);
    }

    private async Task<CoverAbsenceOutcome> CoverAsync(DateOnly day)
    {
        using var scope = CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
            new CoverAbsenceCommand(_absentId, day, _groupId, Guid.Parse(AbsenceGuid)), CancellationToken.None);
        _scenarioTokens.Add(outcome.Token);
        return outcome;
    }

    private async Task<Work> ScenarioWorkOfShiftAsync(Guid token, Guid sourceShiftId, DateOnly day)
    {
        var cloneShiftIds = await Context.Shift.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.AnalyseToken == token && s.ScenarioSourceShiftId == sourceShiftId)
            .Select(s => s.Id)
            .ToListAsync();
        return await Context.Work.AsNoTracking()
            .SingleAsync(w => w.AnalyseToken == token && w.CurrentDate == day && cloneShiftIds.Contains(w.ShiftId));
    }

    private static DateOnly NextTuesdayInTheRecordableWindow()
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(RecordableWindowDays);
        return day.AddDays(((int)DayOfWeek.Tuesday - (int)day.DayOfWeek + 7) % 7);
    }

    private void SeedPeopleAndGroup()
    {
        _groupId = Guid.NewGuid();
        _absentId = Guid.NewGuid();
        _standInId = Guid.NewGuid();
        _recipientId = Guid.NewGuid();
        _ruleId = Guid.NewGuid();

        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = ValidFrom });
        Context.SchedulingRules.Add(new SchedulingRule { Id = _ruleId, Name = Prefix + "RULE", MinPauseHours = 1m });
        AddEmployee(_absentId, "Absent", AbsentRate);
        AddEmployee(_standInId, "StandIn", StandInRate);
        Context.Absence.AddRange(NewAbsence(AbsenceGuid, "ITA", false), NewAbsence(UnpaidAbsenceGuid, "ITU", true));
    }

    /// <summary>
    /// The absent person's work with a sub-work, an unpaid sub-break, a correction and an expense on it.
    /// </summary>
    private async Task SeedWholeWorkAsync(DateOnly day)
    {
        SeedPeopleAndGroup();
        await Context.SaveChangesAsync();
        var shiftId = await AddShiftAsync(new TimeOnly(8, 0), new TimeOnly(16, 0));
        _workId = Guid.NewGuid();

        Context.Work.Add(new Work
        {
            Id = _workId,
            ClientId = _absentId,
            ShiftId = shiftId,
            CurrentDate = day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            Surcharges = SeededSurcharges,
        });
        Context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ParentWorkId = _workId,
            ClientId = _absentId,
            ShiftId = shiftId,
            CurrentDate = day,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            WorkTime = 1m,
            Surcharges = SeededSurcharges,
        });
        Context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ParentWorkId = _workId,
            ClientId = _absentId,
            AbsenceId = Guid.Parse(UnpaidAbsenceGuid),
            CurrentDate = day,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            WorkTime = -1m,
            Surcharges = SeededSurcharges,
        });
        Context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = _workId,
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = 1m,
            Surcharges = SeededSurcharges,
            Description = Prefix,
        });
        Context.Expenses.Add(new Expenses { Id = Guid.NewGuid(), WorkId = _workId, Amount = 5m, Description = Prefix });

        await Context.SaveChangesAsync();
    }

    private async Task SeedSwapPeopleAsync()
    {
        SeedPeopleAndGroup();
        AddEmployee(_recipientId, "Recipient", AbsentRate);
        await Context.SaveChangesAsync();
    }

    private async Task<Guid> AddShiftAsync(TimeOnly start, TimeOnly end)
    {
        var macroId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        Context.Set<Macro>().Add(new Macro { Id = macroId, Name = Prefix + "MACRO", Type = 0, Content = WorkingMacro });
        Context.Shift.Add(new Shift
        {
            Id = shiftId,
            ShiftType = ShiftType.IsTask,
            Name = Prefix + "SHIFT",
            Abbreviation = "ITCAG",
            StartShift = start,
            EndShift = end,
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

    private async Task BlacklistAsync(Guid clientId, Guid shiftId)
    {
        Context.ClientShiftPreference.Add(new ClientShiftPreference
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            PreferenceType = ShiftPreferenceType.Blacklist,
        });
        await Context.SaveChangesAsync();
    }

    private async Task AddSwapWorksAsync(DateOnly day, Guid coverShiftId, Guid hopShiftId)
    {
        Context.Work.AddRange(
            new Work
            {
                Id = Guid.NewGuid(),
                ClientId = _absentId,
                ShiftId = coverShiftId,
                CurrentDate = day,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(16, 0),
                WorkTime = 8m,
                Surcharges = SeededSurcharges,
            },
            new Work
            {
                Id = Guid.NewGuid(),
                ClientId = _standInId,
                ShiftId = hopShiftId,
                CurrentDate = day,
                StartTime = new TimeOnly(14, 0),
                EndTime = new TimeOnly(22, 0),
                WorkTime = 8m,
                Surcharges = SeededSurcharges,
            });
        await Context.SaveChangesAsync();
    }

    private void AddEmployee(Guid clientId, string name, decimal nightRate)
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
            NightRate = nightRate,
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

    private static Absence NewAbsence(string id, string abbreviation, bool isUnpaid)
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
            IsUnpaid = isUnpaid,
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
            DELETE FROM expenses WHERE description LIKE '{Prefix}%';
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
            DELETE FROM break WHERE absence_id IN ('{AbsenceGuid}', '{UnpaidAbsenceGuid}');
            DELETE FROM absence WHERE id IN ('{AbsenceGuid}', '{UnpaidAbsenceGuid}');
            DELETE FROM client WHERE first_name LIKE '{Prefix}%';
        ";
        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
