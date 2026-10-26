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

namespace Klacks.IntegrationTest.Shifts;

/// <summary>
/// Pins, against real PostgreSQL, the order-id filter of ShiftRepository.GetOpenOrdersAsync (OpenOrderFilter.OrderId),
/// which seal_shift uses to load the one order it derives a group for exactly as assign_orders_to_groups loads its batch:
/// the id selects that open order only, and the id of an order that is no longer open (sealed) selects nothing. The
/// seeded orders carry the INTEGRATION_TEST_OpenOrderById_ prefix and the cleanup deletes exactly those rows.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class OpenOrderByIdPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_OpenOrderById_";

    private DataBaseContext _context = null!;
    private IShiftRepository _shiftRepository = null!;
    private Guid _openOrderId;
    private Guid _otherOpenOrderId;
    private Guid _sealedOrderId;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();

        _openOrderId = Guid.NewGuid();
        _otherOpenOrderId = Guid.NewGuid();
        _sealedOrderId = Guid.NewGuid();
        _context.Shift.AddRange(
            Order(_openOrderId, "OPEN", ShiftStatus.OriginalOrder),
            Order(_otherOpenOrderId, "OTHER", ShiftStatus.OriginalOrder),
            Order(_sealedOrderId, "SEALED", ShiftStatus.SealedOrder));
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            new ShiftQueryPipelineService(
                new DateRangeFilterService(),
                new ShiftSearchService(),
                new ShiftSortingService(),
                new ShiftStatusFilterService(),
                new ShiftPaginationService()),
            new ShiftGroupManagementService(_context, Substitute.For<ILogger<ShiftGroupManagementService>>()),
            new EntityCollectionUpdateService(_context),
            new ShiftValidator(),
            new ScheduleMapper(),
            TestCompanyClock.Utc());
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task GetOpenOrdersAsync_WithAnOrderId_ReturnsThatOpenOrderOnly()
    {
        var orders = await _shiftRepository.GetOpenOrdersAsync(Filter(_openOrderId));

        orders.Select(order => order.Id).ShouldBe([_openOrderId]);
    }

    [Test]
    public async Task GetOpenOrdersAsync_WithTheIdOfASealedOrder_ReturnsNothing()
    {
        var orders = await _shiftRepository.GetOpenOrdersAsync(Filter(_sealedOrderId));

        orders.ShouldBeEmpty();
    }

    private static OpenOrderFilter Filter(Guid orderId) =>
        new(SourceSystemId: null, FromDate: null, UntilDate: null, CustomerName: null, GroupId: null, MaxCount: null,
            OrderId: orderId);

    private static Shift Order(Guid id, string label, ShiftStatus status) => new()
    {
        Id = id,
        Name = $"{Prefix}{label}",
        Abbreviation = "OOB",
        Description = Prefix,
        Status = status,
        ShiftType = ShiftType.IsTask,
        FromDate = new DateOnly(2026, 1, 1),
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        AfterShift = new TimeOnly(0, 0),
        BeforeShift = new TimeOnly(0, 0),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 1,
    };

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE starts_with(name, {0}))", Prefix);
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE starts_with(name, {0})", Prefix);
    }
}
