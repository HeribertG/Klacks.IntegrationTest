// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Seeds the plan the find_replacement cost fixtures search in and removes it again: one root group, the requested
/// group and any number of sister groups under that root, one all-week contract bound to one scheduling rule, one
/// absence type, one shift per group and the people of each group, plus the planning rules and per-person
/// compensatory-rest obligations of FindReplacementRuleSeed. Every person has ten works on the shift of their own
/// group inside the HomeWorkHalfWindowDays before and after Day: two days on and one off, the free day shifted by one
/// per person. The first person of the requested group (SlotHolderId) works on Day and holds the requested shift, the
/// one who then falls out; of the others about a third are free on Day and the rest collide with the slot.
/// Every seeded row hangs off a name or import source key that starts with INTEGRATION_TEST_FRM_ plus the scope of the
/// fixture (the obligations off the seeded clients); the cleanup deletes by that prefix only, scenario rows by the
/// token the cover returned. Two fixtures with different scopes do not touch each other's rows.
/// </summary>
/// <param name="scope">Short upper-case label of the fixture, part of every seeded name</param>
/// <param name="groupPeople">People in the requested group, including the one who holds the requested shift on Day</param>
/// <param name="sisterGroupSizes">People per sister group under the same root; empty for no sister group</param>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klacks.IntegrationTest.Recovery;

public sealed class FindReplacementPoolSeed
{
    public static readonly DateOnly Day = new(2092, 5, 14);
    public static readonly TimeOnly ShiftStart = new(8, 0);
    public static readonly TimeOnly ShiftEnd = new(16, 0);

    private const string PrefixRoot = "INTEGRATION_TEST_FRM_";
    private const string PrefixEnd = "_";
    private const string SeededFirstName = "FRM";
    private const string ShiftAbbreviationPrefix = "FRM";
    private const string GroupLabel = "GROUP";
    private const int HomeWorkHalfWindowDays = 7;
    private const int FreeEveryNthDay = 3;
    private const decimal ShiftHours = 8m;

    private const string SeededClients = "SELECT id FROM client WHERE starts_with(name, {0})";
    private const string SeededGroups = "SELECT id FROM \"group\" WHERE starts_with(name, {0})";
    private const string SeededShifts = "SELECT id FROM shift WHERE starts_with(name, {0})";
    private const string SeededAbsences = "SELECT id FROM absence WHERE starts_with(name->>'de', {0})";

