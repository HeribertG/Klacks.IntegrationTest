// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests verifying that real-mode shift read endpoints exclude
/// scenario clones from results: Shifts/GetSimpleList (FilterShifts pipeline),
/// Shifts/{id} (GetQueryHandler), Shifts/ByIds (GetShiftsByIdsQueryHandler).
/// </summary>

using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Domain.DTOs.Filter;
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
public class AnalyseScenarioShiftReadFilterTests
{
    private const string TestPrefix = "INTEGRATION_TEST_READFILTER_";

    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private AnalyseScenarioService _scenarioService = null!;
    private IShiftRepository _shiftRepository = null!;
    private ScheduleMapper _scheduleMapper = null!;

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
        _scenarioService = new AnalyseScenarioService(_context);

        var shiftValidator = new ShiftValidator();
        var queryPipeline = new ShiftQueryPipelineService(
            new DateRangeFilterService(),
            new ShiftSearchService(),
            new ShiftSortingService(),
            new ShiftStatusFilterService(),
            new ShiftPaginationService());

        var groupManagementLogger = Substitute.For<ILogger<ShiftGroupManagementService>>();
        var groupManagementService = new ShiftGroupManagementService(_context, groupManagementLogger);
        var entityCollectionUpdateService = new EntityCollectionUpdateService(_context);
        _scheduleMapper = new ScheduleMapper();
        var shiftLogger = Substitute.For<ILogger<Shift>>();

        _shiftRepository = new ShiftRepository(
            _context,
            shiftLogger,
            queryPipeline,
            groupManagementService,
            entityCollectionUpdateService,
            shiftValidator,
            _scheduleMapper);
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
            Description = "Integration test read filter",
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
    public async Task FilterShifts_Excludes_Scenario_Clones_From_Pipeline()
    {
        var realShift = await CreateRealShiftAsync("FilterReal");
        realShift.Status = ShiftStatus.OriginalOrder;
        await _context.SaveChangesAsync();

        var token = Guid.NewGuid();
        await _scenarioService.CloneScenarioDataAsync(
            groupId: null,
            fromDate: new DateOnly(2026, 1, 1),
            untilDate: new DateOnly(2026, 1, 31),
            token: token,
            additionalShiftIds: new[] { realShift.Id },
            ct: CancellationToken.None);
        await _context.SaveChangesAsync();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Original,
            ActiveDateRange = true,
            FormerDateRange = true,
            FutureDateRange = true,
            SearchString = string.Empty,
            IncludeClientName = false,
            IsSealedOrder = false,
            OrderBy = string.Empty,
            SortOrder = string.Empty,
        };

        var filtered = await _shiftRepository.FilterShifts(filter)
            .Where(s => s.Name.StartsWith(TestPrefix))
            .ToListAsync();

        filtered.ShouldContain(s => s.Id == realShift.Id, "real source shift must appear in real-mode pipeline");
        filtered.ShouldNotContain(s => s.AnalyseToken != null, "no clone may leak through FilterShifts");
        filtered.ShouldNotContain(s => s.ScenarioSourceShiftId != null, "no clone may leak through FilterShifts");

        var allIncludingClones = await _context.Shift.IgnoreQueryFilters()
            .Where(s => s.Name.StartsWith(TestPrefix))
            .ToListAsync();
        allIncludingClones.Count(s => s.AnalyseToken == token).ShouldBeGreaterThan(0,
            "control: at least one clone exists in DB so the filter is meaningful");
    }

    [Test]
    public async Task GetShiftsByIds_Filter_Excludes_Scenario_Clones()
    {
        var realShift = await CreateRealShiftAsync("ByIdsReal");
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

        var handler = new GetShiftsByIdsQueryHandler(
            _shiftRepository,
            _scheduleMapper,
            Substitute.For<ILogger<GetShiftsByIdsQueryHandler>>());

        var result = await handler.Handle(
            new GetShiftsByIdsQuery(new List<Guid> { realShift.Id, cloneId }),
            CancellationToken.None);

        result.Count.ShouldBe(1, "only the real shift must be returned, clone must be filtered");
        result[0].Id.ShouldBe(realShift.Id);
    }

    [Test]
    public async Task GetQueryHandler_Throws_InvalidRequest_For_Scenario_Clone_Id()
    {
        var realShift = await CreateRealShiftAsync("ByIdReal");
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

        var handler = new GetQueryHandler(
            _shiftRepository,
            _scheduleMapper,
            Substitute.For<ILogger<GetQueryHandler>>());

        var act = async () => await handler.Handle(
            new GetQuery<Klacks.Api.Application.DTOs.Schedules.ShiftResource>(cloneId),
            CancellationToken.None);
        await act.ShouldThrowAsync<InvalidRequestException>();
    }

    [Test]
    public async Task GetQueryHandler_Returns_Real_Shift_When_Not_Cloned()
    {
        var realShift = await CreateRealShiftAsync("PlainReal");

        var handler = new GetQueryHandler(
            _shiftRepository,
            _scheduleMapper,
            Substitute.For<ILogger<GetQueryHandler>>());

        var result = await handler.Handle(
            new GetQuery<Klacks.Api.Application.DTOs.Schedules.ShiftResource>(realShift.Id),
            CancellationToken.None);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(realShift.Id);
    }
}
