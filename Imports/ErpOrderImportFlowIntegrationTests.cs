// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end tests of the ledger-based ERP order import against the real PostgreSQL database: the real
/// parser, runner, importer, order processor, repositories and erp_import_file ledger (conditional claim,
/// heartbeat and finish updates on Postgres), with only the object storage replaced by
/// InMemoryObjectStorageService. Files are dropped into the inbox under the ERP's own name, renamed to a
/// unique key by the registration and never moved afterwards. Each test checks the imported clients and
/// shifts plus the ledger row (status, counters, exception linking). Also covered: an order the database
/// refuses (rolled-back entities must not poison the shared context for later orders and files of the run)
/// and the migration of the pre-ledger processing/processed/error sub-folders (incl. an upload's existing row).
/// Cleanup deletes ONLY rows this fixture created: everything carries the INTEGRATION_TEST_ERP_ prefix
/// (drop points by source system, ledger rows by drop point, shifts and clients by source system).
/// </summary>
using System.Globalization;
using System.Text;
using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Common;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Domain.Services.Imports;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Imports;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Settings;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Imports;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Shifts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;
using Klacks.Api.Infrastructure.Services.Clients;

namespace Klacks.IntegrationTest.Imports;

[TestFixture]
[Category("RealDatabase")]
public class ErpOrderImportFlowIntegrationTests
{
    private const string TestDataPrefix = "INTEGRATION_TEST_ERP_";
    private const string DropPointSourceSystemId = "INTEGRATION_TEST_ERP_SOURCE";
    private const string ForeignInstanceId = "INTEGRATION_TEST_ERP_HOST:foreign";
    private const string InboxFileName = "orders.xml";

    private const string FixtureFileName = "sample-erp-order-import.xml";
    private const string FixtureSourceSystemIdAttribute = "sourceSystemId=\"erp-example\"";
    private const string FixtureFirstCompany = "Spitex Musterhausen";
    private const string FixtureSecondCompany = "Alterszentrum Sonnenblick";
    private const string FirstOrderReference = "ORD-2026-0001";
    private const string SecondOrderReference = "ORD-2026-0002";
    private const string FirstCustomerReference = "CUST-1001";
    private const string OriginalEndTimeElement = "<EndTime>15:00</EndTime>";
    private const string ChangedEndTimeElement = "<EndTime>16:00</EndTime>";
    private const string SecondOrderWeekdaysElement = "<Weekdays saturday=\"true\" sunday=\"true\" />";
    private const string MalformedXmlReasonFragment = "Malformed XML";
    private const int OverlongCompanyPadding = 120;

    private const string ValidCronExpression = "0 * * * *";
    private const string CronTimeZoneId = "Europe/Zurich";

    private static readonly string[] ScheduleSettingTypes =
    [
        ErpImportSettingsTypes.CronExpression,
        ErpImportSettingsTypes.CronTimeZoneId,
        ErpImportSettingsTypes.NextRunUtc
    ];

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private InMemoryObjectStorageService _objectStorage = null!;
    private string _storagePrefix = null!;
    private Guid _dropPointId;
    private string _fixtureSourceSystemId = null!;
    private string _firstCompany = null!;
    private string _secondCompany = null!;
    private ICompanyClock _companyClock = null!;
    private readonly Dictionary<string, string?> _originalScheduleSettings = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        using var context = CreateContext();