    private static readonly DateOnly FirstDayOfSeedYear = new(2092, 1, 1);
    private static readonly DateTime ValidFrom = new(2092, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ScenarioPurgeStatements =
    [
        "DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE analyse_token = {0})",
        "DELETE FROM break WHERE analyse_token = {0}",
        "DELETE FROM work WHERE analyse_token = {0}",
        "DELETE FROM replacement_requests WHERE analyse_token = {0}",
        "DELETE FROM client_period_hours WHERE analyse_token = {0}",
        "DELETE FROM group_item WHERE analyse_token = {0}",
        "DELETE FROM shift WHERE analyse_token = {0}",
        "DELETE FROM analyse_scenarios WHERE token = {0}",
    ];

    private static readonly string[] PrefixPurgeStatements =
    [
        .. FindReplacementRuleSeed.RulePurgeStatements,
        "DELETE FROM compensatory_rest_obligation WHERE client_id IN (" + SeededClients + ")",
        "DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (" + SeededClients + "))",
        "DELETE FROM break WHERE client_id IN (" + SeededClients + ")",
        "DELETE FROM work WHERE client_id IN (" + SeededClients + ")",
        "DELETE FROM client_period_hours WHERE client_id IN (" + SeededClients + ")",
        "DELETE FROM replacement_requests WHERE absent_client_id IN (" + SeededClients + ")",
        "DELETE FROM analyse_scenarios WHERE group_id IN (" + SeededGroups + ")",
        "DELETE FROM group_item WHERE group_id IN (" + SeededGroups + ")",
        "DELETE FROM group_item WHERE shift_id IN (" + SeededShifts + ")",
        "DELETE FROM group_item WHERE client_id IN (" + SeededClients + ")",
        "DELETE FROM shift WHERE starts_with(name, {0})",
        "DELETE FROM \"group\" WHERE starts_with(name, {0}) AND parent IS NOT NULL",
        "DELETE FROM \"group\" WHERE starts_with(name, {0})",
        "DELETE FROM client_contract WHERE client_id IN (" + SeededClients + ")",
        "DELETE FROM contract WHERE starts_with(name, {0})",
        FindReplacementRuleSeed.SchedulingRulePurgeStatement,
        "DELETE FROM break WHERE absence_id IN (" + SeededAbsences + ")",
        "DELETE FROM absence WHERE starts_with(name->>'de', {0})",
        "DELETE FROM client WHERE starts_with(name, {0})",
    ];

    private readonly string _prefix;
    private readonly Guid _contractId = Guid.NewGuid();
    private readonly Guid _schedulingRuleId = Guid.NewGuid();
    private int _seededPeople;

    private FindReplacementPoolSeed(string scope)
    {
        _prefix = PrefixOf(scope);
    }

    public Guid GroupId { get; } = Guid.NewGuid();

    public Guid ShiftId { get; } = Guid.NewGuid();

    public Guid SlotHolderId { get; } = Guid.NewGuid();

    public Guid SickAbsenceId { get; } = Guid.NewGuid();

    public static async Task<FindReplacementPoolSeed> CreateAsync(
        DataBaseContext context, string scope, int groupPeople, IReadOnlyList<int> sisterGroupSizes)
    {
        var seed = new FindReplacementPoolSeed(scope);
        seed.Add(context, groupPeople, sisterGroupSizes);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return seed;
    }

    public static async Task PurgeAsync(DataBaseContext context, string scope)
    {
        var prefix = PrefixOf(scope);
        foreach (var statement in PrefixPurgeStatements)
        {
            await context.Database.ExecuteSqlRawAsync(statement, prefix);
        }
    }

    public static async Task PurgeScenarioAsync(DataBaseContext context, Guid token)
    {
        foreach (var statement in ScenarioPurgeStatements)
        {
            await context.Database.ExecuteSqlRawAsync(statement, token);
        }
    }

    public static string PrefixOf(string scope) => PrefixRoot + scope + PrefixEnd;

    private void Add(DataBaseContext context, int groupPeople, IReadOnlyList<int> sisterGroupSizes)
    {
        var rootId = Guid.NewGuid();
        context.Group.Add(new Group { Id = rootId, Name = _prefix + "ROOT", ValidFrom = ValidFrom });
        FindReplacementRuleSeed.AddSchedulingRule(context, _prefix, _schedulingRuleId);
        FindReplacementRuleSeed.AddRules(context, _prefix, _schedulingRuleId, _prefix + GroupLabel, Day);
        context.Contract.Add(new Contract
        {
            Id = _contractId,
            Name = _prefix + "Contract",
            ValidFrom = ValidFrom,
            GuaranteedHours = 100m,
            MaximumHours = 200m,
            FullTime = 40m,
            PerformsShiftWork = true,
            SchedulingRuleId = _schedulingRuleId,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        });
        context.Absence.Add(new Absence
        {
            Id = SickAbsenceId,
            Name = new MultiLanguage { De = _prefix + "SICK", En = _prefix + "SICK" },
            Abbreviation = new MultiLanguage { De = "SICK", En = "SICK" },
            Description = new MultiLanguage { De = _prefix, En = _prefix },
            Color = "#888888",
            IsOnCall = false,
        });

        AddGroupWithPeople(context, rootId, GroupId, ShiftId, GroupLabel, groupPeople, SlotHolderId);
        for (var sister = 0; sister < sisterGroupSizes.Count; sister++)
        {
            AddGroupWithPeople(
                context, rootId, Guid.NewGuid(), Guid.NewGuid(), $"SISTER{sister + 1}", sisterGroupSizes[sister], firstPersonId: null);
        }
    }

    private void AddGroupWithPeople(
        DataBaseContext context, Guid rootId, Guid groupId, Guid shiftId, string label, int people, Guid? firstPersonId)
    {
        context.Group.Add(new Group { Id = groupId, Name = _prefix + label, ValidFrom = ValidFrom, Parent = rootId, Root = rootId });
        context.Shift.Add(new Shift
        {
            Id = shiftId,
            Name = _prefix + label + "_SHIFT",
            Abbreviation = ShiftAbbreviationPrefix + label,
            StartShift = ShiftStart,
            EndShift = ShiftEnd,
            WorkTime = ShiftHours,
            FromDate = FirstDayOfSeedYear,
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
        context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ShiftId = shiftId, GroupId = groupId, ValidFrom = ValidFrom });

        for (var person = 0; person < people; person++)
        {
            var clientId = person == 0 && firstPersonId is { } first ? first : Guid.NewGuid();
            AddPerson(context, clientId, $"{label}_{person + 1:D3}", groupId);
            AddHomeWorks(context, clientId, shiftId, _seededPeople);
            _seededPeople++;
        }
    }

    private void AddPerson(DataBaseContext context, Guid clientId, string label, Guid groupId)
    {
        context.Client.Add(new Client { Id = clientId, FirstName = SeededFirstName, Name = _prefix + label, Type = EntityTypeEnum.Employee });
        context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = _contractId,
            FromDate = FirstDayOfSeedYear,
            IsActive = true,
        });
        context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = groupId, ValidFrom = ValidFrom });
        FindReplacementRuleSeed.AddObligation(context, clientId, Day);
    }

    private static void AddHomeWorks(DataBaseContext context, Guid clientId, Guid shiftId, int freeDayShift)
    {
        for (var windowDay = 0; windowDay <= HomeWorkHalfWindowDays + HomeWorkHalfWindowDays; windowDay++)
        {
            if ((windowDay + freeDayShift) % FreeEveryNthDay == 0)
            {
                continue;
            }

            context.Work.Add(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ShiftId = shiftId,
                CurrentDate = Day.AddDays(windowDay - HomeWorkHalfWindowDays),
                StartTime = ShiftStart,
                EndTime = ShiftEnd,
                WorkTime = ShiftHours,
            });
        }
    }
}
