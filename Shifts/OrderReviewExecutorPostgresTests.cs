// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proof of the order review execution (OrderReviewExecutor on Npgsql, one transaction per order): a
/// full-day order is changed (start 07:00, first group, required qualification, default expense), sealed and cut into
/// 3x8; the sealing copies the qualification the change saved before it, and every part carries the expense and the
/// qualification. A cut the planner refuses rolls back the change and the sealing as well - the order stays an
/// unchanged open order and no plannable shift exists. Only rows named with the INTEGRATION_TEST_ prefix are written
/// and cleaned up.
/// </summary>

using Klacks.Api.Application.DTOs.Orders.Review;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Repositories.Associations;
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
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.Shifts;

[TestFixture]
[Category("RealDatabase")]
public class OrderReviewExecutorPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_SHIFT_ORDERREVIEW_";
    private static readonly TimeOnly Seven = new(7, 0);

    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _qualificationId = Guid.NewGuid();
    private string _connectionString = null!;
    private DbContextOptions<DataBaseContext> _options = null!;
    private DataBaseContext _context = null!;
    private ShiftRepository _shiftRepository = null!;
    private OrderReviewExecutor _executor = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(_options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync(_context);
        BuildExecutor();
        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    private void BuildExecutor()
    {
        var mapper = new ScheduleMapper();
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        var validator = new ShiftValidator();
        var queryPipeline = new ShiftQueryPipelineService(
            new DateRangeFilterService(), new ShiftSearchService(), new ShiftSortingService(),
            new ShiftStatusFilterService(), new ShiftPaginationService());
        _shiftRepository = new ShiftRepository(
            _context,
            Substitute.For<ILogger<Shift>>(),
            queryPipeline,
            new ShiftGroupManagementService(_context, Substitute.For<ILogger<ShiftGroupManagementService>>()),
            new EntityCollectionUpdateService(_context),
            validator,
            mapper,
            TestCompanyClock.Utc());
        var expenses = new ShiftExpensesRepository(_context, Substitute.For<ILogger<ShiftExpenses>>());
        var visible = AllShiftsVisible();
        var facade = new ShiftCutFacade(
            _shiftRepository,
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()),
            new ShiftTreeService(_context, Substitute.For<ILogger<ShiftTreeService>>()),
            new ShiftResetService(_shiftRepository, Substitute.For<ILogger<ShiftResetService>>()),
            validator,
            mapper,
            unitOfWork,
            visible,
            expenses,
            Substitute.For<ILogger<ShiftCutFacade>>());
        var planner = new ShiftTimeCutPlanner(_shiftRepository, new ShiftReader(_context), visible, expenses);
        var sealing = new OrderSealingService(_shiftRepository, unitOfWork, visible);
        _executor = new OrderReviewExecutor(
            _shiftRepository, unitOfWork, sealing, planner, facade, TestCompanyClock.Utc(),
            Substitute.For<ILogger<OrderReviewExecutor>>());
    }

    private async Task SeedAsync()
    {
        _context.Set<Group>().Add(new Group
        {
            Id = _groupId,
            Name = $"{Prefix}Bern",
            Description = string.Empty,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var name = new MultiLanguage();
        name.SetValue("de", $"{Prefix}Sanität");
        _context.Set<Qualification>().Add(new Qualification { Id = _qualificationId, Name = name });
        await _context.SaveChangesAsync();
    }

    private async Task<Guid> SeedOrderAsync()
    {
        var order = new Shift
        {
            Id = Guid.NewGuid(),
            Name = $"{Prefix}Nachtwache",
            Abbreviation = "NW",
            Status = ShiftStatus.OriginalOrder,
            ShiftType = ShiftType.IsTask,
            FromDate = new DateOnly(2027, 1, 4),
            UntilDate = new DateOnly(2027, 1, 10),
            StartShift = TimeOnly.MinValue,
            EndShift = TimeOnly.MinValue,
            WorkTime = 24m,
            IsMonday = true,
            IsTuesday = true,
            IsWednesday = true,
            IsThursday = true,
            IsFriday = true,
            IsSaturday = true,
            IsSunday = true,
            Quantity = 1,
            SumEmployees = 1,
        };
        _context.Shift.Add(order);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return order.Id;
    }

    private async Task<OrderReviewExecutionTarget> TargetAsync(Guid orderId, List<OrderReviewPart> parts)
    {
        var item = new OrderReviewDraftItem
        {
            OrderId = orderId,
            OrderName = $"{Prefix}Nachtwache",
            IsFullDay = true,
            CurrentStart = TimeOnly.MinValue,
            CurrentEnd = TimeOnly.MinValue,
            ChosenStart = Seven,
            ChosenParts = parts,
            ChosenGroupId = _groupId,
            ChosenGroupName = $"{Prefix}Bern",
            ChosenQualificationIds = [_qualificationId],
            ExpenseStatus = OrderReviewExpenseStatus.Provided,
            Expenses = [new OrderReviewExpense { Description = "Mahlzeit", Amount = 15m }],
        };
        return new OrderReviewExecutionTarget(item, (await _shiftRepository.Get(orderId))!);
    }

    [Test]
    public async Task AFullDayOrder_IsChangedSealedAndCut_AndEveryPartCarriesExpenseAndQualification()
    {
        var orderId = await SeedOrderAsync();

        var outcome = await _executor.ExecuteAsync(
            await TargetAsync(orderId, FullDayOrderRule.EqualParts(Seven, 3)), "integration-test", "de");

        outcome.Succeeded.ShouldBeTrue(outcome.FailureReason);
        outcome.PartCount.ShouldBe(3);

        await using var verify = new DataBaseContext(_options, Substitute.For<IHttpContextAccessor>());
        var order = await verify.Shift.AsNoTracking().SingleAsync(s => s.Id == orderId);
        order.Status.ShouldBe(ShiftStatus.SealedOrder);
        order.StartShift.ShouldBe(Seven);
        order.EndShift.ShouldBe(Seven);
        (await verify.GroupItem.CountAsync(g => g.ShiftId == orderId && g.GroupId == _groupId)).ShouldBe(1);

        var parts = await verify.Shift.AsNoTracking()
            .Where(s => s.OriginalId == orderId && s.Status == ShiftStatus.SplitShift)
            .OrderBy(s => s.StartShift)
            .ToListAsync();
        parts.Select(p => p.StartShift).ToArray().ShouldBe(new[] { Seven, new TimeOnly(15, 0), new TimeOnly(23, 0) });
        var familyIds = parts.Select(p => p.Id).Append(orderId).ToList();
        var familyExpenses = await verify.Set<ShiftExpenses>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => familyIds.Contains(e.ShiftId))
            .ToListAsync();
        var expenseDump = string.Join("; ", familyExpenses.Select(e =>
            $"{(e.ShiftId == orderId ? "order" : parts.Single(p => p.Id == e.ShiftId).StartShift.ToString())}:{e.Description}:{e.Amount}:deleted={e.IsDeleted}:{e.Id}"));
        foreach (var part in parts)
        {
            (await verify.Set<ShiftExpenses>().CountAsync(e => e.ShiftId == part.Id && e.Description == "Mahlzeit" && e.Amount == 15m))
                .ShouldBe(1, $"part {part.StartShift} must carry the expense; family expenses: {expenseDump}");
            (await verify.ShiftRequiredQualification.CountAsync(q => q.ShiftId == part.Id && q.QualificationId == _qualificationId))
                .ShouldBe(1, $"part {part.StartShift} must require the qualification");
        }
    }

    [Test]
    public async Task ACutThePlannerRefuses_RollsBackTheChangeAndTheSealing()
    {
        var orderId = await SeedOrderAsync();
        var gap = new List<OrderReviewPart>
        {
            new() { Start = Seven, End = new TimeOnly(15, 0) },
            new() { Start = new TimeOnly(16, 0), End = Seven },
        };

        var outcome = await _executor.ExecuteAsync(await TargetAsync(orderId, gap), "integration-test", "de");

        outcome.Succeeded.ShouldBeFalse();
        await using var verify = new DataBaseContext(_options, Substitute.For<IHttpContextAccessor>());
        var order = await verify.Shift.AsNoTracking().SingleAsync(s => s.Id == orderId);
        order.Status.ShouldBe(ShiftStatus.OriginalOrder);
        order.StartShift.ShouldBe(TimeOnly.MinValue);
        (await verify.GroupItem.CountAsync(g => g.ShiftId == orderId)).ShouldBe(0);
        (await verify.Set<ShiftExpenses>().CountAsync(e => e.ShiftId == orderId)).ShouldBe(0);
        (await verify.ShiftRequiredQualification.CountAsync(q => q.ShiftId == orderId)).ShouldBe(0);
        (await verify.Shift.CountAsync(s => s.OriginalId == orderId)).ShouldBe(0);
    }

    private static IShiftVisibilityGuard AllShiftsVisible()
    {
        var guard = Substitute.For<IShiftVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.IsVisibleAsync(Arg.Any<Shift>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);
        return guard;
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM shift_required_qualification WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM shift WHERE name LIKE '{Prefix}%';
            DELETE FROM qualification WHERE name->>'de' LIKE '{Prefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
