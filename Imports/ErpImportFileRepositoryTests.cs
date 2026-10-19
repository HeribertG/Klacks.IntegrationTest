// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for ErpImportFileRepository against the real PostgreSQL database. They prove what
/// the in-memory provider cannot: the conditional ExecuteUpdateAsync transitions really are
/// compare-and-swap (two concurrent claims -> exactly one winner; a claim with a stale attempt number
/// loses; a foreign instance, or an older attempt of the same instance, cannot heartbeat or finish the
/// file), the reaper flips only stale Importing rows, a retry reset only leaves
/// Failed/Interrupted, every read query translates and filters as documented, and the partial unique
/// index on storage_key rejects a second live row while ignoring soft-deleted ones.
/// Cleanup deletes ONLY rows this fixture created: its drop points carry the INTEGRATION_TEST_ERPFILE_
/// source-system prefix and its ledger rows the same storage-key prefix.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Imports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Imports;

[TestFixture]
[Category("RealDatabase")]
public class ErpImportFileRepositoryTests
{
    private const string TestPrefix = "INTEGRATION_TEST_ERPFILE_";
    private const string InstanceA = "INTEGRATION_TEST_ERPFILE_HOST:a";
    private const string InstanceB = "INTEGRATION_TEST_ERPFILE_HOST:b";
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string AuthenticationType = "IntegrationTest";

    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromMilliseconds(1);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
    }

    [Test]
    public async Task TwoConcurrentClaims_ExactlyOneWins_AndTheRowCarriesTheWinner()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);
        var now = DateTime.UtcNow;

        await using var firstContext = NewContext();
        await using var secondContext = NewContext();
        await firstContext.Database.OpenConnectionAsync();
        await secondContext.Database.OpenConnectionAsync();

        var outcomes = await Task.WhenAll(
            new ErpImportFileRepository(firstContext).TryClaimAsync(file.Id, 0, InstanceA, now),
            new ErpImportFileRepository(secondContext).TryClaimAsync(file.Id, 0, InstanceB, now));

        outcomes.Count(won => won).ShouldBe(1);
        var winner = outcomes[0] ? InstanceA : InstanceB;

        var stored = await ReloadAsync(file.Id);
        stored.Status.ShouldBe(ErpImportFileStatus.Importing);
        stored.ClaimedBy.ShouldBe(winner);
        stored.Attempt.ShouldBe(1);
        stored.StartedAt.ShouldNotBeNull();
        stored.HeartbeatAt.ShouldNotBeNull();
    }

    [Test]
    public async Task Claim_ResetsTheCountersOfAPreviousAttempt()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, configure: f =>
        {
            f.Attempt = 2;
            f.TotalOrders = 9;
            f.ProcessedOrders = 9;
            f.NewCount = 3;
            f.RejectedCount = 6;
        });

        await using var context = NewContext();
        (await new ErpImportFileRepository(context).TryClaimAsync(file.Id, 2, InstanceA, DateTime.UtcNow)).ShouldBeTrue();

        var stored = await ReloadAsync(file.Id);
        stored.Attempt.ShouldBe(3);
        stored.TotalOrders.ShouldBeNull();
        stored.ProcessedOrders.ShouldBe(0);
        stored.NewCount.ShouldBe(0);
        stored.RejectedCount.ShouldBe(0);
    }

    [TestCase(ErpImportFileStatus.Importing)]
    [TestCase(ErpImportFileStatus.Completed)]
    [TestCase(ErpImportFileStatus.Failed)]
    [TestCase(ErpImportFileStatus.Interrupted)]
    public async Task Claim_OfANonReceivedRow_ReturnsFalse(ErpImportFileStatus status)
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, status);

        await using var context = NewContext();
        (await new ErpImportFileRepository(context).TryClaimAsync(file.Id, 0, InstanceA, DateTime.UtcNow)).ShouldBeFalse();

        (await ReloadAsync(file.Id)).Status.ShouldBe(status);
    }

    [Test]
    public async Task Heartbeat_FromAForeignInstance_ReturnsFalseAndChangesNothing_WhileTheOwnerSucceeds()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);
        var claimedAt = DateTime.UtcNow.AddSeconds(-30);

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);
        (await repository.TryClaimAsync(file.Id, 0, InstanceA, claimedAt)).ShouldBeTrue();

        var progress = new ErpImportFileProgress(10, 4, 1, 1, 1, 0, 1);
        (await repository.HeartbeatAsync(file.Id, InstanceB, 1, progress, DateTime.UtcNow)).ShouldBeFalse();

        var afterForeign = await ReloadAsync(file.Id);
        afterForeign.ProcessedOrders.ShouldBe(0);
        afterForeign.HeartbeatAt!.Value.ShouldBe(claimedAt, TimestampTolerance);

        var beatAt = DateTime.UtcNow;
        (await repository.HeartbeatAsync(file.Id, InstanceA, 1, progress, beatAt)).ShouldBeTrue();

        var afterOwner = await ReloadAsync(file.Id);
        afterOwner.Status.ShouldBe(ErpImportFileStatus.Importing);
        afterOwner.HeartbeatAt!.Value.ShouldBe(beatAt, TimestampTolerance);
        afterOwner.TotalOrders.ShouldBe(10);
        afterOwner.ProcessedOrders.ShouldBe(4);
        afterOwner.NewCount.ShouldBe(1);
        afterOwner.UpdatedCount.ShouldBe(1);
        afterOwner.UnchangedCount.ShouldBe(1);
        afterOwner.SupersededCount.ShouldBe(0);
        afterOwner.RejectedCount.ShouldBe(1);
    }

    [Test]
    public async Task Heartbeat_OnAReapedRow_ReturnsFalse()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Interrupted, configure: f => f.ClaimedBy = InstanceA);

        await using var context = NewContext();
        (await new ErpImportFileRepository(context)
            .HeartbeatAsync(file.Id, InstanceA, 0, ErpImportFileProgress.Empty, DateTime.UtcNow)).ShouldBeFalse();
    }

    [Test]
    public async Task Finish_FromAForeignInstance_ReturnsFalse_AndTheOwnerFinishesWithAClampedReason()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);
        (await repository.TryClaimAsync(file.Id, 0, InstanceA, DateTime.UtcNow)).ShouldBeTrue();

        var longReason = new string('r', ErpImportFileLimits.FailureReasonMaxLength + 500);
        (await repository.FinishAsync(file.Id, InstanceB, 1, ErpImportFileStatus.Failed, ErpImportFileProgress.Empty, longReason, DateTime.UtcNow))
            .ShouldBeFalse();
        (await ReloadAsync(file.Id)).Status.ShouldBe(ErpImportFileStatus.Importing);

        (await repository.FinishAsync(file.Id, InstanceA, 1, ErpImportFileStatus.Failed, ErpImportFileProgress.Empty, longReason, DateTime.UtcNow))
            .ShouldBeTrue();

        var stored = await ReloadAsync(file.Id);
        stored.Status.ShouldBe(ErpImportFileStatus.Failed);
        stored.FinishedAt.ShouldNotBeNull();
        stored.FailureReason!.Length.ShouldBe(ErpImportFileLimits.FailureReasonMaxLength);
    }

    [Test]
    public async Task Finish_WithWarnings_StoresTheCountersAndNoReason()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);
        (await repository.TryClaimAsync(file.Id, 0, InstanceA, DateTime.UtcNow)).ShouldBeTrue();

        var progress = new ErpImportFileProgress(5, 5, 2, 1, 0, 1, 1);
        (await repository.FinishAsync(file.Id, InstanceA, 1, ErpImportFileStatus.CompletedWithWarnings, progress, "ignored", DateTime.UtcNow))
            .ShouldBeTrue();

        var stored = await ReloadAsync(file.Id);
        stored.Status.ShouldBe(ErpImportFileStatus.CompletedWithWarnings);
        stored.FailureReason.ShouldBeNull();
        stored.TotalOrders.ShouldBe(5);
        stored.ProcessedOrders.ShouldBe(5);
        stored.NewCount.ShouldBe(2);
        stored.UpdatedCount.ShouldBe(1);
        stored.SupersededCount.ShouldBe(1);
        stored.RejectedCount.ShouldBe(1);
        stored.ClaimedBy.ShouldBe(InstanceA);
    }

    [Test]
    public async Task ReapStale_FlipsOnlyImportingRowsWithAnOldHeartbeat()
    {
        // Fixed timestamps far in the past: ReapStaleAsync updates globally, so the cutoff is chosen such
        // that only rows of this fixture can ever match on the shared database.
        var dropPoint = await GivenDropPointAsync();
        var now = new DateTime(2000, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        var staleBeat = now.AddMinutes(-20);
        var cutoff = now - ErpImportFileTiming.StaleAfter;

        var staleImporting = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Importing, configure: f =>
        {
            f.ClaimedBy = InstanceA;
            f.StartedAt = staleBeat.AddMinutes(-1);
            f.HeartbeatAt = staleBeat;
        });
        var freshImporting = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Importing, configure: f =>
        {
            f.ClaimedBy = InstanceA;
            f.StartedAt = now.AddMinutes(-30);
            f.HeartbeatAt = now.AddMinutes(-1);
        });
        var oldReceived = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, receivedAt: now.AddHours(-2));
        var oldFailed = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, configure: f =>
        {
            f.HeartbeatAt = staleBeat;
            f.FailureReason = "broken xml";
        });

        await using var context = NewContext();
        var reaped = await new ErpImportFileRepository(context).ReapStaleAsync(cutoff);

        reaped.ShouldBeGreaterThanOrEqualTo(1);

        var stale = await ReloadAsync(staleImporting.Id);
        stale.Status.ShouldBe(ErpImportFileStatus.Interrupted);
        stale.FailureReason.ShouldBe(ErpImportFileFailureReasons.StaleHeartbeat);
        stale.FinishedAt!.Value.ShouldBe(staleBeat, TimestampTolerance);

        (await ReloadAsync(freshImporting.Id)).Status.ShouldBe(ErpImportFileStatus.Importing);
        (await ReloadAsync(oldReceived.Id)).Status.ShouldBe(ErpImportFileStatus.Received);

        var failed = await ReloadAsync(oldFailed.Id);
        failed.Status.ShouldBe(ErpImportFileStatus.Failed);
        failed.FailureReason.ShouldBe("broken xml");
    }

    [TestCase(ErpImportFileStatus.Failed)]
    [TestCase(ErpImportFileStatus.Interrupted)]
    public async Task ResetForRetry_FromFailedOrInterrupted_ReturnsToReceivedAndClearsTheAttemptState(ErpImportFileStatus status)
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, status, configure: f =>
        {
            f.Attempt = 1;
            f.ClaimedBy = InstanceA;
            f.StartedAt = DateTime.UtcNow.AddMinutes(-15);
            f.HeartbeatAt = DateTime.UtcNow.AddMinutes(-12);
            f.FinishedAt = DateTime.UtcNow.AddMinutes(-12);
            f.FailureReason = "boom";
            f.TotalOrders = 7;
            f.ProcessedOrders = 3;
            f.RejectedCount = 3;
        });

        await using var context = NewContext();
        (await new ErpImportFileRepository(context).TryResetForRetryAsync(file.Id)).ShouldBeTrue();

        var stored = await ReloadAsync(file.Id);
        stored.Status.ShouldBe(ErpImportFileStatus.Received);
        stored.Attempt.ShouldBe(1);
        stored.ClaimedBy.ShouldBeNull();
        stored.StartedAt.ShouldBeNull();
        stored.HeartbeatAt.ShouldBeNull();
        stored.FinishedAt.ShouldBeNull();
        stored.FailureReason.ShouldBeNull();
        stored.TotalOrders.ShouldBeNull();
        stored.ProcessedOrders.ShouldBe(0);
        stored.RejectedCount.ShouldBe(0);
    }

    [TestCase(ErpImportFileStatus.Received)]
    [TestCase(ErpImportFileStatus.Importing)]
    [TestCase(ErpImportFileStatus.Completed)]
    [TestCase(ErpImportFileStatus.CompletedWithWarnings)]
    public async Task ResetForRetry_FromAnyOtherStatus_ReturnsFalseAndChangesNothing(ErpImportFileStatus status)
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, status, configure: f => f.ProcessedOrders = 4);

        await using var context = NewContext();
        (await new ErpImportFileRepository(context).TryResetForRetryAsync(file.Id)).ShouldBeFalse();

        var stored = await ReloadAsync(file.Id);
        stored.Status.ShouldBe(status);
        stored.ProcessedOrders.ShouldBe(4);
    }

    [Test]
    public async Task Claim_WithAStaleExpectedAttempt_ReturnsFalseAndChangesNothing()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, configure: f => f.Attempt = 2);

        await using var context = NewContext();
        (await new ErpImportFileRepository(context).TryClaimAsync(file.Id, 1, InstanceA, DateTime.UtcNow)).ShouldBeFalse();

        var stored = await ReloadAsync(file.Id);
        stored.Status.ShouldBe(ErpImportFileStatus.Received);
        stored.Attempt.ShouldBe(2);
        stored.ClaimedBy.ShouldBeNull();
    }

    [Test]
    public async Task HeartbeatAndFinish_OfAnOlderAttemptOfTheSameInstance_ReturnFalse_WhileTheCurrentAttemptSucceeds()
    {
        // A stale run of the same process: it claimed attempt 1, the row was reaped and retried, and the
        // same instance claimed it again as attempt 2. The attempt-1 run must not write into attempt 2.
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, configure: f => f.Attempt = 1);

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);
        (await repository.TryClaimAsync(file.Id, 1, InstanceA, DateTime.UtcNow)).ShouldBeTrue();

        var staleProgress = new ErpImportFileProgress(3, 3, 3, 0, 0, 0, 0);
        (await repository.HeartbeatAsync(file.Id, InstanceA, 1, staleProgress, DateTime.UtcNow)).ShouldBeFalse();
        (await repository.FinishAsync(file.Id, InstanceA, 1, ErpImportFileStatus.Completed, staleProgress, null, DateTime.UtcNow))
            .ShouldBeFalse();

        var afterStale = await ReloadAsync(file.Id);
        afterStale.Status.ShouldBe(ErpImportFileStatus.Importing);
        afterStale.Attempt.ShouldBe(2);
        afterStale.ProcessedOrders.ShouldBe(0);

        var progress = new ErpImportFileProgress(1, 1, 1, 0, 0, 0, 0);
        (await repository.HeartbeatAsync(file.Id, InstanceA, 2, progress, DateTime.UtcNow)).ShouldBeTrue();
        (await repository.FinishAsync(file.Id, InstanceA, 2, ErpImportFileStatus.Completed, progress, null, DateTime.UtcNow))
            .ShouldBeTrue();
        (await ReloadAsync(file.Id)).Status.ShouldBe(ErpImportFileStatus.Completed);
    }

    [Test]
    public async Task Finish_AsInterrupted_StoresTheReason()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);
        (await repository.TryClaimAsync(file.Id, 0, InstanceA, DateTime.UtcNow)).ShouldBeTrue();

        var progress = new ErpImportFileProgress(4, 2, 2, 0, 0, 0, 0);
        const string reason = ErpImportFileFailureReasons.ImportAbortedPrefix + "disk gone";
        (await repository.FinishAsync(file.Id, InstanceA, 1, ErpImportFileStatus.Interrupted, progress, reason, DateTime.UtcNow))
            .ShouldBeTrue();

        var stored = await ReloadAsync(file.Id);
        stored.Status.ShouldBe(ErpImportFileStatus.Interrupted);
        stored.FailureReason.ShouldBe(reason);
        stored.FinishedAt.ShouldNotBeNull();
        stored.ProcessedOrders.ShouldBe(2);
    }

    [Test]
    public async Task ListReceived_ReturnsOnlyLiveReceivedRowsOfTheDropPoint_OldestFirst()
    {
        var dropPoint = await GivenDropPointAsync();
        var otherDropPoint = await GivenDropPointAsync();
        var now = DateTime.UtcNow;

        var newest = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, receivedAt: now.AddMinutes(-1));
        var oldest = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, receivedAt: now.AddHours(-3));
        var middle = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, receivedAt: now.AddHours(-1));
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Importing, receivedAt: now.AddHours(-4));
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, receivedAt: now.AddHours(-4));
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, receivedAt: now.AddHours(-5), configure: f => f.IsDeleted = true);
        await GivenFileAsync(otherDropPoint.Id, ErpImportFileStatus.Received, receivedAt: now.AddHours(-6));

        await using var context = NewContext();
        var rows = await new ErpImportFileRepository(context).ListReceivedAsync(dropPoint.Id);

        rows.Select(r => r.Id).ShouldBe([oldest.Id, middle.Id, newest.Id]);
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task GetByStorageKeys_ReturnsOnlyTheRequestedLiveRows()
    {
        var dropPoint = await GivenDropPointAsync();
        var first = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);
        var second = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed);
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);
        var deleted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, configure: f => f.IsDeleted = true);

        await using var context = NewContext();
        var rows = await new ErpImportFileRepository(context)
            .GetByStorageKeysAsync([first.StorageKey, second.StorageKey, deleted.StorageKey, TestPrefix + "unknown"]);

        rows.Select(r => r.Id).ShouldBe([first.Id, second.Id], ignoreOrder: true);
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task FindDuplicate_ReturnsTheEarliestSameHashRowOfTheDropPoint_ExcludingTheGivenId()
    {
        var dropPoint = await GivenDropPointAsync();
        var otherDropPoint = await GivenDropPointAsync();
        var now = DateTime.UtcNow;

        await GivenFileAsync(otherDropPoint.Id, ErpImportFileStatus.Completed, sha256: ShaA, receivedAt: now.AddDays(-5));
        var earliest = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, sha256: ShaA, receivedAt: now.AddDays(-2));
        var later = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, sha256: ShaA, receivedAt: now,
            configure: f => f.DuplicateOfFileId = earliest.Id);
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, sha256: ShaB, receivedAt: now.AddDays(-3));

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);

        (await repository.FindDuplicateAsync(dropPoint.Id, ShaA, later.Id))!.Id.ShouldBe(earliest.Id);
        (await repository.FindDuplicateAsync(dropPoint.Id, ShaA, null))!.Id.ShouldBe(earliest.Id);
        (await repository.FindDuplicateAsync(dropPoint.Id, ShaA, earliest.Id))!.Id.ShouldBe(later.Id);
        (await repository.FindDuplicateAsync(otherDropPoint.Id, ShaB, null)).ShouldBeNull();
    }

    [Test]
    public async Task ListForDropPoint_CapsOnlyProcessedRows_NewestReceivedFirst()
    {
        var dropPoint = await GivenDropPointAsync();
        var otherDropPoint = await GivenDropPointAsync();
        var now = DateTime.UtcNow;

        var oldestCompleted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, receivedAt: now.AddHours(-10));
        var completed = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, receivedAt: now.AddHours(-3));
        var warnings = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.CompletedWithWarnings, receivedAt: now.AddHours(-2));
        var oldFailed = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, receivedAt: now.AddHours(-20));
        var interrupted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Interrupted, receivedAt: now.AddHours(-15));
        var received = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, receivedAt: now.AddMinutes(-1));
        var importing = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Importing, receivedAt: now.AddMinutes(-5));
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, configure: f => f.IsDeleted = true);
        await GivenFileAsync(otherDropPoint.Id, ErpImportFileStatus.Received);

        await using var context = NewContext();
        var rows = await new ErpImportFileRepository(context).ListForDropPointAsync(dropPoint.Id, processedCap: 2);

        rows.Select(r => r.Id).ShouldBe(
        [
            received.Id,
            importing.Id,
            warnings.Id,
            completed.Id,
            interrupted.Id,
            oldFailed.Id
        ]);
        rows.ShouldNotContain(r => r.Id == oldestCompleted.Id);
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task StorageKey_IsUniqueAmongLiveRows_ButASoftDeletedRowDoesNotBlock()
    {
        var dropPoint = await GivenDropPointAsync();
        var storageKey = NewStorageKey();
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, storageKey: storageKey, configure: f => f.IsDeleted = true);
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, storageKey: storageKey);

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);
        repository.Add(NewFile(dropPoint.Id, ErpImportFileStatus.Received, storageKey, ShaA, DateTime.UtcNow));

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Test]
    public async Task GetById_ReturnsTheLiveRow_AndNullForASoftDeletedOne()
    {
        var dropPoint = await GivenDropPointAsync();
        var live = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed);
        var deleted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, configure: f => f.IsDeleted = true);

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);

        (await repository.GetByIdAsync(live.Id)).ShouldNotBeNull().StorageKey.ShouldBe(live.StorageKey);
        (await repository.GetByIdAsync(deleted.Id)).ShouldBeNull();
        (await repository.GetByIdsAsync([live.Id, deleted.Id])).Select(f => f.Id).ShouldBe(new[] { live.Id });
        context.ChangeTracker.Entries().ShouldBeEmpty("ledger reads are untracked");
    }

    [Test]
    public async Task CountByStatus_GroupsTheDropPointsRows_AndCountsStaleImportingRowsAsFailed()
    {
        var dropPoint = await GivenDropPointAsync();
        var otherDropPoint = await GivenDropPointAsync();
        var now = DateTime.UtcNow;
        var cutoff = now - ErpImportFileTiming.StaleAfter;
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Importing, configure: f => { f.StartedAt = now; f.HeartbeatAt = now; });
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Importing, configure: f => { f.StartedAt = cutoff.AddMinutes(-5); f.HeartbeatAt = cutoff.AddMinutes(-1); });
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed);
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.CompletedWithWarnings);
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed);
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Interrupted);
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, configure: f => f.IsDeleted = true);
        await GivenFileAsync(otherDropPoint.Id, ErpImportFileStatus.Received);

        await using var context = NewContext();
        var counts = await new ErpImportFileRepository(context).CountByStatusAsync(dropPoint.Id, cutoff);

        counts.ShouldBe(new ErpImportFileStatusCounts(Received: 1, Importing: 1, Processed: 2, Failed: 3));
    }

    [Test]
    public async Task GetLatestFinished_ReturnsTheDropPointsRowWithTheNewestFinishTime()
    {
        var dropPoint = await GivenDropPointAsync();
        var now = DateTime.UtcNow;
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, configure: f => f.FinishedAt = now.AddMinutes(-10));
        var newest = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, configure: f => f.FinishedAt = now.AddMinutes(-1));
        await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Importing, configure: f => { f.StartedAt = now; f.HeartbeatAt = now; });

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);

        (await repository.GetLatestFinishedAsync(dropPoint.Id)).ShouldNotBeNull().Id.ShouldBe(newest.Id);
        (await repository.GetLatestFinishedAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [TestCase(ErpImportFileStatus.Failed, true)]
    [TestCase(ErpImportFileStatus.Interrupted, true)]
    [TestCase(ErpImportFileStatus.Received, false)]
    [TestCase(ErpImportFileStatus.Importing, false)]
    [TestCase(ErpImportFileStatus.Completed, false)]
    public async Task MarkFailed_OnlyFromFailedOrInterrupted(ErpImportFileStatus status, bool expected)
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, status);
        var now = DateTime.UtcNow;

        await using var context = NewContext();
        (await new ErpImportFileRepository(context).TryMarkFailedAsync(file.Id, ErpImportFileFailureReasons.FileChangedAfterReceipt, now)).ShouldBe(expected);

        var stored = await ReloadAsync(file.Id);
        stored.Status.ShouldBe(expected ? ErpImportFileStatus.Failed : status);
        if (expected)
        {
            stored.FailureReason.ShouldBe(ErpImportFileFailureReasons.FileChangedAfterReceipt);
            stored.FinishedAt!.Value.ShouldBe(now, TimestampTolerance);
        }
    }

    [TestCase(ErpImportFileStatus.Failed, true)]
    [TestCase(ErpImportFileStatus.Interrupted, true)]
    [TestCase(ErpImportFileStatus.Received, false)]
    [TestCase(ErpImportFileStatus.Importing, false)]
    [TestCase(ErpImportFileStatus.CompletedWithWarnings, false)]
    public async Task SoftDelete_OnlyFromFailedOrInterrupted_WritesTheAuditFieldsAndHidesTheRow(ErpImportFileStatus status, bool expected)
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, status);
        var now = DateTime.UtcNow;

        await using var context = NewContext(actor: TestPrefix + "admin-id");
        var repository = new ErpImportFileRepository(context);
        (await repository.TrySoftDeleteAsync(file.Id, now)).ShouldBe(expected);

        var stored = await ReloadAsync(file.Id);
        stored.IsDeleted.ShouldBe(expected);
        if (expected)
        {
            stored.CurrentUserDeleted.ShouldBe(TestPrefix + "admin-id", "the bulk update stamps the context's audit actor (NameIdentifier)");
            stored.DeletedTime!.Value.ShouldBe(now, TimestampTolerance);
            (await repository.GetByIdAsync(file.Id)).ShouldBeNull();
            (await repository.GetByStorageKeysAsync([file.StorageKey])).ShouldBeEmpty();
            (await repository.TrySoftDeleteAsync(file.Id, now)).ShouldBeFalse("a deleted row cannot be deleted twice");
        }
    }

    [Test]
    public async Task GetKeyStates_ReturnsKeyAndDeletedFlagOfLiveAndSoftDeletedRows_AndNothingForUnknownKeys()
    {
        var dropPoint = await GivenDropPointAsync();
        var live = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received);
        var deleted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed);
        await using (var deleteContext = NewContext())
        {
            (await new ErpImportFileRepository(deleteContext).TrySoftDeleteAsync(deleted.Id, DateTime.UtcNow)).ShouldBeTrue();
        }

        await using var context = NewContext();
        var states = await new ErpImportFileRepository(context)
            .GetKeyStatesAsync([live.StorageKey, deleted.StorageKey, TestPrefix + "unknown.xml"]);

        states.OrderBy(s => s.IsDeleted).ShouldBe(new[]
        {
            new ErpImportFileKeyState(live.StorageKey, false),
            new ErpImportFileKeyState(deleted.StorageKey, true)
        });
    }

    [Test]
    public async Task ApplyLegacyState_RewritesANonImportingRow_ButLeavesAnImportingRowAlone()
    {
        var dropPoint = await GivenDropPointAsync();
        var received = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Received, configure: f => f.ProcessedOrders = 3);
        var importing = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Importing, configure: f =>
        {
            f.StartedAt = DateTime.UtcNow;
            f.HeartbeatAt = DateTime.UtcNow;
            f.ClaimedBy = InstanceA;
        });
        var finishedAt = DateTime.UtcNow.AddDays(-3);
        var newKey = NewStorageKey();

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);
        (await repository.TryApplyLegacyStateAsync(received.Id, newKey, ErpImportFileStatus.Failed, "legacy reason", finishedAt)).ShouldBeTrue();
        (await repository.TryApplyLegacyStateAsync(importing.Id, NewStorageKey(), ErpImportFileStatus.Completed, null, finishedAt)).ShouldBeFalse();

        var migrated = await ReloadAsync(received.Id);
        migrated.StorageKey.ShouldBe(newKey);
        migrated.Status.ShouldBe(ErpImportFileStatus.Failed);
        migrated.FailureReason.ShouldBe("legacy reason");
        migrated.FinishedAt!.Value.ShouldBe(finishedAt, TimestampTolerance);
        migrated.TotalOrders.ShouldBeNull();
        migrated.ProcessedOrders.ShouldBe(0);
        (await ReloadAsync(importing.Id)).Status.ShouldBe(ErpImportFileStatus.Importing);
    }

    [Test]
    public async Task ExceptionQueries_ListOnlyOpenReferencedRejections_ResolveByFile_AndLinkByFileKey()
    {
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.CompletedWithWarnings);
        var otherFile = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed);
        var legacyKey = TestPrefix + "erp/orders/legacy-orders.xml";
        var open = NewException(file.Id, file.StorageKey, "ORD-1");
        var resolved = NewException(file.Id, file.StorageKey, "ORD-2", resolvedAt: DateTime.UtcNow);
        var unreferenced = NewException(otherFile.Id, otherFile.StorageKey, reference: null);
        var otherFilesRejection = NewException(otherFile.Id, otherFile.StorageKey, "ORD-3");
        var legacy = NewException(importFileId: null, legacyKey, "ORD-4");
        await using (var seed = NewContext())
        {
            seed.Set<ErpImportException>().AddRange(open, resolved, unreferenced, otherFilesRejection, legacy);
            await seed.SaveChangesAsync();
        }

        await using var context = NewContext();
        var repository = new ErpImportExceptionRepository(context, Substitute.For<ILogger<ErpImportException>>());

        (await repository.GetOpenRejectedOrdersAsync([file.Id])).Select(e => e.Id).ShouldBe(new[] { open.Id });
        (await repository.GetOpenRejectedOrdersAsync([otherFile.Id])).Select(e => e.Id).ShouldBe(new[] { otherFilesRejection.Id });

        (await repository.ResolveOpenForImportFileAsync(otherFile.Id, DateTime.UtcNow)).ShouldBe(2);
        (await repository.GetOpenRejectedOrdersAsync([otherFile.Id])).ShouldBeEmpty();

        (await repository.LinkToImportFileAsync(legacyKey, file.Id)).ShouldBe(1);
        (await repository.LinkToImportFileAsync(legacyKey, otherFile.Id)).ShouldBe(0, "an already linked exception is never re-linked");
        (await repository.GetOpenRejectedOrdersAsync([file.Id])).Select(e => e.ExternalOrderReference).ShouldBe(new[] { "ORD-1", "ORD-4" }, ignoreOrder: true);
    }

    private static ErpImportException NewException(Guid? importFileId, string fileKey, string? reference, DateTime? resolvedAt = null)
    {
        return new ErpImportException
        {
            Id = Guid.NewGuid(),
            SourceSystemId = TestPrefix + "SRC",
            FileKey = fileKey,
            ImportFileId = importFileId,
            ExternalOrderReference = reference,
            Reason = TestPrefix + "reason",
            ResolvedAt = resolvedAt
        };
    }

    private static ErpImportFile NewFile(Guid dropPointId, ErpImportFileStatus status, string storageKey, string sha256, DateTime receivedAt)
    {
        return new ErpImportFile
        {
            Id = Guid.NewGuid(),
            DropPointId = dropPointId,
            SourceSystemId = TestPrefix + "SRC",
            StorageKey = storageKey,
            FileName = "orders.xml",
            SizeBytes = 1234,
            Sha256 = sha256,
            Channel = ErpImportChannel.UiUpload,
            UploadedBy = TestPrefix + "user",
            Status = status,
            ReceivedAt = receivedAt,
        };
    }

    private static string NewStorageKey() => $"{TestPrefix}erp/orders/{Guid.NewGuid():N}-orders.xml";

    private static async Task<ErpDropPoint> GivenDropPointAsync()
    {
        var dropPoint = new ErpDropPoint
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "drop point",
            SourceSystemId = TestPrefix + Guid.NewGuid().ToString("N"),
            BucketPrefix = TestPrefix + "bucket",
        };

        await using var context = NewContext();
        context.ErpDropPoints.Add(dropPoint);
        await context.SaveChangesAsync();
        return dropPoint;
    }

    private static async Task<ErpImportFile> GivenFileAsync(
        Guid dropPointId,
        ErpImportFileStatus status,
        string? storageKey = null,
        string sha256 = ShaA,
        DateTime? receivedAt = null,
        Action<ErpImportFile>? configure = null)
    {
        var file = NewFile(dropPointId, status, storageKey ?? NewStorageKey(), sha256, receivedAt ?? DateTime.UtcNow);
        configure?.Invoke(file);

        await using var context = NewContext();
        context.ErpImportFiles.Add(file);
        await context.SaveChangesAsync();
        return file;
    }

    private static async Task<ErpImportFile> ReloadAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.ErpImportFiles.AsNoTracking().IgnoreQueryFilters().SingleAsync(f => f.Id == id);
    }

    private static DataBaseContext NewContext(string? actor = null)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        if (actor != null)
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor)], AuthenticationType))
            };
            httpContextAccessor.HttpContext.Returns(httpContext);
        }

        return new DataBaseContext(options, httpContextAccessor);
    }

    private static async Task CleanupAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM erp_import_exception WHERE source_system_id LIKE {0}",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM erp_import_file WHERE storage_key LIKE {0} OR drop_point_id IN (SELECT id FROM erp_drop_points WHERE source_system_id LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM erp_drop_points WHERE source_system_id LIKE {0}",
            TestPrefix + "%");
    }
}
