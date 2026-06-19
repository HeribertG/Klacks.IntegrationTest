using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.Wizard.Spec;
using Microsoft.EntityFrameworkCore;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// The single reusable, parameterizable seeder. Seeds one Client + Contract + active
/// ClientContract per client plus the shift pool (and an optional shared scheduling rule) using
/// DETERMINISTIC index-derived GUIDs so the context builder's id-driven ordering is stable across
/// runs. Cleanup hard-deletes the seeded rows (bypassing soft-delete) in FK-child-first order.
/// </summary>
/// <param name="context">The DataBaseContext owned by the test base.</param>
public sealed class WizardScenarioBuilder(DataBaseContext context)
{
    private const string ClientGuidPrefix = "00000000-0000-0000-0001-";
    private const string ContractGuidPrefix = "00000000-0000-0000-0002-";
    private const string ClientContractGuidPrefix = "00000000-0000-0000-0003-";
    private const string ShiftGuidPrefix = "00000000-0000-0000-0004-";
    private const string SchedulingRuleGuid = "00000000-0000-0000-0005-000000000001";
    private const string QualificationGuidPrefix = "00000000-0000-0000-0006-";
    private const string ShiftRequiredQualGuidPrefix = "00000000-0000-0000-0007-";
    private const string ClientQualGuidPrefix = "00000000-0000-0000-0008-";
    private const string ScheduleCommandGuidPrefix = "00000000-0000-0000-0009-";
    private const string LockedWorkGuidPrefix = "00000000-0000-0000-000a-";
    private const string BreakGuidPrefix = "00000000-0000-0000-000b-";
    private const string AbsenceGuid = "00000000-0000-0000-000c-000000000001";

