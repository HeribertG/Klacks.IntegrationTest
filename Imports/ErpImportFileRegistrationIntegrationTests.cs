// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the registration half of the ERP import file ledger against the real PostgreSQL
/// database: an upload through UploadErpOrderFileCommandHandler persists a Received row whose SHA-256
/// equals the hash of the stored object, a second identical delivery links the first as duplicate, the
/// inbox registration renames an ERP-named file to a unique key, persists only files directly in the inbox
/// and skips keys that already have a row,
/// and a concurrent registration of the same key (stale read, then the real partial unique index fires)
/// is detached so the next commit on the same context does not write it again.
/// Cleanup deletes ONLY rows this fixture created: its drop points carry the INTEGRATION_TEST_ERPREG_
/// source-system prefix, and ledger rows are deleted by those drop points.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Services.Imports;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Imports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Imports;

[TestFixture]
[Category("RealDatabase")]
public class ErpImportFileRegistrationIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_ERPREG_";
    private const string OrderXml = "<ErpOrderImport sourceSystemId=\"erp-example\" />";
    private const string TokenName = "INTEGRATION_TEST_ERPREG_token";
    private const string UploadId = "0123456789abcdef0123456789abcdef";

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
    public async Task Upload_PersistsAReceivedRowWhoseHashMatchesTheStoredObject()
    {
        var dropPoint = await GivenDropPointAsync();

        await using var context = NewContext();
        var key = await NewUploadHandler(context).Handle(
            new UploadErpOrderFileCommand(dropPoint.Id, "orders.xml", ToStream(OrderXml), ErpImportChannel.TokenPush, TokenName),
            CancellationToken.None);

        var stored = await _storage.GetObjectInfoAsync(key);
        var row = (await LoadRowsAsync(dropPoint.Id)).ShouldHaveSingleItem();
        row.StorageKey.ShouldBe(key);
        row.FileName.ShouldBe("orders.xml");
        row.Status.ShouldBe(ErpImportFileStatus.Received);
        row.Channel.ShouldBe(ErpImportChannel.TokenPush);
        row.UploadedBy.ShouldBe(TokenName);
        row.SourceSystemId.ShouldBe(dropPoint.SourceSystemId);
        row.SizeBytes.ShouldBe(Encoding.UTF8.GetByteCount(OrderXml));
        row.Sha256.ShouldBe(stored!.Sha256);
        row.Sha256.ShouldBe(ErpImportFileHasher.ComputeSha256Hex(ToStream(OrderXml)));
        row.DuplicateOfFileId.ShouldBeNull();
    }

    [Test]
    public async Task SecondIdenticalUpload_LinksTheFirstRowAsDuplicate()
    {
        var dropPoint = await GivenDropPointAsync();

        await using (var firstContext = NewContext())
        {
            await NewUploadHandler(firstContext).Handle(
                new UploadErpOrderFileCommand(dropPoint.Id, "orders.xml", ToStream(OrderXml), ErpImportChannel.UiUpload, "admin"),
                CancellationToken.None);
        }

        await using (var secondContext = NewContext())
        {
            await NewUploadHandler(secondContext).Handle(
                new UploadErpOrderFileCommand(dropPoint.Id, "orders-again.xml", ToStream(OrderXml), ErpImportChannel.UiUpload, "admin"),
                CancellationToken.None);
        }

        var rows = await LoadRowsAsync(dropPoint.Id);
        rows.Count.ShouldBe(2);
        var first = rows.Single(r => r.FileName == "orders.xml");
        var second = rows.Single(r => r.FileName == "orders-again.xml");
        first.DuplicateOfFileId.ShouldBeNull();
        second.DuplicateOfFileId.ShouldBe(first.Id);
    }

    [Test]
    public async Task InboxRegistration_RegistersOnlyInboxFiles_AndIsIdempotent()
    {
        var dropPoint = await GivenDropPointAsync();
        var prefix = ErpImportStorageKeys.NormalizePrefix(dropPoint.BucketPrefix);
        await _storage.UploadAsync(prefix + "dropped.xml", ToStream(OrderXml));
        await _storage.UploadAsync(prefix + "processed/old.xml", ToStream(OrderXml));
        await _storage.UploadAsync(prefix + "error/broken.xml", ToStream(OrderXml));

        await using (var firstContext = NewContext())
        {
            (await NewInboxService(firstContext).RegisterInboxFilesAsync(dropPoint)).ShouldBe(1);
        }

        await using (var secondContext = NewContext())
        {
            (await NewInboxService(secondContext).RegisterInboxFilesAsync(dropPoint)).ShouldBe(0);
        }

        var row = (await LoadRowsAsync(dropPoint.Id)).ShouldHaveSingleItem();
        row.StorageKey.ShouldMatch("^" + Regex.Escape(prefix) + "[0-9a-f]{32}-dropped\\.xml$");
        _storage.Contains(row.StorageKey).ShouldBeTrue("the ERP-named file is renamed to its unique key");
        _storage.Contains(prefix + "dropped.xml").ShouldBeFalse("the ERP's own name must be free for the next delivery");
        row.FileName.ShouldBe("dropped.xml");
        row.Channel.ShouldBe(ErpImportChannel.InboxFolder);
        row.UploadedBy.ShouldBeNull();
        row.Status.ShouldBe(ErpImportFileStatus.Received);
    }

    [Test]
    public async Task InboxRegistration_AfterAnUpload_KeepsTheUploadRow()
    {
        var dropPoint = await GivenDropPointAsync();

        await using (var uploadContext = NewContext())
        {
            await NewUploadHandler(uploadContext).Handle(
                new UploadErpOrderFileCommand(dropPoint.Id, "orders.xml", ToStream(OrderXml), ErpImportChannel.TokenPush, TokenName),
                CancellationToken.None);
        }

        await using (var inboxContext = NewContext())
        {
            (await NewInboxService(inboxContext).RegisterInboxFilesAsync(dropPoint)).ShouldBe(0);
        }

        var row = (await LoadRowsAsync(dropPoint.Id)).ShouldHaveSingleItem();
        row.Channel.ShouldBe(ErpImportChannel.TokenPush);
        row.UploadedBy.ShouldBe(TokenName);
    }

    [Test]
    public async Task InboxRegistration_ConcurrentRegistrationOfTheSameKey_IsDetachedAndNotWrittenAgain()
    {
        var dropPoint = await GivenDropPointAsync();
        var prefix = ErpImportStorageKeys.NormalizePrefix(dropPoint.BucketPrefix);
        var key = prefix + UploadId + "-raced.xml";
        await _storage.UploadAsync(key, ToStream(OrderXml));

        await using (var winnerContext = NewContext())
        {
            (await NewInboxService(winnerContext).RegisterInboxFilesAsync(dropPoint)).ShouldBe(1);
        }

        await using var loserContext = NewContext();
        var realRepository = new ErpImportFileRepository(loserContext);
        var staleReadRepository = StaleReadRepository(realRepository);
        var unitOfWork = new UnitOfWork(loserContext, Substitute.For<ILogger<UnitOfWork>>());
        var registrar = new ErpImportFileRegistrar(
            staleReadRepository, unitOfWork, TimeProvider.System, NullLogger<ErpImportFileRegistrar>.Instance);
        var loser = new ErpImportInboxRegistrationService(
            _storage, staleReadRepository, registrar, unitOfWork, NullLogger<ErpImportInboxRegistrationService>.Instance);

        (await loser.RegisterInboxFilesAsync(dropPoint)).ShouldBe(0);

        loserContext.ChangeTracker.Entries<ErpImportFile>().ShouldBeEmpty();
        await unitOfWork.CompleteAsync();
        (await LoadRowsAsync(dropPoint.Id)).ShouldHaveSingleItem().StorageKey.ShouldBe(key);
    }

    private static IErpImportFileRepository StaleReadRepository(ErpImportFileRepository realRepository)
    {
        var repository = Substitute.For<IErpImportFileRepository>();
        repository.GetKeyStatesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportFileKeyState>());
        repository.FindDuplicateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => realRepository.FindDuplicateAsync(call.ArgAt<Guid>(0), call.ArgAt<string>(1), call.ArgAt<Guid?>(2)));
        repository.When(r => r.Add(Arg.Any<ErpImportFile>())).Do(call => realRepository.Add(call.Arg<ErpImportFile>()));
        repository.When(r => r.Detach(Arg.Any<ErpImportFile>())).Do(call => realRepository.Detach(call.Arg<ErpImportFile>()));
        return repository;
    }

    private UploadErpOrderFileCommandHandler NewUploadHandler(DataBaseContext context)
    {
        return new UploadErpOrderFileCommandHandler(
            new ErpDropPointRepository(context, Substitute.For<ILogger<ErpDropPoint>>()),
            _storage,
            NewRegistrar(context));
    }

    private ErpImportInboxRegistrationService NewInboxService(DataBaseContext context)
    {
        return new ErpImportInboxRegistrationService(
            _storage,
            new ErpImportFileRepository(context),
            NewRegistrar(context),
            new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>()),
            NullLogger<ErpImportInboxRegistrationService>.Instance);
    }

    private static ErpImportFileRegistrar NewRegistrar(DataBaseContext context)
    {
        return new ErpImportFileRegistrar(
            new ErpImportFileRepository(context),
            new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>()),
            TimeProvider.System,
            NullLogger<ErpImportFileRegistrar>.Instance);
    }

    private static MemoryStream ToStream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static async Task<ErpDropPoint> GivenDropPointAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var dropPoint = new ErpDropPoint
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "drop point",
            SourceSystemId = TestPrefix + suffix,
            BucketPrefix = $"{TestPrefix}{suffix}/orders",
            IsEnabled = true,
        };

        await using var context = NewContext();
        context.ErpDropPoints.Add(dropPoint);
        await context.SaveChangesAsync();
        return dropPoint;
    }

    private static async Task<List<ErpImportFile>> LoadRowsAsync(Guid dropPointId)
    {
        await using var context = NewContext();
        return await context.ErpImportFiles.AsNoTracking().Where(f => f.DropPointId == dropPointId).ToListAsync();
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
            "DELETE FROM erp_import_file WHERE drop_point_id IN (SELECT id FROM erp_drop_points WHERE source_system_id LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM erp_drop_points WHERE source_system_id LIKE {0}",
            TestPrefix + "%");
    }
}
