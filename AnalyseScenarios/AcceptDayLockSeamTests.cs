// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Seam test (real Postgres) for the day lock of the accept: accepting a scenario through
/// AcceptAnalyseScenarioCommandHandler leaves sealed days untouched. Real works, breaks and notes on a globally or
/// group-sealed day survive unchanged although the scenario changed them, a scenario work added on a sealed day is not
/// promoted, unlocked days of the same scenario are accepted normally, a slot moved away from a sealed client is not
/// half applied, and a locked break is kept without being duplicated. A replacement with an employee whose day is sealed
/// is neither written nor removed. The result reports the entries not taken over, and the staleness check before the
/// accept ignores changes the accept leaves in place.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class AcceptDayLockSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_ACCEPTDAYLOCK_";
    private const string OriginalText = "original";
    private const string ChangedText = "changed";
    private static readonly DateOnly PeriodFrom = new(2099, 7, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 7, 31);
    private static readonly DateOnly SealedDate = new(2099, 7, 6);
    private static readonly DateOnly OpenDate = new(2099, 7, 8);
    private static readonly TimeOnly Start = new(8, 0);
    private static readonly TimeOnly End = new(16, 0);
    private static readonly TimeOnly ChangedStart = new(9, 0);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;

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
        _service = AnalyseScenarioTestServiceFactory.Create(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task Accept_GlobalSeal_KeepsTheSealedDay_AndAcceptsTheOpenDay()
    {
        var (group, shift) = await CreateGroupWithShiftAsync("G1");
        var client = await CreateClientAsync("C1", group.Id);
        var absence = await CreateAbsenceAsync("A1");
        var sealedWork = await AddRealWorkAsync(client.Id, shift.Id, SealedDate);
        var openWork = await AddRealWorkAsync(client.Id, shift.Id, OpenDate);
        var sealedBreak = await AddRealBreakAsync(client.Id, absence.Id, SealedDate.AddDays(1), WorkLockLevel.None);
        var sealedNote = await AddRealNoteAsync(client.Id, SealedDate);
        await SealAsync(SealedDate, null);
        await SealAsync(SealedDate.AddDays(1), null);

        var scenario = await CreateScenarioAsync(group.Id, shift.Id);
        await ChangeScenarioAsync(scenario.Token);
        var cloneShiftId = await _context.Shift.Where(s => s.AnalyseToken == scenario.Token).Select(s => s.Id).SingleAsync();
        var addedClient = await CreateClientAsync("C1ADDED", group.Id);
        await _context.Work.AddAsync(NewWork(addedClient.Id, cloneShiftId, SealedDate, scenario.Token));
        await _context.SaveChangesAsync();

        var result = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        result.ShouldBe(new AcceptAnalyseScenarioResult(true, 2, 1, 1));
        await using var verify = NewContext();
        var onSealedDay = await LiveRealWorksAsync(verify, SealedDate, client.Id, addedClient.Id);
        onSealedDay.ShouldHaveSingleItem().Id.ShouldBe(sealedWork.Id);
        onSealedDay[0].StartTime.ShouldBe(Start);
        var liveBreak = (await verify.Break.Where(b => b.ClientId == client.Id && b.AnalyseToken == null).ToListAsync())
            .ShouldHaveSingleItem();
        liveBreak.Id.ShouldBe(sealedBreak.Id);
        liveBreak.EndTime.ShouldBe(End);
        var liveNote = (await verify.ScheduleNotes.Where(n => n.ClientId == client.Id && n.AnalyseToken == null).ToListAsync())
            .ShouldHaveSingleItem();
        liveNote.Id.ShouldBe(sealedNote.Id);
        liveNote.Content.ShouldBe(TestPrefix + OriginalText);

        var onOpenDay = await LiveRealWorksAsync(verify, OpenDate, client.Id);
        onOpenDay.ShouldHaveSingleItem().Id.ShouldNotBe(openWork.Id);
        onOpenDay[0].StartTime.ShouldBe(ChangedStart);
        onOpenDay[0].ShiftId.ShouldBe(shift.Id);
    }

    [Test]
    public async Task Accept_GroupSeal_KeepsTheWorkOfTheSealedWorker_AndDoesNotHalfApplyAMove()
    {
        var (group, shift) = await CreateGroupWithShiftAsync("G2");
        var worker = await CreateClientAsync("C2", group.Id);
        var other = await CreateClientAsync("C2OTHER", group.Id);
        var sealedWork = await AddRealWorkAsync(worker.Id, shift.Id, SealedDate);
        var openWork = await AddRealWorkAsync(worker.Id, shift.Id, OpenDate);
        await SealAsync(SealedDate, group.Id);

        var scenario = await CreateScenarioAsync(group.Id, shift.Id);
        foreach (var clone in await _context.Work.Where(w => w.AnalyseToken == scenario.Token && w.CurrentDate == SealedDate).ToListAsync())
        {
            clone.ClientId = other.Id;
        }
        foreach (var clone in await _context.Work.Where(w => w.AnalyseToken == scenario.Token && w.CurrentDate == OpenDate).ToListAsync())
        {
            clone.StartTime = ChangedStart;
        }
        await _context.SaveChangesAsync();

        var result = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        result.ShouldBe(new AcceptAnalyseScenarioResult(true, 1, 0, 0));
        await using var verify = NewContext();
        (await LiveRealWorksAsync(verify, SealedDate, worker.Id, other.Id)).ShouldHaveSingleItem().Id.ShouldBe(sealedWork.Id);
        var onOpenDay = await LiveRealWorksAsync(verify, OpenDate, worker.Id, other.Id);
        onOpenDay.ShouldHaveSingleItem().Id.ShouldNotBe(openWork.Id);
        onOpenDay[0].StartTime.ShouldBe(ChangedStart);
    }

    [Test]
    public async Task Accept_LockedBreak_IsKeptAndNotDuplicated()
    {
        var (group, shift) = await CreateGroupWithShiftAsync("G3");
        var client = await CreateClientAsync("C3", group.Id);
        var absence = await CreateAbsenceAsync("A3");
        await AddRealWorkAsync(client.Id, shift.Id, OpenDate);
        var lockedBreak = await AddRealBreakAsync(client.Id, absence.Id, OpenDate.AddDays(1), WorkLockLevel.Confirmed);

        var scenario = await CreateScenarioAsync(group.Id, shift.Id);

        var result = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        result.SkippedLockedTotal.ShouldBe(0);
        await using var verify = NewContext();
        (await verify.Break.Where(b => b.ClientId == client.Id && b.AnalyseToken == null).ToListAsync())
            .ShouldHaveSingleItem().Id.ShouldBe(lockedBreak.Id);
    }

    [Test]
    public async Task Accept_ScenarioReplacementWithAnEmployeeWhoseDayIsSealed_IsNotTakenOver()
    {
        var (group, shift) = await CreateGroupWithShiftAsync("G4");
        var (replacerGroup, _) = await CreateGroupWithShiftAsync("G4R");
        var worker = await CreateClientAsync("C4", group.Id);
        var replacer = await CreateClientAsync("C4R", replacerGroup.Id);
        var realWork = await AddRealWorkAsync(worker.Id, shift.Id, SealedDate);
        await SealAsync(SealedDate, replacerGroup.Id);

        var scenario = await CreateScenarioAsync(group.Id, shift.Id);
        var clone = await _context.Work.SingleAsync(w => w.AnalyseToken == scenario.Token && w.CurrentDate == SealedDate);
        await _context.WorkChange.AddAsync(NewReplacement(clone.Id, replacer.Id, scenario.Token));
        await _context.SaveChangesAsync();

        var result = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        result.SkippedLockedWorkChanges.ShouldBe(1);
        result.SkippedLockedTotal.ShouldBe(1);
        await using var verify = NewContext();
        (await LiveRealWorksAsync(verify, SealedDate, worker.Id)).ShouldHaveSingleItem().Id.ShouldBe(realWork.Id);
        (await verify.WorkChange.CountAsync(wc => wc.AnalyseToken == null && wc.ReplaceClientId == replacer.Id))
            .ShouldBe(0, "the replacer's sealed day must not receive the scenario's replacement");
    }

    [Test]
    public async Task Accept_RealReplacementWithAnEmployeeWhoseDayIsSealed_IsNotRemoved()
    {
        var (group, shift) = await CreateGroupWithShiftAsync("G5");
        var (replacerGroup, _) = await CreateGroupWithShiftAsync("G5R");
        var worker = await CreateClientAsync("C5", group.Id);
        var replacer = await CreateClientAsync("C5R", replacerGroup.Id);
        var realWork = await AddRealWorkAsync(worker.Id, shift.Id, SealedDate);
        var realReplacement = NewReplacement(realWork.Id, replacer.Id, null);
        await _context.WorkChange.AddAsync(realReplacement);
        await _context.SaveChangesAsync();
        await SealAsync(SealedDate, replacerGroup.Id);

        var scenario = await CreateScenarioAsync(group.Id, shift.Id);
        var clone = await _context.Work.SingleAsync(w => w.AnalyseToken == scenario.Token && w.CurrentDate == SealedDate);
        _context.WorkChange.Remove(await _context.WorkChange.SingleAsync(wc => wc.WorkId == clone.Id));
        await _context.SaveChangesAsync();

        var result = await Handler().Handle(new AcceptAnalyseScenarioCommand(scenario.Id), CancellationToken.None);

        result.SkippedLockedWorkChanges.ShouldBe(1, "the scenario removed the replacement, which stays");
        await using var verify = NewContext();
        (await LiveRealWorksAsync(verify, SealedDate, worker.Id)).ShouldHaveSingleItem().Id.ShouldBe(realWork.Id);
        (await verify.WorkChange.Where(wc => wc.AnalyseToken == null && wc.WorkId == realWork.Id).ToListAsync())
            .ShouldHaveSingleItem().Id.ShouldBe(realReplacement.Id);
    }

    [Test]
    public async Task Staleness_IgnoresChangesOnASealedDay_ButReportsChangesOnAnOpenDay()
    {
        var (group, shift) = await CreateGroupWithShiftAsync("G6");
        var client = await CreateClientAsync("C6", group.Id);
        var sealedWork = await AddRealWorkAsync(client.Id, shift.Id, SealedDate);
        var openWork = await AddRealWorkAsync(client.Id, shift.Id, OpenDate);
        await SealAsync(SealedDate, group.Id);
        var scenario = await CreateScenarioAsync(group.Id, shift.Id);

        sealedWork.Information = TestPrefix + ChangedText;
        await _context.Expenses.AddAsync(new Expenses { Id = Guid.NewGuid(), WorkId = sealedWork.Id, Amount = 12m, Description = TestPrefix });
        openWork.Information = TestPrefix + ChangedText;
        await _context.SaveChangesAsync();

        var staleness = await _service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None);

        staleness.ShouldNotBeNull();
        staleness.ChangedEntryCount.ShouldBe(1, "only the open-day edit is overwritten by the accept");
    }

    private static WorkChange NewReplacement(Guid workId, Guid replaceClientId, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = workId,
        Type = WorkChangeType.ReplacementWithin,
        ReplaceClientId = replaceClientId,
        StartTime = Start,
        EndTime = End,
        ChangeTime = 8m,
        Description = TestPrefix,
        AnalyseToken = token
    };

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
            DELETE FROM expenses WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%'));
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM schedule_notes WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM sealed_day WHERE reason LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM absence WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<(Group Group, Shift Shift)> CreateGroupWithShiftAsync(string suffix)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = null
        };
        await _context.Set<Group>().AddAsync(group);
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "accept day lock seam",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = Start,
            EndShift = End,
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        };
        await _context.Shift.AddAsync(shift);
        await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = group.Id, ShiftId = shift.Id });
        await _context.SaveChangesAsync();
        return (group, shift);
    }

    private async Task<Client> CreateClientAsync(string suffix, Guid groupId)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false,
            Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ValidUntil = null
            }
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ClientId = client.Id });
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Absence> CreateAbsenceAsync(string suffix)
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = TestPrefix + suffix },
            Abbreviation = new MultiLanguage { De = "TST" },
            Description = new MultiLanguage { De = "accept day lock absence" },
            Color = "#cccccc"
        };
        await _context.Set<Absence>().AddAsync(absence);
        await _context.SaveChangesAsync();
        return absence;
    }

    private static Work NewWork(Guid clientId, Guid shiftId, DateOnly date, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = shiftId,
        CurrentDate = date,
        StartTime = Start,
        EndTime = End,
        WorkTime = 8m,
        AnalyseToken = token
    };

    private async Task<Work> AddRealWorkAsync(Guid clientId, Guid shiftId, DateOnly date)
    {
        var work = NewWork(clientId, shiftId, date, null);
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
        return work;
    }

    private async Task<Break> AddRealBreakAsync(Guid clientId, Guid absenceId, DateOnly date, WorkLockLevel lockLevel)
    {
        var brk = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = date,
            StartTime = Start,
            EndTime = End,
            WorkTime = 8m,
            LockLevel = lockLevel
        };
        await _context.Break.AddAsync(brk);
        await _context.SaveChangesAsync();
        return brk;
    }

    private async Task<ScheduleNote> AddRealNoteAsync(Guid clientId, DateOnly date)
    {
        var note = new ScheduleNote { Id = Guid.NewGuid(), ClientId = clientId, CurrentDate = date, Content = TestPrefix + OriginalText };
        await _context.ScheduleNotes.AddAsync(note);
        await _context.SaveChangesAsync();
        return note;
    }

    private async Task SealAsync(DateOnly date, Guid? groupId)
    {
        await _context.SealedDay.AddAsync(new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = date,
            GroupId = groupId,
            Level = WorkLockLevel.Closed,
            Reason = TestPrefix + "seal",
            SealedAt = DateTime.UtcNow,
            SealedBy = TestPrefix
        });
        await _context.SaveChangesAsync();
    }

    private async Task<AnalyseScenario> CreateScenarioAsync(Guid groupId, Guid shiftId)
    {
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SCENARIO",
            Token = Guid.NewGuid(),
            GroupId = groupId,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active
        };
        await _context.Set<AnalyseScenario>().AddAsync(scenario);
        await _context.SaveChangesAsync();
        await _service.CloneScenarioDataWithMapsAsync(
            groupId, PeriodFrom, PeriodUntil, scenario.Token, new[] { shiftId }, null, CancellationToken.None);
        await _context.SaveChangesAsync();
        return scenario;
    }

    private async Task ChangeScenarioAsync(Guid token)
    {
        foreach (var work in await _context.Work.Where(w => w.AnalyseToken == token).ToListAsync())
        {
            work.StartTime = ChangedStart;
        }
        foreach (var brk in await _context.Break.Where(b => b.AnalyseToken == token).ToListAsync())
        {
            brk.EndTime = ChangedStart;
        }
        foreach (var note in await _context.ScheduleNotes.Where(n => n.AnalyseToken == token).ToListAsync())
        {
            note.Content = TestPrefix + ChangedText;
        }
        await _context.SaveChangesAsync();
    }

    private static Task<List<Work>> LiveRealWorksAsync(DataBaseContext context, DateOnly date, params Guid[] clientIds)
        => context.Work
            .Where(w => w.AnalyseToken == null && w.CurrentDate == date && clientIds.Contains(w.ClientId))
            .ToListAsync();

    private AcceptAnalyseScenarioCommandHandler Handler()
    {
        var repo = new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>());
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        var compliance = Substitute.For<Klacks.Api.Application.Interfaces.Schedules.IScenarioComplianceService>();
        compliance
            .EvaluateAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ScenarioComplianceReport([], []));
        return new AcceptAnalyseScenarioCommandHandler(
            repo, _service, unitOfWork, Substitute.For<IWorkSofteningRepository>(),
            compliance,
            Substitute.For<Klacks.Api.Application.Interfaces.Schedules.ISupervisorOverrideAuthorizer>(),
            Substitute.For<Klacks.Api.Application.Interfaces.IScheduleTimelineService>(),
            new Klacks.Api.Infrastructure.Repositories.Assistant.AgentConditionRepository(_context),
            new Klacks.Api.Application.Services.Assistant.Conditions.AgentConditionLedgerService(
                new Klacks.Api.Infrastructure.Repositories.Assistant.AgentConditionRepository(_context),
                TimeProvider.System,
                Substitute.For<ILogger<Klacks.Api.Application.Services.Assistant.Conditions.AgentConditionLedgerService>>()),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Schedules.IPeriodHoursService>(),
            Substitute.For<Klacks.Api.Application.Interfaces.IWorkNotificationService>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Assistant.IEscalationChainService>(),
            Klacks.IntegrationTest.TestHelpers.UnrestrictedGroupVisibility.Create(),
            Substitute.For<ILogger<AcceptAnalyseScenarioCommandHandler>>());
    }
}