    private static readonly DateOnly ContractStart = new(2090, 1, 1);
    private static readonly DateTime ContractValidFrom = new(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly DataBaseContext _context = context;

    private readonly List<Guid> _clientIds = [];
    private readonly List<Guid> _contractIds = [];
    private readonly List<Guid> _clientContractIds = [];
    private readonly List<Guid> _shiftIds = [];
    private Guid? _schedulingRuleId;
    private int _requiredQualCounter;
    private int _clientQualCounter;

    public async Task<WizardScenarioContext> SeedAsync(WizardScenarioSpec spec)
    {
        await PurgeDeterministicAsync();

        Guid? schedulingRuleId = null;
        if (spec.SchedulingRule is { } ruleSpec)
        {
            schedulingRuleId = Guid.Parse(SchedulingRuleGuid);
            _schedulingRuleId = schedulingRuleId;
            _context.SchedulingRules.Add(new SchedulingRule
            {
                Id = schedulingRuleId.Value,
                Name = "TEST_WizCov_Rule",
                MaxConsecutiveDays = ruleSpec.MaxConsecutiveDays,
                MinPauseHours = ruleSpec.MinPauseHours,
                MaxDailyHours = ruleSpec.MaxDailyHours,
                MaxWeeklyHours = ruleSpec.MaxWeeklyHours,
                MaxWorkDays = ruleSpec.MaxWorkDays,
                MinRestDays = ruleSpec.MinRestDays,
                IsDeleted = false,
            });
        }

        for (var i = 0; i < spec.ClientCount; i++)
        {
            var clientId = IndexedGuid(ClientGuidPrefix, i);
            var contractId = IndexedGuid(ContractGuidPrefix, i);
            var clientContractId = IndexedGuid(ClientContractGuidPrefix, i);

            _clientIds.Add(clientId);
            _contractIds.Add(contractId);
            _clientContractIds.Add(clientContractId);

            _context.Client.Add(new Client
            {
                Id = clientId,
                Name = $"TEST_WizCov_{i}",
                FirstName = "Integration",
                Type = EntityTypeEnum.Employee,
                IsDeleted = false,
            });

            _context.Contract.Add(new Contract
            {
                Id = contractId,
                Name = $"TEST_WizCov_Contract_{i}",
                ValidFrom = ContractValidFrom,
                ValidUntil = null,
                GuaranteedHours = spec.GuaranteedHoursPerClient(i),
                MaximumHours = spec.MaximumHoursOrZero(i),
                FullTime = spec.FullTimeHours,
                PerformsShiftWork = spec.PerformsShiftWork,
                WorkOnMonday = spec.ContractWorkDays[0],
                WorkOnTuesday = spec.ContractWorkDays[1],
                WorkOnWednesday = spec.ContractWorkDays[2],
                WorkOnThursday = spec.ContractWorkDays[3],
                WorkOnFriday = spec.ContractWorkDays[4],
                WorkOnSaturday = spec.ContractWorkDays[5],
                WorkOnSunday = spec.ContractWorkDays[6],
                SchedulingRuleId = schedulingRuleId,
                IsDeleted = false,
            });

            _context.ClientContract.Add(new ClientContract
            {
                Id = clientContractId,
                ClientId = clientId,
                ContractId = contractId,
                FromDate = ContractStart,
                UntilDate = null,
                IsActive = true,
            });
        }

        var shiftIdByAbbrev = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var shiftDefByAbbrev = new Dictionary<string, ShiftDef>(StringComparer.OrdinalIgnoreCase);

        for (var s = 0; s < spec.ShiftDefs.Count; s++)
        {
            var def = spec.ShiftDefs[s];
            var shiftId = IndexedGuid(ShiftGuidPrefix, s);
            _shiftIds.Add(shiftId);
            shiftIdByAbbrev[def.Abbrev] = shiftId;
            shiftDefByAbbrev[def.Abbrev] = def;

            _context.Shift.Add(new Shift
            {
                Id = shiftId,
                Name = def.Abbrev,
                Abbreviation = def.Abbrev,
                StartShift = def.StartShift,
                EndShift = def.EndShift,
                WorkTime = def.WorkTime,
                FromDate = ContractStart,
                UntilDate = null,
                IsMonday = def.Weekdays[0],
                IsTuesday = def.Weekdays[1],
                IsWednesday = def.Weekdays[2],
                IsThursday = def.Weekdays[3],
                IsFriday = def.Weekdays[4],
                IsSaturday = def.Weekdays[5],
                IsSunday = def.Weekdays[6],
                Quantity = def.Quantity,
                CuttingAfterMidnight = def.CuttingAfterMidnight,
                Status = ShiftStatus.OriginalOrder,
                IsDeleted = false,
            });
        }

        var qualifications = spec.QualificationsOrEmpty;
        for (var q = 0; q < qualifications.Count; q++)
        {
            var qualSpec = qualifications[q];
            var qualificationId = IndexedGuid(QualificationGuidPrefix, q);

            var name = new MultiLanguage();
            name.SetValue("en", qualSpec.Name);

            _context.Qualification.Add(new Qualification
            {
                Id = qualificationId,
                Name = name,
                Type = QualificationType.Work,
                IsDeleted = false,
            });

            for (var m = 0; m < qualSpec.MandatoryForShiftAbbrevs.Count; m++)
            {
                var abbrev = qualSpec.MandatoryForShiftAbbrevs[m];
                if (!shiftIdByAbbrev.TryGetValue(abbrev, out var reqShiftId))
                {
                    continue;
                }

                _context.ShiftRequiredQualification.Add(new ShiftRequiredQualification
                {
                    Id = IndexedGuid(ShiftRequiredQualGuidPrefix, _requiredQualCounter++),
                    ShiftId = reqShiftId,
                    QualificationId = qualificationId,
                    IsMandatory = true,
                    MinLevel = qualSpec.MinLevel,
                    IsDeleted = false,
                });
            }

            for (var h = 0; h < qualSpec.HeldByClientIndexes.Count; h++)
            {
                var clientIndex = qualSpec.HeldByClientIndexes[h];
                if (clientIndex < 0 || clientIndex >= _clientIds.Count)
                {
                    continue;
                }

                _context.ClientQualification.Add(new ClientQualification
                {
                    Id = IndexedGuid(ClientQualGuidPrefix, _clientQualCounter++),
                    ClientId = _clientIds[clientIndex],
                    QualificationId = qualificationId,
                    Level = qualSpec.HeldLevel,
                    IsDeleted = false,
                });
            }
        }

        var scheduleCommands = spec.ScheduleCommandsOrEmpty;
        for (var c = 0; c < scheduleCommands.Count; c++)
        {
            var cmdSpec = scheduleCommands[c];
            if (cmdSpec.ClientIndex < 0 || cmdSpec.ClientIndex >= _clientIds.Count)
            {
                continue;
            }

            _context.ScheduleCommands.Add(new ScheduleCommand
            {
                Id = IndexedGuid(ScheduleCommandGuidPrefix, c),
                ClientId = _clientIds[cmdSpec.ClientIndex],
                CurrentDate = cmdSpec.Date,
                CommandKeyword = cmdSpec.CommandKeyword,
                AnalyseToken = null,
            });
        }

        var lockedWorks = spec.LockedWorksOrEmpty;
        for (var w = 0; w < lockedWorks.Count; w++)
        {
            var workSpec = lockedWorks[w];
            if (workSpec.ClientIndex < 0 || workSpec.ClientIndex >= _clientIds.Count)
            {
                continue;
            }

            if (!shiftIdByAbbrev.TryGetValue(workSpec.ShiftAbbrev, out var workShiftId)
                || !shiftDefByAbbrev.TryGetValue(workSpec.ShiftAbbrev, out var workShiftDef))
            {
                continue;
            }

            _context.Work.Add(new Work
            {
                Id = IndexedGuid(LockedWorkGuidPrefix, w),
                ClientId = _clientIds[workSpec.ClientIndex],
                CurrentDate = workSpec.Date,
                ShiftId = workShiftId,
                StartTime = workShiftDef.StartShift,
                EndTime = workShiftDef.EndShift,
                WorkTime = workShiftDef.WorkTime,
                LockLevel = workSpec.LockLevel,
                ParentWorkId = null,
                AnalyseToken = null,
                IsDeleted = false,
            });
        }

        var breaks = spec.BreaksOrEmpty;
        if (breaks.Count > 0)
        {
            var absenceName = new MultiLanguage();
            absenceName.SetValue("en", "TEST_WizCov_Absence");

            var absenceAbbreviation = new MultiLanguage();
            absenceAbbreviation.SetValue("en", "TWA");

            var absenceDescription = new MultiLanguage();
            absenceDescription.SetValue("en", "Wizard coverage break absence");

            _context.Absence.Add(new Absence
            {
                Id = Guid.Parse(AbsenceGuid),
                Name = absenceName,
                Abbreviation = absenceAbbreviation,
                Description = absenceDescription,
                Color = "#888888",
                IsDeleted = false,
            });

            for (var b = 0; b < breaks.Count; b++)
            {
                var breakSpec = breaks[b];
                if (breakSpec.ClientIndex < 0 || breakSpec.ClientIndex >= _clientIds.Count)
                {
                    continue;
                }

                _context.Break.Add(new Break
                {
                    Id = IndexedGuid(BreakGuidPrefix, b),
                    ClientId = _clientIds[breakSpec.ClientIndex],
                    CurrentDate = breakSpec.Date,
                    WorkTime = breakSpec.WorkTime,
                    AbsenceId = Guid.Parse(AbsenceGuid),
                    ParentWorkId = null,
                    AnalyseToken = null,
                    IsDeleted = false,
                });
            }
        }

        await _context.SaveChangesAsync();

        var request = new WizardContextRequest(
            PeriodFrom: spec.PeriodFrom,
            PeriodUntil: spec.PeriodUntil,
            AgentIds: _clientIds.ToList(),
            ShiftIds: _shiftIds.ToList(),
            AnalyseToken: null);

        return new WizardScenarioContext(
            AgentIds: _clientIds.ToList(),
            ShiftIds: _shiftIds.ToList(),
            ContextRequest: request);
    }

    /// <summary>
    /// Hard-DELETEs every deterministically-prefixed seed row (bypassing soft-delete), in
    /// FK-child-first order. Delegates to <see cref="PurgeDeterministicAsync"/> so teardown and the
    /// idempotent top-of-seed purge share one source of truth.
    /// </summary>
    public Task CleanupAsync() => PurgeDeterministicAsync();

    /// <summary>
    /// Hard-DELETEs all rows whose primary key (or, for junctions, foreign key) carries one of the
    /// builder's deterministic GUID prefixes, in FK-child-first order: break, work,
    /// schedule_commands, client_qualification, shift_required_qualification, shift, qualification,
    /// absence, client_contract, contract, scheduling_rules, client. Prefix-based (not list-based)
    /// so a crashed prior run with skipped teardown cannot cause a primary-key violation on re-seed,
    /// even though a fresh builder's id lists are empty.
    /// </summary>
    private async Task PurgeDeterministicAsync()
    {
        await DeleteByPrefixAsync("break", "id", BreakGuidPrefix);
        await DeleteByPrefixAsync("work", "id", LockedWorkGuidPrefix);
        await DeleteByPrefixAsync("schedule_commands", "id", ScheduleCommandGuidPrefix);
        await DeleteByPrefixAsync("client_qualification", "id", ClientQualGuidPrefix);
        await DeleteByPrefixAsync("shift_required_qualification", "id", ShiftRequiredQualGuidPrefix);
        await DeleteByPrefixAsync("shift", "id", ShiftGuidPrefix);
        await DeleteByPrefixAsync("qualification", "id", QualificationGuidPrefix);
        await DeleteByPrefixAsync("absence", "id", AbsencePurgePrefix);
        await DeleteByPrefixAsync("client_contract", "id", ClientContractGuidPrefix);
        await DeleteByPrefixAsync("contract", "id", ContractGuidPrefix);
        await DeleteByPrefixAsync("scheduling_rules", "id", SchedulingRulePurgePrefix);
        await DeleteByPrefixAsync("client", "id", ClientGuidPrefix);
    }

    private const string AbsencePurgePrefix = "00000000-0000-0000-000c-";
    private const string SchedulingRulePurgePrefix = "00000000-0000-0000-0005-";

    private async Task DeleteByPrefixAsync(string table, string column, string prefix)
    {
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM {table} WHERE {column}::text LIKE {{0}}", prefix + "%");
    }

    private static Guid IndexedGuid(string prefix, int index) => Guid.Parse($"{prefix}{index:D12}");
}
