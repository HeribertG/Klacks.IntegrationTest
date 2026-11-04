// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ShiftFamilyReset under a real transaction against PostgreSQL: the real UnitOfWork, DataBaseContext and repositories on a
/// context with EnableRetryOnFailure, like production. A failure after the first save leaves every row as it was; a transient
/// failure of the first run replays the transaction with exactly one result; two concurrent resets of a family are ordered
/// by the head lock, the first keeps the piece before the day, the second answers nothingToReset. All state is compared in a
/// fresh context.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
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
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Shifts;

public partial class ShiftManipulationIntegrationTests
{
    private const int MaxResetRetries = 3;
    private const string InjectedFailure = "Injected failure after the first save";

    private static readonly TimeSpan ResetWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BlockedPollInterval = TimeSpan.FromMilliseconds(100);

    private sealed record ResetStack(DataBaseContext Context, ShiftFamilyReset Reset, HookedShiftTreeService Tree) : IDisposable
    {
        public void Dispose() => Context.Dispose();
    }

    private sealed record StoredShiftState(Guid Id, ShiftStatus Status, DateOnly? From, DateOnly? Until, bool Deleted);

    private sealed record StoredExpenseState(Guid Id, Guid ShiftId, decimal Amount, bool Deleted);

    private DataBaseContext NewRetryingResetContext(string? applicationName = null)
    {
        var connection = new NpgsqlConnectionStringBuilder(_connectionString);
        if (applicationName != null)
        {
            connection.ApplicationName = applicationName;
        }

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connection.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(MaxResetRetries))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private ResetStack NewResetStack(Func<int, Task> beforeRecalculate, string? applicationName = null)
    {
        var context = NewRetryingResetContext(applicationName);
        var unitOfWork = new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>());
        var queryPipeline = new ShiftQueryPipelineService(
            new DateRangeFilterService(),
            new ShiftSearchService(),
            new ShiftSortingService(),
            new ShiftStatusFilterService(),
            new ShiftPaginationService());
        var shiftRepository = new ShiftRepository(
            context,
            Substitute.For<ILogger<Shift>>(),
            queryPipeline,
            new ShiftGroupManagementService(context, Substitute.For<ILogger<ShiftGroupManagementService>>()),
            new EntityCollectionUpdateService(context),
            new ShiftValidator(),
            new ScheduleMapper(),
            TestCompanyClock.Utc());
        var tree = new HookedShiftTreeService(
            new ShiftTreeService(context, Substitute.For<ILogger<ShiftTreeService>>()), beforeRecalculate);
        var reset = ShiftFamilyResetFactory.Create(context, shiftRepository, tree, AllShiftsVisible(), unitOfWork);
        return new ResetStack(context, reset, tree);
    }

    private async Task<(Guid SealedOrder, Guid First, Guid Later)> CreateFamilyWithExpensesAsync(string suffix)
    {
        return await CreateUiCutFamilyAsync(suffix, order =>
        {
            order.DefaultExpenses =
            [
                new Klacks.Api.Application.DTOs.Schedules.ShiftExpensesResource { Amount = 7.5m, Description = $"{TestShiftPrefix}Toll", Taxable = false },
                new Klacks.Api.Application.DTOs.Schedules.ShiftExpensesResource { Amount = 20m, Description = $"{TestShiftPrefix}Allowance", Taxable = true }
            ];
        });
    }

    private async Task<(List<StoredShiftState> Shifts, List<StoredExpenseState> Expenses)> FreshFamilyStateAsync(Guid sealedOrder)
    {
        using var fresh = NewRetryingResetContext();
        var shifts = await fresh.Shift.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == sealedOrder || s.OriginalId == sealedOrder)
            .OrderBy(s => s.Id)
            .Select(s => new StoredShiftState(s.Id, s.Status, s.FromDate, s.UntilDate, s.IsDeleted))
            .ToListAsync();
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var expenses = await fresh.Set<ShiftExpenses>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => shiftIds.Contains(e.ShiftId))
            .OrderBy(e => e.Id)
            .Select(e => new StoredExpenseState(e.Id, e.ShiftId, e.Amount, e.IsDeleted))
            .ToListAsync();
        return (shifts, expenses);
    }

    private static async Task<T> WithinResetTimeoutAsync<T>(Task<T> task)
    {
        await task.WaitAsync(ResetWaitTimeout);
        return await task;
    }

    private async Task<bool> WaitUntilBlockedOrFinishedAsync(string applicationName, Task resetTask)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow + ResetWaitTimeout;

        while (DateTime.UtcNow < deadline && !resetTask.IsCompleted)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name AND wait_event_type = 'Lock'", connection);
            command.Parameters.AddWithValue("name", applicationName);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(BlockedPollInterval);
        }

        return false;
    }

    [Test]
    public async Task Reset_FailingAfterTheFirstSave_RollsEverythingBack_InAFreshContext()
    {
        var family = await CreateFamilyWithExpensesAsync("ResetTx_Rollback");
        await AddWorkOnAsync(family.First, ResetDay.AddDays(-1));
        var before = await FreshFamilyStateAsync(family.SealedOrder);
        using var stack = NewResetStack(_ => throw new InvalidOperationException(InjectedFailure));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            stack.Reset.ResetAsync(new Shift { Id = family.SealedOrder }, ResetDay));

        thrown.Message.ShouldBe(InjectedFailure);
        stack.Tree.RecalculateCalls.ShouldBe(1, "the failure has to come after the first save");
        var after = await FreshFamilyStateAsync(family.SealedOrder);
        after.Shifts.ShouldBe(before.Shifts);
        after.Expenses.ShouldBe(before.Expenses);
        after.Shifts.Count(s => s.Status == ShiftStatus.OriginalShift).ShouldBe(0, "no plannable row was left behind");
    }

    [Test]
    public async Task Reset_WithATransientFailureOfTheFirstRun_ReplaysTheTransaction_AndEndsWithOneResult()
    {
        var family = await CreateFamilyWithExpensesAsync("ResetTx_Retry");
        await AddWorkOnAsync(family.First, ResetDay.AddDays(-1));
        var before = await FreshFamilyStateAsync(family.SealedOrder);
        using var stack = NewResetStack(call => call == 1
            ? throw new NpgsqlException(InjectedFailure, new TimeoutException())
            : Task.CompletedTask);

        var result = await stack.Reset.ResetAsync(new Shift { Id = family.SealedOrder }, ResetDay);

        stack.Tree.RecalculateCalls.ShouldBe(2, "the transaction lambda runs twice");
        result.Count(row => row.Status == ShiftStatus.OriginalShift).ShouldBe(1);

        var after = await FreshFamilyStateAsync(family.SealedOrder);
        var plannable = after.Shifts.Single(s => s.Status == ShiftStatus.OriginalShift);
        plannable.Deleted.ShouldBeFalse();
        plannable.From.ShouldBe(ResetDay);
        plannable.Until.ShouldBeNull();

        var first = after.Shifts.Single(s => s.Id == family.First);
        first.Deleted.ShouldBeFalse();
        first.Until.ShouldBe(ResetDay.AddDays(-1), "the replay has to write the end of the kept piece again");
        after.Shifts.Single(s => s.Id == family.Later).Deleted.ShouldBeTrue("the replay has to delete the piece again");
        after.Shifts.Count.ShouldBe(before.Shifts.Count + 1, "exactly one new row, no duplicate of the first run");

        var plannableExpenses = after.Expenses.Where(e => e.ShiftId == plannable.Id).ToList();
        plannableExpenses.ShouldAllBe(expense => !expense.Deleted);
        plannableExpenses.Select(e => e.Amount).OrderBy(amount => amount).ShouldBe([7.5m, 20m]);
        after.Expenses.Count.ShouldBe(before.Expenses.Count + 2, "exactly one set of expenses");
    }

    [Test]
    public async Task Reset_TwoConcurrentResetsOfAFamily_GiveOneResult_AndTheSecondAnswersNothingToReset()
    {
        var family = await CreateFamilyWithExpensesAsync("ResetTx_Lock");
        var firstEnteredTheGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openTheGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstStack = NewResetStack(async _ =>
        {
            firstEnteredTheGate.TrySetResult();
            await openTheGate.Task.WaitAsync(ResetWaitTimeout);
        });
        var secondName = $"reset-lock-{Guid.NewGuid():N}";
        using var secondStack = NewResetStack(_ => Task.CompletedTask, secondName);

        var firstReset = Task.Run(() => firstStack.Reset.ResetAsync(new Shift { Id = family.SealedOrder }, ResetDay));
        await firstEnteredTheGate.Task.WaitAsync(ResetWaitTimeout);
        var secondReset = Task.Run(() => secondStack.Reset.ResetAsync(new Shift { Id = family.SealedOrder }, ResetDay));
        var secondWasBlocked = await WaitUntilBlockedOrFinishedAsync(secondName, secondReset);
        openTheGate.TrySetResult();

        var firstResult = await WithinResetTimeoutAsync(firstReset);
        var refused = await Should.ThrowAsync<ShiftResetRefusedException>(() => secondReset.WaitAsync(ResetWaitTimeout));

        secondWasBlocked.ShouldBeTrue("the second reset has to wait for the first one, or the test proves no overlap");
        refused.ConflictDetails![ShiftResetRefusedException.ReasonKey].ShouldBe(ShiftResetRefusedException.NothingToResetReason);
        firstResult.Count.ShouldBe(2);
        firstResult.Single(row => row.Status == ShiftStatus.OriginalShift).FromDate.ShouldBe(ResetDay);
        var after = await FreshFamilyStateAsync(family.SealedOrder);
        var live = after.Shifts.Where(s => s.Id != family.SealedOrder && !s.Deleted).ToList();
        live.Single(s => s.Status == ShiftStatus.OriginalShift).From.ShouldBe(ResetDay);
        live.Single(s => s.Status == ShiftStatus.SplitShift).Id.ShouldBe(family.First, "the piece before the day stays, without work too");
        live.Count.ShouldBe(2);
        after.Shifts.Count(s => s.Status == ShiftStatus.OriginalShift).ShouldBe(1, "no second plannable row, not even a deleted one");
    }
}