        foreach (var type in ScheduleSettingTypes)
        {
            var existing = await context.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Type == type);
            _originalScheduleSettings[type] = existing?.Value;
        }

        await CleanupTestDataAsync(context);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        using var context = CreateContext();

        foreach (var (type, originalValue) in _originalScheduleSettings)
        {
            var row = await context.Settings.FirstOrDefaultAsync(s => s.Type == type);
            if (originalValue == null)
            {
                if (row != null)
                {
                    context.Settings.Remove(row);
                }
            }
            else if (row == null)
            {
                context.Settings.Add(new SettingsEntity { Id = Guid.NewGuid(), Type = type, Value = originalValue });
            }
            else
            {
                row.Value = originalValue;
            }
        }

        await context.SaveChangesAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = CreateContext();
        _objectStorage = new InMemoryObjectStorageService();

        // All keys the import writes to the shared database are uniquified with the
        // integration-test prefix so the cleanup can never touch production rows.
        var runId = $"{Guid.NewGuid():N}";
        _fixtureSourceSystemId = $"{TestDataPrefix}SRC_{runId}";
        _firstCompany = $"{TestDataPrefix}{FixtureFirstCompany} {runId}";
        _secondCompany = $"{TestDataPrefix}{FixtureSecondCompany} {runId}";

        var bucketPrefix = $"{TestDataPrefix}{Guid.NewGuid():N}";
        _storagePrefix = $"{bucketPrefix}/";

        _dropPointId = Guid.NewGuid();
        _context.ErpDropPoints.Add(new ErpDropPoint
        {
            Id = _dropPointId,
            Name = $"{TestDataPrefix}DROP_POINT",
            SourceSystemId = DropPointSourceSystemId,
            BucketPrefix = bucketPrefix,
            IsEnabled = true
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataAsync(_context);
        _context.Dispose();
    }

    [Test]
    public async Task RunAsync_SampleFixtureInDropPoint_ImportsCustomersAndOrdersAndCompletesTheLedgerRow()
    {
        var fileName = UniqueFileName();
        await UploadAsync(fileName, ReadFixtureXml());

        await RunImportAsync();

        var clients = await LoadImportedClientsAsync();
        clients.Count.ShouldBe(2, "the sample file carries two distinct customers");

        var firstCustomer = clients.Single(c => c.Company == _firstCompany);
        firstCustomer.Type.ShouldBe(EntityTypeEnum.Customer);
        firstCustomer.LegalEntity.ShouldBeTrue();
        firstCustomer.SourceSystemId.ShouldBe(_fixtureSourceSystemId);
        firstCustomer.ExternalCustomerReference.ShouldBe(FirstCustomerReference);
        var firstAddress = firstCustomer.Addresses.ShouldHaveSingleItem();
        firstAddress.Type.ShouldBe(AddressTypeEnum.Workplace);
        firstAddress.Street.ShouldBe("Hauptstrasse 5");
        firstAddress.Zip.ShouldBe("4000");
        firstAddress.City.ShouldBe("Basel");
        firstAddress.State.ShouldBe("BS");
        firstAddress.Country.ShouldBe("CH");

        var secondCustomer = clients.Single(c => c.Company == _secondCompany);
        secondCustomer.Type.ShouldBe(EntityTypeEnum.Customer);
        secondCustomer.SourceSystemId.ShouldBe(_fixtureSourceSystemId);
        secondCustomer.ExternalCustomerReference.ShouldBeNull("the minimal sample order has no customer reference");
        var secondAddress = secondCustomer.Addresses.ShouldHaveSingleItem();
        secondAddress.Street.ShouldBe("Bahnhofweg 12");
        secondAddress.Zip.ShouldBe("8400");
        secondAddress.City.ShouldBe("Winterthur");
        secondAddress.State.ShouldBe("ZH");
        secondAddress.Country.ShouldBe("CH");

        var shifts = await LoadImportedShiftsAsync();
        shifts.Count.ShouldBe(2, "the sample file carries two orders");

        var firstOrder = shifts.Single(s => s.ExternalOrderReference == FirstOrderReference);
        firstOrder.Status.ShouldBe(ShiftStatus.OriginalOrder);
        firstOrder.ClientId.ShouldBe(firstCustomer.Id);
        firstOrder.SourceSystemId.ShouldBe(_fixtureSourceSystemId);
        firstOrder.FromDate.ShouldBe(new DateOnly(2026, 8, 1));
        firstOrder.UntilDate.ShouldBe(new DateOnly(2026, 12, 31));
        firstOrder.StartShift.ShouldBe(new TimeOnly(8, 0));
        firstOrder.EndShift.ShouldBe(new TimeOnly(15, 0));
        firstOrder.IsTimeRange.ShouldBeTrue();
        firstOrder.WorkTime.ShouldBe(0.3333m, "the explicit duration of 20 minutes maps to 0.3333 decimal hours");
        firstOrder.Description.ShouldBe("Morning care visit, ground floor, ring twice");
        firstOrder.IsMonday.ShouldBeTrue();
        firstOrder.IsTuesday.ShouldBeTrue();
        firstOrder.IsWednesday.ShouldBeTrue();
        firstOrder.IsThursday.ShouldBeTrue();
        firstOrder.IsFriday.ShouldBeTrue();
        firstOrder.IsSaturday.ShouldBeFalse();
        firstOrder.IsSunday.ShouldBeFalse();
        firstOrder.Quantity.ShouldBe(1);
        firstOrder.SumEmployees.ShouldBe(2);

        var defaultMacroId = await new DefaultShiftMacroResolver(
            CreateSettingsRepository(_context), Substitute.For<ILogger<DefaultShiftMacroResolver>>()).ResolveDefaultMacroIdAsync();
        defaultMacroId.ShouldNotBeNull("the seeded AllShift macros give every installation a default Shift macro");
        firstOrder.MacroId.ShouldBe(defaultMacroId, "a newly imported draft gets the default Shift macro");

        var secondOrder = shifts.Single(s => s.ExternalOrderReference == SecondOrderReference);
        secondOrder.Status.ShouldBe(ShiftStatus.OriginalOrder);
        secondOrder.ClientId.ShouldBe(secondCustomer.Id);
        secondOrder.SourceSystemId.ShouldBe(_fixtureSourceSystemId);
        secondOrder.FromDate.ShouldBe(new DateOnly(2026, 9, 15));
        secondOrder.UntilDate.ShouldBeNull("the minimal sample order is open-ended");
        secondOrder.StartShift.ShouldBe(new TimeOnly(18, 0));
        secondOrder.EndShift.ShouldBe(new TimeOnly(22, 0));
        secondOrder.IsTimeRange.ShouldBeFalse();
        secondOrder.WorkTime.ShouldBe(4m, "a fixed shift derives its duration in decimal hours from start and end time");
        secondOrder.IsMonday.ShouldBeFalse();
        secondOrder.IsSaturday.ShouldBeTrue();
        secondOrder.IsSunday.ShouldBeTrue();
        secondOrder.Quantity.ShouldBe(1);
        secondOrder.SumEmployees.ShouldBe(1);
        secondOrder.MacroId.ShouldBe(defaultMacroId);

        var row = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        row.Status.ShouldBe(ErpImportFileStatus.Completed);
        row.Channel.ShouldBe(ErpImportChannel.InboxFolder);
        row.FileName.ShouldBe(fileName);
        row.Attempt.ShouldBe(1);
        row.ClaimedBy.ShouldNotBeNull();
        row.FinishedAt.ShouldNotBeNull();
        row.FailureReason.ShouldBeNull();
        AssertCounts(row, total: 2, created: 2);
        row.StorageKey.ShouldNotBe(PendingKey(fileName), "the ERP-named file is registered under a unique key");
        _objectStorage.Contains(row.StorageKey).ShouldBeTrue("the raw file stays in the inbox for audit and retry");
        (await _objectStorage.ListAsync(_storagePrefix)).ShouldBe(new[] { row.StorageKey }, "nothing is moved into sub-folders any more");
    }

    [Test]
    public async Task RunAsync_ReimportOfSameFile_UpdatesDraftsInsteadOfDuplicating()
    {
        await UploadAsync(UniqueFileName(), ReadFixtureXml());
        await RunImportAsync();

        var draftsAfterFirstRun = await LoadImportedShiftsAsync();
        draftsAfterFirstRun.Count.ShouldBe(2);
        var firstDraftId = draftsAfterFirstRun.Single(s => s.ExternalOrderReference == FirstOrderReference).Id;
        var secondDraftId = draftsAfterFirstRun.Single(s => s.ExternalOrderReference == SecondOrderReference).Id;

        const int driftedSumEmployees = 99;
        var plannerMacroId = SeededMacroIds.AllShiftAdditive;
        await _context.Database.ExecuteSqlAsync(
            $"UPDATE shift SET sum_employees = {driftedSumEmployees}, macro_id = {plannerMacroId} WHERE id = {firstDraftId}");

        await UploadAsync(UniqueFileName(), ReadFixtureXml());
        await RunImportAsync();

        var clients = await LoadImportedClientsAsync();
        clients.Count.ShouldBe(2, "re-importing the same file must not create duplicate customers");

        var shifts = await LoadImportedShiftsAsync();
        shifts.Count.ShouldBe(2, "re-importing the same file must not create duplicate orders");

        var firstDraft = shifts.Single(s => s.ExternalOrderReference == FirstOrderReference);
        firstDraft.Id.ShouldBe(firstDraftId, "the existing draft must be updated in place");
        firstDraft.Status.ShouldBe(ShiftStatus.OriginalOrder);
        firstDraft.SumEmployees.ShouldBe(2, "the re-import must overwrite drifted draft data with the payload");
        firstDraft.MacroId.ShouldBe(plannerMacroId, "a re-delivery overwrites order data but never the macro the planner chose");

        shifts.Single(s => s.ExternalOrderReference == SecondOrderReference).Id.ShouldBe(secondDraftId);

        var rows = await LoadLedgerRowsAsync();
        rows.Count.ShouldBe(2);
        var firstRow = rows.MinBy(r => r.ReceivedAt)!;
        var secondRow = rows.Single(r => r.Id != firstRow.Id);
        AssertCounts(firstRow, total: 2, created: 2);
        AssertCounts(secondRow, total: 2, updated: 1, unchanged: 1);
        secondRow.Status.ShouldBe(ErpImportFileStatus.Completed);
        secondRow.DuplicateOfFileId.ShouldBe(firstRow.Id, "an identical re-delivery is linked as duplicate but still imported");
    }

    [Test]
    public async Task RunAsync_ChangedRedeliveryOfSealedOrder_SupersedesItAndUnchangedRedeliveryIsNoOp()
    {
        await UploadAsync(UniqueFileName(), ReadFixtureXml());
        await RunImportAsync();

        var sealedOrderId = (await LoadImportedShiftsAsync())
            .Single(s => s.ExternalOrderReference == FirstOrderReference).Id;
        await _context.Database.ExecuteSqlAsync(
            $"UPDATE shift SET status = {(int)ShiftStatus.SealedOrder} WHERE id = {sealedOrderId}");

        await UploadAsync(UniqueFileName(), ReadFixtureXml());
        await RunImportAsync();

        var afterUnchangedRedelivery = (await LoadImportedShiftsAsync())
            .Where(s => s.ExternalOrderReference == FirstOrderReference)
            .ToList();
        var untouched = afterUnchangedRedelivery.ShouldHaveSingleItem(
            "an unchanged redelivery of a sealed order must be a no-op");
        untouched.Id.ShouldBe(sealedOrderId);
        untouched.Status.ShouldBe(ShiftStatus.SealedOrder);
        untouched.UntilDate.ShouldBe(new DateOnly(2026, 12, 31), "a no-op must not close the sealed order");
        untouched.SupersedesOrderId.ShouldBeNull();

        await UploadAsync(UniqueFileName(), ReadFixtureXml().Replace(OriginalEndTimeElement, ChangedEndTimeElement));
        await RunImportAsync();

        var versions = (await LoadImportedShiftsAsync())
            .Where(s => s.ExternalOrderReference == FirstOrderReference)
            .ToList();
        versions.Count.ShouldBe(2, "a changed redelivery must close the old order and open a replacement draft");

        var closedOrder = versions.Single(s => s.Id == sealedOrderId);
        closedOrder.Status.ShouldBe(ShiftStatus.SealedOrder);
        var companyToday = await _companyClock.GetTodayDateAsync();
        closedOrder.UntilDate.ShouldBe(companyToday, "the superseded order must be closed as of the company's today");

        var replacementDraft = versions.Single(s => s.Id != sealedOrderId);
        replacementDraft.Status.ShouldBe(ShiftStatus.OriginalOrder);
        replacementDraft.SupersedesOrderId.ShouldBe(sealedOrderId);
        replacementDraft.EndShift.ShouldBe(new TimeOnly(16, 0));
        replacementDraft.UntilDate.ShouldBe(new DateOnly(2026, 12, 31));

        var rows = (await LoadLedgerRowsAsync()).OrderBy(r => r.ReceivedAt).ToList();
        rows.Count.ShouldBe(3);
        AssertCounts(rows[1], total: 2, unchanged: 2);
        AssertCounts(rows[2], total: 2, unchanged: 1, superseded: 1);
    }

    [Test]
    public async Task RunAsync_OrderWithoutWeekdays_IsRejectedWhileTheRestOfTheBatchImports()
    {
        var fileName = UniqueFileName();
        await UploadAsync(fileName, ReadFixtureXml().Replace(SecondOrderWeekdaysElement, string.Empty));

        await RunImportAsync();

        var shifts = await LoadImportedShiftsAsync();
        var imported = shifts.ShouldHaveSingleItem("the incomplete second order must be rejected");
        imported.ExternalOrderReference.ShouldBe(FirstOrderReference);

        var clients = await LoadImportedClientsAsync();
        clients.ShouldHaveSingleItem().Company.ShouldBe(_firstCompany, "the rejected order must not create its customer");

        var row = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        row.Status.ShouldBe(ErpImportFileStatus.CompletedWithWarnings, "a partly rejected file is processed with warnings");
        row.FailureReason.ShouldBeNull();
        AssertCounts(row, total: 2, created: 1, rejected: 1);
        _objectStorage.Contains(row.StorageKey).ShouldBeTrue();

        var recordedExceptions = await _context.Set<ErpImportException>()
            .AsNoTracking()
            .Where(e => e.SourceSystemId == DropPointSourceSystemId)
            .ToListAsync();
        var recorded = recordedExceptions.ShouldHaveSingleItem();
        recorded.FileKey.ShouldBe(row.StorageKey);
        recorded.ImportFileId.ShouldBe(row.Id);
        recorded.ExternalOrderReference.ShouldBe(SecondOrderReference);
        recorded.Reason.ShouldContain("weekday");
    }

    [Test]
    public async Task RunAsync_OrderViolatingADatabaseConstraint_IsRejectedAndTheRestOfTheRunImportsNormally()
    {
        var overlongCompany = _firstCompany + new string('X', OverlongCompanyPadding);
        var failingFile = await GivenLedgerRowAsync(
            ErpImportFileStatus.Received,
            ReadFixtureXml().Replace(_firstCompany, overlongCompany),
            configure: f => f.ReceivedAt = DateTime.UtcNow.AddMinutes(-10));
        var laterFile = await GivenLedgerRowAsync(
            ErpImportFileStatus.Received,
            ReadFixtureXml(),
            configure: f => f.ReceivedAt = DateTime.UtcNow.AddMinutes(-5));

        await RunImportAsync();

        var rows = await LoadLedgerRowsAsync();
        var failingRow = rows.Single(r => r.Id == failingFile.Id);
        failingRow.Status.ShouldBe(
            ErpImportFileStatus.CompletedWithWarnings,
            "an order the database refuses is rejected; it must not abort the file");
        failingRow.FailureReason.ShouldBeNull();
        AssertCounts(failingRow, total: 2, created: 1, rejected: 1);

        var laterRow = rows.Single(r => r.Id == laterFile.Id);
        laterRow.Status.ShouldBe(
            ErpImportFileStatus.Completed,
            "the rolled-back order must not poison the shared context for the next file of the run");
        AssertCounts(laterRow, total: 2, created: 1, unchanged: 1);

        var recordedExceptions = await _context.Set<ErpImportException>()
            .AsNoTracking()
            .Where(e => e.SourceSystemId == DropPointSourceSystemId)
            .ToListAsync();
        var recorded = recordedExceptions.ShouldHaveSingleItem();
        recorded.ImportFileId.ShouldBe(failingRow.Id);
        recorded.ExternalOrderReference.ShouldBe(FirstOrderReference);
        recorded.Reason.ShouldNotBeNullOrWhiteSpace();
        recorded.ResolvedAt.ShouldBeNull();

        var shifts = await LoadImportedShiftsAsync();
        shifts.Count.ShouldBe(2, "both orders exist after the run: the second order from the first file, the first order from the later file");
        shifts.Select(s => s.ExternalOrderReference).ShouldBe(new[] { FirstOrderReference, SecondOrderReference }, ignoreOrder: true);

        var clients = await LoadImportedClientsAsync();
        clients.ShouldNotContain(c => c.Company == overlongCompany, "the refused customer must not be persisted by a later commit");
        clients.Select(c => c.Company).ShouldBe(new[] { _firstCompany, _secondCompany }, ignoreOrder: true);
    }

    [Test]
    public async Task RunAsync_LegacySubFolders_AreMigratedIntoTheLedgerAndBackIntoTheInboxOnce()
    {
        const string uploadId = "0123456789abcdef0123456789abcdef";
        var processedKey = $"{_storagePrefix}{ErpImportLegacyFolders.Processed}/{uploadId}-done.xml";
        var errorKey = $"{_storagePrefix}{ErpImportLegacyFolders.Error}/{InboxFileName}";
        var processingKey = $"{_storagePrefix}{ErpImportLegacyFolders.Processing}/fedcba9876543210fedcba9876543210-stuck.xml";
        await UploadToKeyAsync(processedKey, ReadFixtureXml());
        await UploadToKeyAsync(errorKey, ReadFixtureXml()[..100]);
        await UploadToKeyAsync(processingKey, ReadFixtureXml().Replace(OriginalEndTimeElement, ChangedEndTimeElement));

        var olderError = new ErpImportException
        {
            Id = Guid.NewGuid(),
            SourceSystemId = DropPointSourceSystemId,
            FileKey = PendingKey(InboxFileName),
            Reason = "older reason"
        };
        _context.Set<ErpImportException>().Add(olderError);
        await _context.SaveChangesAsync();
        var latestError = new ErpImportException
        {
            Id = Guid.NewGuid(),
            SourceSystemId = DropPointSourceSystemId,
            FileKey = PendingKey(InboxFileName),
            Reason = "Malformed XML: unexpected end of file"
        };
        _context.Set<ErpImportException>().Add(latestError);
        await _context.SaveChangesAsync();

        await RunImportAsync();

        var rows = await LoadLedgerRowsAsync();
        rows.Count.ShouldBe(3);

        var completed = rows.Single(r => r.FileName == "done.xml");
        completed.Status.ShouldBe(ErpImportFileStatus.Completed);
        completed.StorageKey.ShouldBe($"{_storagePrefix}{uploadId}-done.xml", "an upload goes back to its original inbox key");
        completed.TotalOrders.ShouldBeNull("the counts of a pre-ledger import are unknown");
        completed.Channel.ShouldBe(ErpImportChannel.InboxFolder);
        completed.FinishedAt.ShouldNotBeNull();

        var failed = rows.Single(r => r.FileName == InboxFileName);
        failed.Status.ShouldBe(ErpImportFileStatus.Failed);
        failed.FailureReason.ShouldBe(latestError.Reason);
        ErpImportStorageKeys.HasUploadIdPrefix(failed.StorageKey[_storagePrefix.Length..]).ShouldBeTrue("an ERP-named file gets a unique inbox key");

        var interrupted = rows.Single(r => r.FileName == "stuck.xml");
        interrupted.Status.ShouldBe(ErpImportFileStatus.Interrupted);
        interrupted.FailureReason.ShouldBe(ErpImportFileFailureReasons.LegacyImportInterrupted);

        var inboxKeys = await _objectStorage.ListAsync(_storagePrefix);
        inboxKeys.ShouldBe(rows.Select(r => r.StorageKey), ignoreOrder: true, "every legacy object now lives at its row's inbox key");

        var linked = await _context.Set<ErpImportException>()
            .AsNoTracking()
            .Where(e => e.SourceSystemId == DropPointSourceSystemId)
            .ToListAsync();
        linked.ShouldAllBe(e => e.ImportFileId == failed.Id);

        (await LoadImportedShiftsAsync()).ShouldBeEmpty("migrated files are history, they are not imported again");

        await RunImportAsync();

        var afterRerun = await LoadLedgerRowsAsync();
        afterRerun.Select(r => (r.Id, r.Status, r.StorageKey)).ShouldBe(rows.Select(r => (r.Id, r.Status, r.StorageKey)), ignoreOrder: true,
            "a second run finds no legacy folder content and changes nothing");
    }

    [Test]
    public async Task RunAsync_LegacyFileOfARowRegisteredBeforeTheOldImportMovedIt_UpdatesThatRow()
    {
        var row = await GivenLedgerRowAsync(ErpImportFileStatus.Received, ReadFixtureXml());
        var storedName = row.StorageKey[_storagePrefix.Length..];
        var legacyKey = $"{_storagePrefix}{ErpImportLegacyFolders.Processed}/{storedName}";
        (await _objectStorage.TryMoveAsync(row.StorageKey, legacyKey)).ShouldBeTrue();

        await RunImportAsync();

        var stored = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem("the upload's row is updated, no second row is inserted");
        stored.Id.ShouldBe(row.Id);
        stored.Status.ShouldBe(ErpImportFileStatus.Completed);
        stored.StorageKey.ShouldBe(row.StorageKey);
        stored.Attempt.ShouldBe(0, "the old import processed it; the ledger never claimed it");
        _objectStorage.Contains(row.StorageKey).ShouldBeTrue();
        _objectStorage.Contains(legacyKey).ShouldBeFalse();
        (await LoadImportedShiftsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task RunAsync_TruncatedXml_FailsTheFileAndRecordsAnUnreferencedImportException()
    {
        var fileName = UniqueFileName();
        var fixtureXml = ReadFixtureXml();
        await UploadAsync(fileName, fixtureXml[..(fixtureXml.Length / 2)]);

        await RunImportAsync();

        var row = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        row.Status.ShouldBe(ErpImportFileStatus.Failed);
        row.FailureReason.ShouldNotBeNull().ShouldContain(MalformedXmlReasonFragment);
        row.TotalOrders.ShouldBeNull();

        (await LoadImportedShiftsAsync()).ShouldBeEmpty("an unparsable file must not create any order");
        (await LoadImportedClientsAsync()).ShouldBeEmpty("an unparsable file must not create any customer");

        var recordedExceptions = await _context.Set<ErpImportException>()
            .AsNoTracking()
            .Where(e => e.SourceSystemId == DropPointSourceSystemId)
            .ToListAsync();
        var recorded = recordedExceptions.ShouldHaveSingleItem();
        recorded.FileKey.ShouldBe(row.StorageKey);
        recorded.ImportFileId.ShouldBe(row.Id);
        recorded.ExternalOrderReference.ShouldBeNull();
        recorded.Reason.ShouldContain(MalformedXmlReasonFragment);
        recorded.ResolvedAt.ShouldBeNull();
    }

    [Test]
    public async Task RunAsync_SameFileNameRedeliveredAfterTheImport_IsImportedAgain()
    {
        await UploadAsync(InboxFileName, ReadFixtureXml());
        await RunImportAsync();

        await UploadAsync(InboxFileName, ReadFixtureXml().Replace(OriginalEndTimeElement, ChangedEndTimeElement));
        await RunImportAsync();

        var rows = (await LoadLedgerRowsAsync()).OrderBy(r => r.ReceivedAt).ToList();
        rows.Count.ShouldBe(2, "an ERP re-using its file name every night must not be ignored");
        rows.ShouldAllBe(r => r.FileName == InboxFileName && r.Status == ErpImportFileStatus.Completed);
        rows[0].StorageKey.ShouldNotBe(rows[1].StorageKey);
        AssertCounts(rows[1], total: 2, updated: 1, unchanged: 1);

        (await LoadImportedShiftsAsync()).Single(s => s.ExternalOrderReference == FirstOrderReference)
            .EndShift.ShouldBe(new TimeOnly(16, 0), "the second delivery's change must reach the draft");
        _objectStorage.Contains(PendingKey(InboxFileName)).ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_FileImportingOnAnotherInstance_IsLeftAlone()
    {
        var row = await GivenLedgerRowAsync(ErpImportFileStatus.Importing, ReadFixtureXml(), configure: f =>
        {
            f.Attempt = 1;
            f.ClaimedBy = ForeignInstanceId;
            f.StartedAt = DateTime.UtcNow.AddMinutes(-1);
            f.HeartbeatAt = DateTime.UtcNow;
        });

        await RunImportAsync();

        var stored = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        stored.Status.ShouldBe(ErpImportFileStatus.Importing);
        stored.ClaimedBy.ShouldBe(ForeignInstanceId);
        stored.Attempt.ShouldBe(row.Attempt);
        (await LoadImportedShiftsAsync()).ShouldBeEmpty("a file another instance is importing must not be imported twice");
        (await LoadImportedClientsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task RunAsync_ImportWhoseHeartbeatStopped_IsReapedToInterruptedAndNotImported()
    {
        await GivenLedgerRowAsync(ErpImportFileStatus.Importing, ReadFixtureXml(), configure: f =>
        {
            f.Attempt = 1;
            f.ClaimedBy = ForeignInstanceId;
            f.StartedAt = DateTime.UtcNow.AddMinutes(-30);
            f.HeartbeatAt = DateTime.UtcNow - ErpImportFileTiming.StaleAfter - TimeSpan.FromMinutes(1);
        });

        await RunImportAsync();

        var stored = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        stored.Status.ShouldBe(ErpImportFileStatus.Interrupted);
        stored.FailureReason.ShouldBe(ErpImportFileFailureReasons.StaleHeartbeat);
        (await LoadImportedShiftsAsync()).ShouldBeEmpty("an interrupted file waits for an explicit retry");
    }

    [Test]
    public async Task RunAsync_StoredFileChangedAfterReceipt_FailsWithoutImporting()
    {
        var row = await GivenLedgerRowAsync(ErpImportFileStatus.Received, ReadFixtureXml());
        await using (var changed = new MemoryStream(Encoding.UTF8.GetBytes(ReadFixtureXml().Replace(OriginalEndTimeElement, ChangedEndTimeElement))))
        {
            await _objectStorage.UploadAsync(row.StorageKey, changed);
        }

        await RunImportAsync();

        var stored = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        stored.Status.ShouldBe(ErpImportFileStatus.Failed);
        stored.FailureReason.ShouldBe(ErpImportFileFailureReasons.FileChangedAfterReceipt);
        stored.Attempt.ShouldBe(1);
        (await LoadImportedShiftsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task RetryAndDelete_OfAFailedFile_WorkAgainstTheRealLedger()
    {
        var fixtureXml = ReadFixtureXml();
        await UploadAsync(UniqueFileName(), fixtureXml[..(fixtureXml.Length / 2)]);
        await RunImportAsync();
        var failed = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        failed.Status.ShouldBe(ErpImportFileStatus.Failed);

        var mediator = Substitute.For<Klacks.Api.Infrastructure.Mediator.IMediator>();
        using (var retryContext = CreateContext())
        {
            var retry = new RetryErpOrderFileCommandHandler(
                new ErpImportFileRepository(retryContext),
                new ErpImportExceptionRepository(retryContext, Substitute.For<ILogger<ErpImportException>>()),
                _objectStorage,
                new UnitOfWork(retryContext, Substitute.For<ILogger<UnitOfWork>>()),
                mediator,
                TimeProvider.System);

            var result = await retry.Handle(new RetryErpOrderFileCommand(failed.Id), CancellationToken.None);

            result.Status.ShouldBe(ErpImportFileStatus.Received);
        }

        var reset = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        reset.Status.ShouldBe(ErpImportFileStatus.Received);
        reset.FailureReason.ShouldBeNull();
        (await LoadExceptionsAsync()).ShouldAllBe(e => e.ResolvedAt != null, "a retry resolves the file's open exceptions");
        await mediator.Received(1).Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());

        await RunImportAsync();
        var failedAgain = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        failedAgain.Status.ShouldBe(ErpImportFileStatus.Failed);
        failedAgain.Attempt.ShouldBe(2);

        using (var deleteContext = CreateContext())
        {
            var delete = new DeleteErpOrderFileCommandHandler(
                new ErpImportFileRepository(deleteContext),
                new ErpImportExceptionRepository(deleteContext, Substitute.For<ILogger<ErpImportException>>()),
                _objectStorage,
                new UnitOfWork(deleteContext, Substitute.For<ILogger<UnitOfWork>>()),
                TimeProvider.System,
                Substitute.For<ILogger<DeleteErpOrderFileCommandHandler>>());

            await delete.Handle(new DeleteErpOrderFileCommand(failed.Id), CancellationToken.None);
        }

        (await LoadLedgerRowsAsync()).ShouldBeEmpty("a deleted row is hidden by the soft-delete filter");
        _objectStorage.Contains(failed.StorageKey).ShouldBeFalse();
        (await LoadExceptionsAsync()).ShouldAllBe(e => e.ResolvedAt != null);

        await RunImportAsync();
        (await LoadLedgerRowsAsync()).ShouldBeEmpty("nothing of the deleted file is registered again");
    }

    [Test]
    public async Task RunAsync_DeliveriesInTheInbox_AreImportedOldestFirstWhateverTheirNames()
    {
        await UploadAsync("a-newer-delivery.xml", ReadFixtureXml().Replace(OriginalEndTimeElement, ChangedEndTimeElement));
        await UploadAsync("b-older-delivery.xml", ReadFixtureXml());
        _objectStorage.SetLastModified(PendingKey("a-newer-delivery.xml"), DateTime.UtcNow.AddMinutes(-1));
        _objectStorage.SetLastModified(PendingKey("b-older-delivery.xml"), DateTime.UtcNow.AddMinutes(-30));

        await RunImportAsync();

        var rows = (await LoadLedgerRowsAsync()).OrderBy(r => r.StartedAt).ToList();
        rows.Select(r => r.FileName).ShouldBe(new[] { "b-older-delivery.xml", "a-newer-delivery.xml" }, "older deliveries are imported first");
        rows[0].ReceivedAt.ShouldBeLessThan(rows[1].ReceivedAt, "ReceivedAt is the object's last write time, not the registration time");
        (await LoadImportedShiftsAsync()).Single(s => s.ExternalOrderReference == FirstOrderReference)
            .EndShift.ShouldBe(new TimeOnly(16, 0), "the newer delivery must win even though its name sorts first");
    }

    [Test]
    public async Task RunAsync_StoredFileWhoseLedgerRowWasLost_IsRegisteredFailedAndNotImportedUntilRetried()
    {
        await UploadAsync(UniqueFileName(), ReadFixtureXml());
        await RunImportAsync();
        var imported = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        imported.Status.ShouldBe(ErpImportFileStatus.Completed);
        await DeleteLedgerRowsOfThisTestAsync();

        await RunImportAsync();

        var orphan = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        orphan.StorageKey.ShouldBe(imported.StorageKey);
        orphan.Status.ShouldBe(ErpImportFileStatus.Failed);
        orphan.FailureReason.ShouldBe(ErpImportFileFailureReasons.LedgerEntryMissing);
        orphan.Sha256.ShouldBe(imported.Sha256);
        orphan.Attempt.ShouldBe(0, "a stored file without its record is never claimed automatically");
        orphan.StartedAt.ShouldBeNull();

        using (var retryContext = CreateContext())
        {
            var retry = new RetryErpOrderFileCommandHandler(
                new ErpImportFileRepository(retryContext),
                new ErpImportExceptionRepository(retryContext, Substitute.For<ILogger<ErpImportException>>()),
                _objectStorage,
                new UnitOfWork(retryContext, Substitute.For<ILogger<UnitOfWork>>()),
                Substitute.For<Klacks.Api.Infrastructure.Mediator.IMediator>(),
                TimeProvider.System);

            (await retry.Handle(new RetryErpOrderFileCommand(orphan.Id), CancellationToken.None)).Status.ShouldBe(ErpImportFileStatus.Received);
        }

        await RunImportAsync();

        var retried = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        retried.Status.ShouldBe(ErpImportFileStatus.Completed, "after an explicit retry the file is imported");
        retried.Attempt.ShouldBe(1);
    }

    [Test]
    public async Task RunAsync_LeftoverObjectOfADeletedFile_IsRemovedInsteadOfRegistered()
    {
        var fixtureXml = ReadFixtureXml();
        await UploadAsync(UniqueFileName(), fixtureXml[..(fixtureXml.Length / 2)]);
        await RunImportAsync();
        var failed = (await LoadLedgerRowsAsync()).ShouldHaveSingleItem();
        using (var deleteContext = CreateContext())
        {
            (await new ErpImportFileRepository(deleteContext).TrySoftDeleteAsync(failed.Id, DateTime.UtcNow)).ShouldBeTrue();
        }

        _objectStorage.Contains(failed.StorageKey).ShouldBeTrue("the object deletion is simulated as failed");

        await RunImportAsync();

        (await LoadLedgerRowsAsync()).ShouldBeEmpty("a deleted file is never registered again");
        _objectStorage.Contains(failed.StorageKey).ShouldBeFalse("the inbox registration removes the leftover");
    }

    private async Task DeleteLedgerRowsOfThisTestAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM erp_import_exception WHERE source_system_id = {0} AND source_system_id LIKE 'INTEGRATION_TEST_%'", DropPointSourceSystemId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM erp_import_file WHERE drop_point_id = {0} AND source_system_id LIKE 'INTEGRATION_TEST_%'", _dropPointId);
    }

    private async Task<List<ErpImportException>> LoadExceptionsAsync()
    {
        return await _context.Set<ErpImportException>()
            .AsNoTracking()
            .Where(e => e.SourceSystemId == DropPointSourceSystemId)
            .ToListAsync();
    }

    private DataBaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private async Task RunImportAsync()
    {
        await ArmImportScheduleAsync();

        using var runContext = CreateContext();
        var runner = CreateRunner(runContext);
        await runner.RunAsync(CancellationToken.None);
    }

    private async Task ArmImportScheduleAsync()
    {
        using var context = CreateContext();

        var dueNextRunUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture);
        var scheduleValues = new Dictionary<string, string>
        {
            [ErpImportSettingsTypes.CronExpression] = ValidCronExpression,
            [ErpImportSettingsTypes.CronTimeZoneId] = CronTimeZoneId,
            [ErpImportSettingsTypes.NextRunUtc] = dueNextRunUtc
        };

        foreach (var (type, value) in scheduleValues)
        {
            var row = await context.Settings.FirstOrDefaultAsync(s => s.Type == type);
            if (row == null)
            {
                context.Settings.Add(new SettingsEntity { Id = Guid.NewGuid(), Type = type, Value = value });
            }
            else
            {
                row.Value = value;
            }
        }

        await context.SaveChangesAsync();
    }

    private ErpOrderImportRunner CreateRunner(DataBaseContext context)
    {
        var unitOfWork = new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>());
        var collectionUpdateService = new EntityCollectionUpdateService(context);

        var clientValidator = new ClientValidator();
        var clientRepository = new ClientRepository(
            context,
            new MacroEngine(),
            Substitute.For<IClientChangeTrackingService>(),
            new ClientEntityManagementService(clientValidator),
            collectionUpdateService,
            clientValidator,
            Substitute.For<ILogger<ClientRepository>>());

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
            collectionUpdateService,
            new ShiftValidator(),
            new ScheduleMapper(),
            Klacks.IntegrationTest.TestHelpers.TestCompanyClock.Utc());

        var workRepository = new WorkRepository(
            context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Macros.IWorkMacroService>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Associations.IClientContractDataProvider>());

        var settingsRepository = CreateSettingsRepository(context);
        var defaultShiftMacroResolver = new DefaultShiftMacroResolver(
            settingsRepository, Substitute.For<ILogger<DefaultShiftMacroResolver>>());

        var triggerService = Substitute.For<IAgentTriggerService>();

        var companyClock = new CompanyClock(
            settingsRepository, TimeProvider.System, new Klacks.Api.Infrastructure.Services.Settings.SettingsChangeVersion());
        _companyClock = companyClock;

        var supersessionService = new OrderSupersessionService(
            shiftRepository,
            workRepository,
            clientRepository,
            triggerService,
            new ShiftGroupScopeReadRepository(context),
            unitOfWork,
            companyClock,
            defaultShiftMacroResolver,
            Substitute.For<ILogger<OrderSupersessionService>>());

        var dropPointRepository = new ErpDropPointRepository(context, Substitute.For<ILogger<ErpDropPoint>>());
        var defaultDropPointProvider = new ErpDefaultDropPointProvider(dropPointRepository, unitOfWork);

        var fileRepository = new ErpImportFileRepository(context);
        var exceptionRepository = new ErpImportExceptionRepository(context, Substitute.For<ILogger<ErpImportException>>());
        var registrar = new ErpImportFileRegistrar(
            fileRepository, unitOfWork, TimeProvider.System, Substitute.For<ILogger<ErpImportFileRegistrar>>());
        var inboxRegistration = new ErpImportInboxRegistrationService(
            _objectStorage, fileRepository, registrar, unitOfWork, Substitute.For<ILogger<ErpImportInboxRegistrationService>>());
        var orderProcessor = new ImportedOrderProcessor(
            new ErpCustomerResolver(clientRepository), shiftRepository, supersessionService, unitOfWork, defaultShiftMacroResolver);
        var importer = new ErpImportFileImporter(
            fileRepository,
            _objectStorage,
            new XmlOrderImportParser(),
            orderProcessor,
            exceptionRepository,
            triggerService,
            unitOfWork,
            new ErpImportInstanceIdentity(),
            TimeProvider.System,
            Substitute.For<ILogger<ErpImportFileImporter>>());

        var legacyFolderMigrator = new ErpImportLegacyFolderMigrator(
            _objectStorage, fileRepository, exceptionRepository, unitOfWork, TimeProvider.System,
            Substitute.For<ILogger<ErpImportLegacyFolderMigrator>>());

        var retentionService = new ErpImportRetentionService(
            fileRepository, exceptionRepository, _objectStorage, unitOfWork, TimeProvider.System,
            Substitute.For<ILogger<ErpImportRetentionService>>());

        return new ErpOrderImportRunner(
            dropPointRepository,
            defaultDropPointProvider,
            legacyFolderMigrator,
            fileRepository,
            retentionService,
            inboxRegistration,
            importer,
            settingsRepository,
            companyClock,
            unitOfWork,
            new Klacks.Api.Application.Services.Imports.ErpImportRunState(),
            TimeProvider.System,
            Substitute.For<ILogger<ErpOrderImportRunner>>(),
            new ErpCronTimeZoneDriftNotifier(),
            Substitute.For<Klacks.Api.Application.Interfaces.Assistant.IOpenOrderSummaryRescan>());
    }

    private string ReadFixtureXml()
    {
        var fixturePath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Imports", "Fixtures", FixtureFileName);
        return File.ReadAllText(fixturePath)
            .Replace(FixtureSourceSystemIdAttribute, $"sourceSystemId=\"{_fixtureSourceSystemId}\"")
            .Replace(FixtureFirstCompany, _firstCompany)
            .Replace(FixtureSecondCompany, _secondCompany);
    }

    private static string UniqueFileName()
    {
        return $"order-import-{Guid.NewGuid():N}.xml";
    }

    private string PendingKey(string fileName)
    {
        return $"{_storagePrefix}{fileName}";
    }

    private async Task UploadToKeyAsync(string key, string xml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await _objectStorage.UploadAsync(key, stream);
    }

    private async Task UploadAsync(string fileName, string xml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await _objectStorage.UploadAsync(PendingKey(fileName), stream);
    }

    private async Task<ErpImportFile> GivenLedgerRowAsync(ErpImportFileStatus status, string xml, Action<ErpImportFile>? configure = null)
    {
        var key = ErpImportStorageKeys.BuildUploadKey(_storagePrefix, InboxFileName);
        StoredObjectInfo stored;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
        {
            stored = await _objectStorage.UploadAsync(key, stream);
        }

        var row = new ErpImportFile
        {
            Id = Guid.NewGuid(),
            DropPointId = _dropPointId,
            SourceSystemId = DropPointSourceSystemId,
            StorageKey = key,
            FileName = InboxFileName,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            Channel = ErpImportChannel.InboxFolder,
            Status = status,
            ReceivedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        configure?.Invoke(row);

        using var context = CreateContext();
        context.ErpImportFiles.Add(row);
        await context.SaveChangesAsync();
        return row;
    }

    private async Task<List<ErpImportFile>> LoadLedgerRowsAsync()
    {
        return await _context.ErpImportFiles
            .AsNoTracking()
            .Where(f => f.DropPointId == _dropPointId)
            .ToListAsync();
    }

    private static void AssertCounts(
        ErpImportFile row,
        int total,
        int created = 0,
        int updated = 0,
        int unchanged = 0,
        int superseded = 0,
        int rejected = 0)
    {
        row.TotalOrders.ShouldBe(total);
        row.ProcessedOrders.ShouldBe(total);
        row.NewCount.ShouldBe(created);
        row.UpdatedCount.ShouldBe(updated);
        row.UnchangedCount.ShouldBe(unchanged);
        row.SupersededCount.ShouldBe(superseded);
        row.RejectedCount.ShouldBe(rejected);
    }

    private async Task<List<Client>> LoadImportedClientsAsync()
    {
        return await _context.Client
            .AsNoTracking()
            .Include(c => c.Addresses)
            .Where(c => c.SourceSystemId == _fixtureSourceSystemId)
            .ToListAsync();
    }

    private static SettingsRepository CreateSettingsRepository(DataBaseContext context)
    {
        var macroManagementService = Substitute.For<Klacks.Api.Domain.Interfaces.Macros.IMacroManagementService>();
        macroManagementService.GetMacroListAsync()
            .Returns(_ => context.Macro.AsNoTracking().Where(m => !m.IsDeleted).ToListAsync());
        return new SettingsRepository(
            context,
            Substitute.For<ICalendarRuleFilterService>(),
            Substitute.For<ICalendarRuleSortingService>(),
            Substitute.For<ICalendarRulePaginationService>(),
            macroManagementService);
    }

        private async Task<List<Shift>> LoadImportedShiftsAsync()
    {
        return await _context.Shift
            .AsNoTracking()
            .Where(s => s.SourceSystemId == _fixtureSourceSystemId)
            .ToListAsync();
    }

    // The cleanup must only ever match rows whose source_system_id carries the
    // integration-test prefix; production data can share this database.
    private static async Task CleanupTestDataAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM erp_import_exception WHERE source_system_id LIKE '{TestDataPrefix}%';
            DELETE FROM erp_import_file WHERE drop_point_id IN (SELECT id FROM erp_drop_points WHERE source_system_id LIKE '{TestDataPrefix}%');
            DELETE FROM erp_drop_points WHERE source_system_id LIKE '{TestDataPrefix}%';
            DELETE FROM shift WHERE source_system_id LIKE '{TestDataPrefix}%';
            DELETE FROM communication WHERE client_id IN (SELECT id FROM client WHERE source_system_id LIKE '{TestDataPrefix}%');
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE source_system_id LIKE '{TestDataPrefix}%');
            DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE source_system_id LIKE '{TestDataPrefix}%');
            DELETE FROM client WHERE source_system_id LIKE '{TestDataPrefix}%';
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
