// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Pagination;

/// <summary>
/// Pins, against real PostgreSQL, the shift list path behind Shifts/GetSimpleList
/// (ShiftRepository.GetFilteredAndPaginatedShifts: FilterShifts, then ApplyPaginationAsync) when no date range is
/// selected: the result is an empty page, not a failure of EF CountAsync on an in-memory query. A control case with
/// every date range selected finds the seeded shift through the same pipeline. The seeded shift carries the
/// INTEGRATION_TEST_ShiftNoRange_ prefix and the cleanup deletes exactly those rows.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class ShiftListNoDateRangePostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_ShiftNoRange_";
    private const string Abbreviation = "SNR";
    private const int PageSize = 20;

    private DataBaseContext _context = null!;
    private IShiftRepository _shiftRepository = null!;
    private string _searchToken = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();

        _searchToken = "shnr" + Guid.NewGuid().ToString("N")[..10];
        _context.Shift.Add(new Shift
        {
            Id = Guid.NewGuid(),
            Name = $"{Prefix}ACTIVE {_searchToken}",
            Abbreviation = Abbreviation,
            Description = Prefix,
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            Quantity = 1,
            SumEmployees = 1,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var scheduleMapper = new ScheduleMapper();
        var queryPipeline = new ShiftQueryPipelineService(
            new DateRangeFilterService(),
            new ShiftSearchService(),
            new ShiftSortingService(),
            new ShiftStatusFilterService(),
            new ShiftPaginationService());
        _shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            queryPipeline,
            new ShiftGroupManagementService(_context, Substitute.For<ILogger<ShiftGroupManagementService>>()),
            new EntityCollectionUpdateService(_context),
            new ShiftValidator(),
            scheduleMapper,
            TestCompanyClock.Utc());
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task GetFilteredAndPaginatedShifts_NoDateRangeSelected_ReturnsAnEmptyPageWithMinusOneMarkers(bool withSearchString)
    {
        var filter = ListFilter(active: false, former: false, future: false);
        if (!withSearchString)
        {
            filter.SearchString = string.Empty;
        }

        var result = await _shiftRepository.GetFilteredAndPaginatedShifts(filter, null);

        result.Shifts.ShouldBeEmpty();
        result.MaxItems.ShouldBe(0);
        result.FirstItemOnPage.ShouldBe(-1);
        result.MaxPages.ShouldBe(-1);
    }

    [Test]
    public async Task GetFilteredAndPaginatedShifts_EveryDateRangeSelected_FindsTheSeededShift()
    {
        var filter = ListFilter(active: true, former: true, future: true);

        var result = await _shiftRepository.GetFilteredAndPaginatedShifts(filter, null);

        result.Shifts.Select(shift => shift.Name).ShouldBe([$"{Prefix}ACTIVE {_searchToken}"]);
        result.MaxItems.ShouldBe(1);
        result.FirstItemOnPage.ShouldBe(0);
    }

    private ShiftFilter ListFilter(bool active, bool former, bool future) => new()
    {
        FilterType = ShiftFilterType.Shift,
        ActiveDateRange = active,
        FormerDateRange = former,
        FutureDateRange = future,
        IsTimeRange = true,
        IsSporadic = true,
        SearchString = _searchToken,
        OrderBy = string.Empty,
        SortOrder = string.Empty,
        RequiredPage = 0,
        NumberOfItemsPerPage = PageSize
    };

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, {0}))", Prefix);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE starts_with(name, {0})", Prefix);
    }
}
