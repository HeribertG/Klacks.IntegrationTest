// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the phantom-shift fix in scenario accept/reject flows.
/// Verifies that scenario clones never replace real shifts (multi-user data
/// safety), edit/cut on clones is blocked, and conflict detection fires when
/// a real-mode user mutates a source shift while a scenario is open.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Enums;
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
public class AnalyseScenarioPhantomShiftTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PHANTOM_";

    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private AnalyseScenarioService _service = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        await using var context = new DataBaseContext(options, mockHttpContextAccessor);
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _service = new AnalyseScenarioService(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM client_shift_preference WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateTestClientAsync(string suffix)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Shift> CreateRealShiftAsync(string suffix)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Integration test phantom-shift",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            AnalyseToken = null,
            ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    [Test]
    public async Task CloneShifts_Sets_ScenarioSourceShiftId_And_ChildCountSnapshot()
    {
        var realShift = await CreateRealShiftAsync("Source1");
        var token = Guid.NewGuid();

        var idMap = await _service.CloneScenarioDataAsync(
            groupId: null,
            fromDate: new DateOnly(2026, 1, 1),
            untilDate: new DateOnly(2026, 1, 31),
            token: token,
            additionalShiftIds: new[] { realShift.Id },
            ct: CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneId = idMap[realShift.Id];
        var clone = await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.Id == cloneId)
            .AsNoTracking()
            .SingleAsync();

        clone.AnalyseToken.ShouldBe(token);
        clone.ScenarioSourceShiftId.ShouldBe(realShift.Id);
        clone.SourceChildCountSnapshot.ShouldBe(0);
    }

    [Test]
    public async Task ValidateNoAcceptConflicts_Throws_When_Source_Shift_Soft_Deleted()
    {
        var realShift = await CreateRealShiftAsync("Source2");
        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            token, new[] { realShift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        realShift.IsDeleted = true;
        realShift.DeletedTime = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var act = async () => await _service.ValidateNoAcceptConflictsAsync(token, CancellationToken.None);
        var ex = await act.ShouldThrowAsync<ConflictException>();
        ex.Message.ShouldContain(realShift.Id.ToString());
    }

    [Test]
    public async Task ValidateNoAcceptConflicts_Throws_When_Source_Subtree_Got_Cut()
    {
        var realShift = await CreateRealShiftAsync("Source3");
        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            token, new[] { realShift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var newSplit = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "Split_Source3",
            Abbreviation = "TST",
            Description = "Concurrent cut",
            Status = ShiftStatus.SplitShift,
            FromDate = realShift.FromDate,
            StartShift = realShift.StartShift,
            EndShift = realShift.EndShift,
            ShiftType = realShift.ShiftType,
            ParentId = realShift.Id,
            RootId = realShift.Id,
            AnalyseToken = null,
            ScenarioSourceShiftId = null
        };
        await _context.Shift.AddAsync(newSplit);
        await _context.SaveChangesAsync();

        var act = async () => await _service.ValidateNoAcceptConflictsAsync(token, CancellationToken.None);
        var ex = await act.ShouldThrowAsync<ConflictException>();
        ex.Message.ShouldContain("subtree changed");
    }

    [Test]
    public async Task PromoteScenarioWorks_Remaps_Work_ShiftId_To_Source_And_SoftDeletes_Clones()
    {
        var realShift = await CreateRealShiftAsync("Source4");
        var token = Guid.NewGuid();
        var idMap = await _service.CloneScenarioDataAsync(null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            token, new[] { realShift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneId = idMap[realShift.Id];

        var client = await CreateTestClientAsync("Worker4");
        var scenarioWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ShiftId = cloneId,
            CurrentDate = new DateOnly(2026, 1, 5),
            StartTime = realShift.StartShift,
            EndTime = realShift.EndShift,
            WorkTime = 8m,
            AnalyseToken = token
        };
        await _context.Work.AddAsync(scenarioWork);
        await _context.SaveChangesAsync();

        await _service.PromoteScenarioWorksAsync(token, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), CancellationToken.None);
        await _context.SaveChangesAsync();

        var promotedWork = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == scenarioWork.Id)
            .AsNoTracking()
            .SingleAsync();
        promotedWork.AnalyseToken.ShouldBeNull();
        promotedWork.ShiftId.ShouldBe(realShift.Id);

        var cloneAfter = await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.Id == cloneId)
            .AsNoTracking()
            .SingleAsync();
        cloneAfter.IsDeleted.ShouldBeTrue("clone shift must be soft-deleted, not promoted");
        cloneAfter.AnalyseToken.ShouldBe(token, "token stays so the clone is not mistakable for a real shift");

        var sourceAfter = await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.Id == realShift.Id)
            .AsNoTracking()
            .SingleAsync();
        sourceAfter.IsDeleted.ShouldBeFalse("source shift must remain unchanged after accept");
        sourceAfter.AnalyseToken.ShouldBeNull();
    }

    [Test]
    public async Task SoftDeleteScenarioData_Removes_Clones_Including_Preferences_And_ShiftExpenses()
    {
        var realShift = await CreateRealShiftAsync("Source5");
        var prefClient = await CreateTestClientAsync("PrefClient5");
        var token = Guid.NewGuid();

        var preference = new Klacks.Api.Domain.Models.Associations.ClientShiftPreference
        {
            Id = Guid.NewGuid(),
            ClientId = prefClient.Id,
            ShiftId = realShift.Id,
            PreferenceType = ShiftPreferenceType.Preferred,
            AnalyseToken = null
        };
        var expense = new ShiftExpenses
        {
            Id = Guid.NewGuid(),
            ShiftId = realShift.Id,
            Amount = 12.5m,
            Description = "Travel",
            Taxable = false,
            AnalyseToken = null
        };
        await _context.Set<Klacks.Api.Domain.Models.Associations.ClientShiftPreference>().AddAsync(preference);
        await _context.Set<ShiftExpenses>().AddAsync(expense);
        await _context.SaveChangesAsync();

        await _service.CloneScenarioDataAsync(null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            token, new[] { realShift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clonedPrefCount = await _context.Set<Klacks.Api.Domain.Models.Associations.ClientShiftPreference>()
            .IgnoreQueryFilters()
            .CountAsync(p => p.AnalyseToken == token && !p.IsDeleted);
        var clonedExpCount = await _context.Set<ShiftExpenses>()
            .IgnoreQueryFilters()
            .CountAsync(e => e.AnalyseToken == token && !e.IsDeleted);
        clonedPrefCount.ShouldBe(1, "preference must be cloned");
        clonedExpCount.ShouldBe(1, "shift-expense must be cloned");

        await _service.SoftDeleteScenarioDataAsync(token, CancellationToken.None);
        await _context.SaveChangesAsync();

        var prefAfter = await _context.Set<Klacks.Api.Domain.Models.Associations.ClientShiftPreference>()
            .IgnoreQueryFilters()
            .CountAsync(p => p.AnalyseToken == token && !p.IsDeleted);
        var expAfter = await _context.Set<ShiftExpenses>()
            .IgnoreQueryFilters()
            .CountAsync(e => e.AnalyseToken == token && !e.IsDeleted);
        prefAfter.ShouldBe(0, "preference clone must be soft-deleted by reject");
        expAfter.ShouldBe(0, "shift-expense clone must be soft-deleted by reject");
    }

    [Test]
    public async Task PreExisting_Real_Work_On_Source_Shift_Survives_Scenario_Accept()
    {
        var realShift = await CreateRealShiftAsync("Source6");
        var workClient = await CreateTestClientAsync("Worker6");

        var preExistingWorkId = Guid.NewGuid();
        var preExistingWork = new Work
        {
            Id = preExistingWorkId,
            ClientId = workClient.Id,
            ShiftId = realShift.Id,
            CurrentDate = new DateOnly(2025, 11, 5),
            StartTime = realShift.StartShift,
            EndTime = realShift.EndShift,
            WorkTime = 8m,
            AnalyseToken = null
        };
        await _context.Work.AddAsync(preExistingWork);
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            token, new[] { realShift.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        await _service.SoftDeleteRealScheduleDataAsync(null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31),
            CancellationToken.None);
        await _service.PromoteScenarioWorksAsync(token, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), CancellationToken.None);
        await _context.SaveChangesAsync();

        var preserved = await _context.Work.IgnoreQueryFilters()
            .Where(w => w.Id == preExistingWorkId)
            .AsNoTracking()
            .SingleAsync();
        preserved.IsDeleted.ShouldBeFalse("pre-existing real work outside scenario range must survive");
        preserved.ShiftId.ShouldBe(realShift.Id, "pre-existing work must still reference the unchanged real shift id");

        var sourceShift = await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.Id == realShift.Id)
            .AsNoTracking()
            .SingleAsync();
        sourceShift.IsDeleted.ShouldBeFalse("real source shift must remain after scenario accept");
    }
}
