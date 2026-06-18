// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests securing the scenario input-fidelity clone fixes (2026-06-18): the scenario
/// clone must carry the planning inputs the wizards read in scenario mode, and the scenario teardown
/// (Delete / Accept-Promote) must remove those clones again. Covers CommandKeys (schedule_commands),
/// required-qualification rows (C1, with shift-id remap), Lock-level preservation, and cleanup.
/// Uses far-future dates (2099) so the group-less clone only picks up the seeded test rows.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class AnalyseScenarioInputFidelityCloneTests
{
    private const string TestPrefix = "INTEGRATION_TEST_FIDELITY_";
    private static readonly DateOnly PeriodFrom = new(2099, 7, 6);
    private static readonly DateOnly PeriodUntil = new(2099, 7, 10);
    private static readonly DateOnly InPeriodDate = new(2099, 7, 8);

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
        _service = new AnalyseScenarioService(_context);
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
            DELETE FROM schedule_commands WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_required_qualification WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR qualification_id IN (SELECT id FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%');
            DELETE FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM client_shift_preference WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var c = new Client { Id = Guid.NewGuid(), Name = TestPrefix + suffix, FirstName = "Test", Company = string.Empty, LegalEntity = false };
        await _context.Set<Client>().AddAsync(c);
        await _context.SaveChangesAsync();
        return c;
    }

    private async Task<Shift> CreateShiftAsync(string suffix)
    {
        var s = new Shift
        {
            Id = Guid.NewGuid(), Name = TestPrefix + suffix, Abbreviation = "TST", Description = "Fidelity test",
            Status = ShiftStatus.OriginalShift, FromDate = new DateOnly(2099, 1, 1), UntilDate = null,
            StartShift = new TimeOnly(8, 0), EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            IsSaturday = true, IsSunday = true,
            ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 8m,
            AnalyseToken = null, ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(s);
        await _context.SaveChangesAsync();
        return s;
    }

    private async Task<Qualification> CreateQualificationAsync(string suffix)
    {
        var q = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = TestPrefix + suffix } };
        await _context.Set<Qualification>().AddAsync(q);
        await _context.SaveChangesAsync();
        return q;
    }

    [Test]
    public async Task Clone_Copies_ScheduleCommand_With_ScenarioToken()
    {
        var client = await CreateClientAsync("CMD");
        var shift = await CreateShiftAsync("CMD");
        _context.ScheduleCommands.Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            CurrentDate = InPeriodDate,
            CommandKeyword = "FREE",
            AnalyseToken = null
        });
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clonedCommands = await _context.ScheduleCommands.IgnoreQueryFilters()
            .CountAsync(sc => sc.AnalyseToken == token && sc.ClientId == client.Id && sc.CommandKeyword == "FREE" && !sc.IsDeleted);
        clonedCommands.ShouldBe(1, "the FREE CommandKey must be cloned into the scenario so wizards see it in scenario mode");
    }

    [Test]
    public async Task Clone_Copies_RequiredQualification_With_Remapped_ShiftId()
    {
        var shift = await CreateShiftAsync("QUAL");
        var qual = await CreateQualificationAsync("QUAL");
        _context.Set<ShiftRequiredQualification>().Add(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = shift.Id, QualificationId = qual.Id,
            IsMandatory = true, MinLevel = QualificationLevel.Proficient
        });
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneShiftId = await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.AnalyseToken == token && s.ScenarioSourceShiftId == shift.Id)
            .Select(s => s.Id)
            .FirstAsync();

        // The required-qualification row must be cloned and re-keyed onto the CLONE shift id — otherwise
        // EligibilityMatrixBuilder (which queries shift_required_qualification by the clone shift id)
        // returns an empty matrix and qualification matching silently disables in scenario mode (C1).
        var clonedRequirements = await _context.Set<ShiftRequiredQualification>().IgnoreQueryFilters()
            .CountAsync(srq => srq.ShiftId == cloneShiftId && srq.QualificationId == qual.Id && !srq.IsDeleted);
        clonedRequirements.ShouldBe(1, "shift_required_qualification must be cloned with the remapped clone shift id (C1)");
    }

    [Test]
    public async Task Clone_Preserves_LockLevel_On_Cloned_Work()
    {
        var client = await CreateClientAsync("LOCK");
        var shift = await CreateShiftAsync("LOCK");
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(), ClientId = client.Id, ShiftId = shift.Id, CurrentDate = InPeriodDate,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 0), WorkTime = 8m,
            LockLevel = WorkLockLevel.Confirmed, AnalyseToken = null
        });
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clonedLock = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.AnalyseToken == token && w.ClientId == client.Id && w.CurrentDate == InPeriodDate)
            .Select(w => w.LockLevel)
            .FirstAsync();
        clonedLock.ShouldBe(WorkLockLevel.Confirmed, "the cloned work must keep its lock level so the wizard treats it as immutable");
    }

    [Test]
    public async Task Delete_Removes_Cloned_Commands_And_RequiredQualifications()
    {
        var client = await CreateClientAsync("DEL");
        var shift = await CreateShiftAsync("DEL");
        var qual = await CreateQualificationAsync("DEL");
        _context.ScheduleCommands.Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(), ClientId = client.Id, CurrentDate = InPeriodDate, CommandKeyword = "FREE", AnalyseToken = null
        });
        _context.Set<ShiftRequiredQualification>().Add(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = shift.Id, QualificationId = qual.Id, IsMandatory = true, MinLevel = QualificationLevel.Proficient
        });
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        await _service.SoftDeleteScenarioDataAsync(token, CancellationToken.None);
        await _context.SaveChangesAsync();

        var leftoverCommands = await _context.ScheduleCommands.IgnoreQueryFilters()
            .CountAsync(sc => sc.AnalyseToken == token && !sc.IsDeleted);
        leftoverCommands.ShouldBe(0, "scenario-tagged CommandKeys must be soft-deleted on scenario delete");

        var cloneShiftIds = await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.AnalyseToken == token).Select(s => s.Id).ToListAsync();
        var leftoverRequirements = await _context.Set<ShiftRequiredQualification>().IgnoreQueryFilters()
            .CountAsync(srq => cloneShiftIds.Contains(srq.ShiftId) && !srq.IsDeleted);
        leftoverRequirements.ShouldBe(0, "scenario-tagged required-qualification clones must be soft-deleted on scenario delete");
    }

    [Test]
    public async Task Promote_Cleans_Cloned_Commands_And_Quals_And_Keeps_Real_Command()
    {
        var client = await CreateClientAsync("PROM");
        var shift = await CreateShiftAsync("PROM");
        var qual = await CreateQualificationAsync("PROM");
        _context.ScheduleCommands.Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(), ClientId = client.Id, CurrentDate = InPeriodDate, CommandKeyword = "FREE", AnalyseToken = null
        });
        _context.Set<ShiftRequiredQualification>().Add(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = shift.Id, QualificationId = qual.Id, IsMandatory = true, MinLevel = QualificationLevel.Proficient
        });
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, PeriodFrom, PeriodUntil, token, new[] { shift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        await _service.PromoteScenarioWorksAsync(token, PeriodFrom, PeriodUntil, CancellationToken.None);
        await _context.SaveChangesAsync();

        // Cloned command/qual rows are cleaned up (NOT promoted) — the real command on the token=null
        // ledger already exists, so promoting the clone would duplicate it.
        var leftoverScenarioCommands = await _context.ScheduleCommands.IgnoreQueryFilters()
            .CountAsync(sc => sc.AnalyseToken == token && !sc.IsDeleted);
        leftoverScenarioCommands.ShouldBe(0, "scenario-tagged CommandKeys must be cleaned up on accept/promote");

        var realCommands = await _context.ScheduleCommands.IgnoreQueryFilters()
            .CountAsync(sc => sc.AnalyseToken == null && sc.ClientId == client.Id && sc.CommandKeyword == "FREE" && !sc.IsDeleted);
        realCommands.ShouldBe(1, "the real CommandKey must survive accept (commands live on the real ledger, are never promoted)");
    }
}
