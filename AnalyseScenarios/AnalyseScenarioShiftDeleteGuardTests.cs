// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for the Shift-Delete guard introduced as defense-in-depth
/// for the phantom-shift fix. Verifies that ShiftRepository.Delete blocks
/// deletion of clone shifts (AnalyseToken set) and source shifts that are
/// still referenced by active scenario clones.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
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
public class AnalyseScenarioShiftDeleteGuardTests
{
    private const string TestPrefix = "INTEGRATION_TEST_DELETEGUARD_";

    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private AnalyseScenarioService _scenarioService = null!;
    private IShiftRepository _shiftRepository = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
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
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _scenarioService = new AnalyseScenarioService(_context);

        var shiftValidator = new ShiftValidator();
        var dateRangeFilterService = new DateRangeFilterService();
        var shiftSearchService = new ShiftSearchService();
        var shiftSortingService = new ShiftSortingService();
        var shiftStatusFilterService = new ShiftStatusFilterService();
        var shiftPaginationService = new ShiftPaginationService();
        var queryPipeline = new ShiftQueryPipelineService(
            dateRangeFilterService,
            shiftSearchService,
            shiftSortingService,
            shiftStatusFilterService,
            shiftPaginationService);

        var groupManagementLogger = Substitute.For<ILogger<ShiftGroupManagementService>>();
        var groupManagementService = new ShiftGroupManagementService(_context, groupManagementLogger);
        var entityCollectionUpdateService = new EntityCollectionUpdateService(_context);
        var scheduleMapper = new ScheduleMapper();
        var shiftLogger = Substitute.For<ILogger<Shift>>();

        _shiftRepository = new ShiftRepository(
            _context,
            shiftLogger,
            queryPipeline,
            groupManagementService,
            entityCollectionUpdateService,
            shiftValidator,
            scheduleMapper);
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
            DELETE FROM client_shift_preference WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Shift> CreateRealShiftAsync(string suffix)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Integration test delete guard",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
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
    public async Task Delete_Throws_When_Shift_Is_Scenario_Clone()
    {
        var realShift = await CreateRealShiftAsync("CloneSource");
        var token = Guid.NewGuid();
        var idMap = await _scenarioService.CloneScenarioDataAsync(
            groupId: null,
            fromDate: new DateOnly(2026, 1, 1),
            untilDate: new DateOnly(2026, 1, 31),
            token: token,
            additionalShiftIds: new[] { realShift.Id },
            ct: CancellationToken.None);
        await _context.SaveChangesAsync();

        var cloneId = idMap[realShift.Id];

        var act = async () => await _shiftRepository.Delete(cloneId);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("scenario mode");
        ex.Message.ShouldContain(cloneId.ToString());

        var cloneStillThere = await _context.Shift.IgnoreQueryFilters()
            .AnyAsync(s => s.Id == cloneId && !s.IsDeleted);
        cloneStillThere.ShouldBeTrue("clone must remain untouched after blocked delete");
    }

    [Test]
    public async Task Delete_Throws_When_Shift_Is_Source_Of_Active_Scenario_Clones()
    {
        var realShift = await CreateRealShiftAsync("SourceWithClones");
        var token = Guid.NewGuid();
        await _scenarioService.CloneScenarioDataAsync(
            groupId: null,
            fromDate: new DateOnly(2026, 1, 1),
            untilDate: new DateOnly(2026, 1, 31),
            token: token,
            additionalShiftIds: new[] { realShift.Id },
            ct: CancellationToken.None);
        await _context.SaveChangesAsync();

        var act = async () => await _shiftRepository.Delete(realShift.Id);
        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("active scenario clones");
        ex.Message.ShouldContain(realShift.Id.ToString());

        var sourceStillThere = await _context.Shift.IgnoreQueryFilters()
            .AnyAsync(s => s.Id == realShift.Id && !s.IsDeleted);
        sourceStillThere.ShouldBeTrue("source must remain untouched after blocked delete");
    }

    [Test]
    public async Task Delete_Succeeds_For_Real_Shift_Without_Active_Scenario_Clones()
    {
        var realShift = await CreateRealShiftAsync("Plain");

        var deleted = await _shiftRepository.Delete(realShift.Id);
        await _context.SaveChangesAsync();

        deleted.ShouldNotBeNull();
        deleted!.Id.ShouldBe(realShift.Id);

        var stillActive = await _context.Shift.IgnoreQueryFilters()
            .AnyAsync(s => s.Id == realShift.Id && !s.IsDeleted);
        stillActive.ShouldBeFalse("real shift without scenario clones must be soft-deleted by Delete()");
    }

    [Test]
    public async Task Delete_Returns_Null_When_Shift_Does_Not_Exist()
    {
        var nonExistentId = Guid.NewGuid();

        var result = await _shiftRepository.Delete(nonExistentId);

        result.ShouldBeNull();
    }
}
