// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins against PostgreSQL (IT database klacks) what the update hand-off handlers answer when the database rejects their
/// write through the real UpdateHistoryRepository: a status that moved underneath (status is a concurrency token) makes
/// delete answer BlockedByLiveOperation and cancel NoLongerPending; a second active row (partial unique index
/// ix_update_history_single_active) makes the Whisper install answer "operation in progress" and stores nothing. The
/// race is staged by a delegating repository that moves the status right after the handler's read (and, for cancel,
/// hands the handler the row as still Pending) or hides the active row from the handler's pre-check.
/// Operational precondition (like UpdateHistorySingleActiveGuardTests): no other Pending/Running row may exist in the
/// shared database for the install case. Every row carries the INTEGRATION_TEST_UPDCONFLICT requester and the cleanup
/// deletes exactly those rows.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Commands.Update;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Handlers.Update;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Update;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Update;

[TestFixture]
[Category("RealDatabase")]
public class UpdateHandlersWriteConflictPostgresTests
{
    private const string TestRequester = "INTEGRATION_TEST_UPDCONFLICT";

    private DataBaseContext _context = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task Delete_StatusMovedAfterTheRead_IsBlockedByLiveOperation()
    {
        var row = await SeedAsync(UpdateOperationStatus.Failed);
        var repository = new RacingRepository(new UpdateHistoryRepository(_context), _context, presentAsPending: false);

        var outcome = await new DeleteUpdateHistoryCommandHandler(repository)
            .Handle(new DeleteUpdateHistoryCommand(row.Id), CancellationToken.None);

        outcome.ShouldBe(UpdateHistoryDeletionOutcome.BlockedByLiveOperation);
        (await StoredStatusAsync(row.Id)).ShouldBe(UpdateOperationStatus.Succeeded);
    }

    [Test]
    public async Task Cancel_StatusMovedAfterTheRead_AnswersNoLongerPending()
    {
        var row = await SeedAsync(UpdateOperationStatus.Failed);
        var repository = new RacingRepository(new UpdateHistoryRepository(_context), _context, presentAsPending: true);

        var outcome = await new CancelUpdateCommandHandler(repository)
            .Handle(new CancelUpdateCommand(row.Id), CancellationToken.None);

        outcome.ShouldBe(UpdateOperationCancellationOutcome.NoLongerPending);
        (await StoredStatusAsync(row.Id)).ShouldBe(UpdateOperationStatus.Succeeded);
    }

    [Test]
    public async Task InstallWhisper_SecondActiveRow_IsRejectedAsOperationInProgress_AndStoresNothing()
    {
        await SeedAsync(UpdateOperationStatus.Pending);
        var repository = new RacingRepository(new UpdateHistoryRepository(_context), _context, presentAsPending: false);

        var result = await new InstallWhisperPluginCommandHandler(repository).Handle(
            new InstallWhisperPluginCommand(WhisperPluginConstants.ModelAliasLarge, TestRequester), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        result.Reason.ShouldBe(UpdateReasons.OperationInProgress);
        _context.ChangeTracker.Clear();
        (await _context.UpdateHistory.IgnoreQueryFilters().CountAsync(h => h.RequestedBy == TestRequester)).ShouldBe(1);
    }

    private async Task<UpdateHistory> SeedAsync(UpdateOperationStatus status)
    {
        var row = new UpdateHistory
        {
            Id = Guid.NewGuid(),
            OperationType = UpdateOperationType.Update,
            Status = status,
            Channel = UpdateChannel.Stable,
            FromVersion = "1.0.0",
            TargetVersion = "1.1.0",
            RequestedBy = TestRequester,
            RequestedAt = DateTime.UtcNow,
        };
        _context.UpdateHistory.Add(row);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return row;
    }

    private async Task<UpdateOperationStatus> StoredStatusAsync(Guid id)
    {
        _context.ChangeTracker.Clear();
        return await _context.UpdateHistory.IgnoreQueryFilters().Where(h => h.Id == id).Select(h => h.Status).SingleAsync();
    }

    private async Task CleanupAsync()
    {
        await _context.UpdateHistory
            .IgnoreQueryFilters()
            .Where(h => h.RequestedBy == TestRequester)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Delegates to the real repository. GetByIdAsync moves the stored status to Succeeded right after the read (the
    /// updater acting in the gap) and optionally hands the row to the handler as Pending while its original (token)
    /// value stays the one read; GetActiveOperationAsync reports no active row so the handler reaches its insert.
    /// </summary>
    private sealed class RacingRepository(IUpdateHistoryRepository inner, DataBaseContext context, bool presentAsPending)
        : IUpdateHistoryRepository
    {
        public Task<UpdateHistory> AddAsync(UpdateHistory entry, CancellationToken cancellationToken = default) =>
            inner.AddAsync(entry, cancellationToken);

        public Task UpdateAsync(UpdateHistory entry, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(entry, cancellationToken);

        public Task DeleteAsync(UpdateHistory entry, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(entry, cancellationToken);

        public Task<UpdateHistory?> GetActiveOperationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<UpdateHistory?>(null);

        public Task<UpdateHistory?> GetLastSuccessfulUpdateAsync(CancellationToken cancellationToken = default) =>
            inner.GetLastSuccessfulUpdateAsync(cancellationToken);

        public async Task<UpdateHistory?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var entry = await inner.GetByIdAsync(id, cancellationToken);
            await context.UpdateHistory
                .IgnoreQueryFilters()
                .Where(h => h.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, UpdateOperationStatus.Succeeded), cancellationToken);
            if (entry != null && presentAsPending)
            {
                entry.Status = UpdateOperationStatus.Pending;
            }

            return entry;
        }

        public Task<IReadOnlyList<UpdateHistory>> GetRecentAsync(int take, CancellationToken cancellationToken = default) =>
            inner.GetRecentAsync(take, cancellationToken);

        public Task<UpdateHistory?> GetLatestByTypesAsync(
            IReadOnlyCollection<UpdateOperationType> types, CancellationToken cancellationToken = default) =>
            inner.GetLatestByTypesAsync(types, cancellationToken);
    }
}
