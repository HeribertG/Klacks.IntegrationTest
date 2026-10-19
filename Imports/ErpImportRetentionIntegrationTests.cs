// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the ERP import retention purge against the real PostgreSQL database (repositories,
/// UnitOfWork transaction and conditional ExecuteDeleteAsync) with the in-memory object storage. They prove: an
/// expired Completed/CompletedWithWarnings row loses its stored object, its row and every linked exception, while
/// old Failed/Interrupted rows and fresh Completed rows keep row and object (89 vs. 91 days, clear of the
/// microsecond timestamp precision); the database nulls the duplicate link of a later row; a soft-deleted row is
/// purged only when its object is gone; and a row whose status changed after it was listed is not deleted by the
/// conditional delete. Cleanup deletes ONLY rows this fixture created: its drop points and exceptions carry the
/// INTEGRATION_TEST_ERPRETENTION_ source-system prefix and its ledger rows the same storage-key prefix.
/// </summary>

using Klacks.Api.Application.Services.Imports;
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
public class ErpImportRetentionIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_ERPRETENTION_";
    private const string Sha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const int ExpiredDays = 91;
    private const int FreshDays = 89;

    private InMemoryObjectStorageService _storage = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();
    }

    [SetUp]
    public void SetUp()
    {
        _storage = new InMemoryObjectStorageService();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
    }

    [Test]
    public async Task Purge_RemovesExpiredProcessedFilesWithTheirExceptions_AndKeepsFailedAndFreshFiles()
    {
        // Arrange
        var dropPoint = await GivenDropPointAsync();
        var expiredCompleted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, finishedDaysAgo: ExpiredDays);
        var expiredWarnings = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.CompletedWithWarnings, finishedDaysAgo: ExpiredDays);
        var legacyWithoutFinish = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, finishedDaysAgo: null, receivedDaysAgo: ExpiredDays);
        var freshCompleted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, finishedDaysAgo: FreshDays, receivedDaysAgo: ExpiredDays);
        var oldFailed = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, finishedDaysAgo: 400);
        var oldInterrupted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Interrupted, finishedDaysAgo: 400);
        var laterDuplicate = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, finishedDaysAgo: 1,
            configure: f => f.DuplicateOfFileId = expiredCompleted.Id);

        var openRejection = await GivenExceptionAsync(expiredWarnings, resolvedAt: null);
        var resolvedRejection = await GivenExceptionAsync(expiredWarnings, resolvedAt: DateTime.UtcNow.AddDays(-95));
        var failedFileException = await GivenExceptionAsync(oldFailed, resolvedAt: null);

        // Act
        var removed = await PurgeAsync(dropPoint);

        // Assert
        removed.ShouldBe(3);
        foreach (var gone in new[] { expiredCompleted, expiredWarnings, legacyWithoutFinish })
        {
            (await ExistsAsync(gone.Id)).ShouldBeFalse();
            _storage.Contains(gone.StorageKey).ShouldBeFalse();
        }

        foreach (var kept in new[] { freshCompleted, oldFailed, oldInterrupted, laterDuplicate })
        {
            (await ExistsAsync(kept.Id)).ShouldBeTrue();
            _storage.Contains(kept.StorageKey).ShouldBeTrue();
        }

        (await ExceptionExistsAsync(openRejection.Id)).ShouldBeFalse();
        (await ExceptionExistsAsync(resolvedRejection.Id)).ShouldBeFalse();
        (await ExceptionExistsAsync(failedFileException.Id)).ShouldBeTrue();
        (await ReloadAsync(laterDuplicate.Id)).DuplicateOfFileId.ShouldBeNull();
    }

    [Test]
    public async Task Purge_SoftDeletedRow_IsPurgedOnlyOnceItsObjectIsGone()
    {
        // Arrange
        var dropPoint = await GivenDropPointAsync();
        var withoutObject = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, finishedDaysAgo: 120, storeObject: false,
            configure: f => SoftDelete(f, daysAgo: ExpiredDays));
        var withLeftover = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, finishedDaysAgo: 120,
            configure: f => SoftDelete(f, daysAgo: ExpiredDays));
        var recentlyDeleted = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Interrupted, finishedDaysAgo: 120, storeObject: false,
            configure: f => SoftDelete(f, daysAgo: FreshDays));
        var exception = await GivenExceptionAsync(withoutObject, resolvedAt: DateTime.UtcNow.AddDays(-ExpiredDays));

        // Act
        var removed = await PurgeAsync(dropPoint);

        // Assert
        removed.ShouldBe(1);
        (await ExistsAsync(withoutObject.Id)).ShouldBeFalse();
        (await ExceptionExistsAsync(exception.Id)).ShouldBeFalse();
        (await ExistsAsync(withLeftover.Id)).ShouldBeTrue();
        _storage.Contains(withLeftover.StorageKey).ShouldBeTrue("the purge never deletes the object of a soft-deleted row");
        (await ExistsAsync(recentlyDeleted.Id)).ShouldBeTrue();
    }

    [Test]
    public async Task ConditionalDelete_OfARowWhoseStatusChangedAfterListing_KeepsTheRow()
    {
        // Arrange
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Completed, finishedDaysAgo: ExpiredDays);
        var cutoff = DateTime.UtcNow - ErpImportFileTiming.RetentionPeriod;

        await using var context = NewContext();
        var repository = new ErpImportFileRepository(context);
        (await repository.ListExpiredAsync(dropPoint.Id, cutoff, ErpImportFileTiming.RetentionBatchSize))
            .Select(c => c.Id).ShouldContain(file.Id);

        await using (var concurrent = NewContext())
        {
            await concurrent.ErpImportFiles
                .Where(f => f.Id == file.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.Status, ErpImportFileStatus.Failed));
        }

        // Act
        var deleted = await repository.TryDeleteExpiredProcessedAsync(file.Id, cutoff);

        // Assert
        deleted.ShouldBeFalse();
        (await ReloadAsync(file.Id)).Status.ShouldBe(ErpImportFileStatus.Failed);
    }

    [Test]
    public async Task ConditionalPurge_OfARowThatIsNoLongerDeleted_KeepsTheRow()
    {
        // Arrange
        var dropPoint = await GivenDropPointAsync();
        var file = await GivenFileAsync(dropPoint.Id, ErpImportFileStatus.Failed, finishedDaysAgo: 120, storeObject: false,
            configure: f => SoftDelete(f, daysAgo: ExpiredDays));
        var cutoff = DateTime.UtcNow - ErpImportFileTiming.RetentionPeriod;

        await using (var concurrent = NewContext())
        {
            await concurrent.ErpImportFiles
                .IgnoreQueryFilters()
                .Where(f => f.Id == file.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.IsDeleted, false));
        }

        // Act
        await using var context = NewContext();
        var purged = await new ErpImportFileRepository(context).TryPurgeExpiredDeletedAsync(file.Id, cutoff);

        // Assert
        purged.ShouldBeFalse();
        (await ExistsAsync(file.Id)).ShouldBeTrue();
    }

    private async Task<int> PurgeAsync(ErpDropPoint dropPoint)
    {
        await using var context = NewContext();
        var service = new ErpImportRetentionService(
            new ErpImportFileRepository(context),
            new ErpImportExceptionRepository(context, Substitute.For<ILogger<ErpImportException>>()),
            _storage,
            new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>()),
            TimeProvider.System,
            Substitute.For<ILogger<ErpImportRetentionService>>());

        return await service.PurgeExpiredAsync(dropPoint);
    }

    private static void SoftDelete(ErpImportFile file, int daysAgo)
    {
        file.IsDeleted = true;
        file.DeletedTime = DateTime.UtcNow.AddDays(-daysAgo);
        file.CurrentUserDeleted = TestPrefix + "admin";
    }

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

    private async Task<ErpImportFile> GivenFileAsync(
        Guid dropPointId,
        ErpImportFileStatus status,
        int? finishedDaysAgo,
        int? receivedDaysAgo = null,
        bool storeObject = true,
        Action<ErpImportFile>? configure = null)
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var file = new ErpImportFile
        {
            Id = id,
            DropPointId = dropPointId,
            SourceSystemId = TestPrefix + "SRC",
            StorageKey = $"{TestPrefix}erp/orders/{id:N}-orders.xml",
            FileName = "orders.xml",
            SizeBytes = 3,
            Sha256 = Sha,
            Channel = ErpImportChannel.InboxFolder,
            Status = status,
            ReceivedAt = now.AddDays(-(receivedDaysAgo ?? finishedDaysAgo ?? 0)).AddMinutes(-1),
            FinishedAt = finishedDaysAgo is { } days ? now.AddDays(-days) : null,
        };
        configure?.Invoke(file);

        await using (var context = NewContext())
        {
            context.ErpImportFiles.Add(file);
            await context.SaveChangesAsync();
        }

        if (storeObject)
        {
            await _storage.UploadAsync(file.StorageKey, new MemoryStream([1, 2, 3]));
        }

        return file;
    }

    private static async Task<ErpImportException> GivenExceptionAsync(ErpImportFile file, DateTime? resolvedAt)
    {
        var exception = new ErpImportException
        {
            Id = Guid.NewGuid(),
            SourceSystemId = TestPrefix + "SRC",
            FileKey = file.StorageKey,
            ImportFileId = file.Id,
            ExternalOrderReference = TestPrefix + "ORD",
            Reason = TestPrefix + "reason",
            ResolvedAt = resolvedAt
        };

        await using var context = NewContext();
        context.Set<ErpImportException>().Add(exception);
        await context.SaveChangesAsync();
        return exception;
    }

    private static async Task<bool> ExistsAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.ErpImportFiles.IgnoreQueryFilters().AnyAsync(f => f.Id == id);
    }

    private static async Task<bool> ExceptionExistsAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.Set<ErpImportException>().IgnoreQueryFilters().AnyAsync(e => e.Id == id);
    }

    private static async Task<ErpImportFile> ReloadAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.ErpImportFiles.AsNoTracking().IgnoreQueryFilters().SingleAsync(f => f.Id == id);
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
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
