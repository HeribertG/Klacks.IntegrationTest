// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using System.Text;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Domain.Services.Common;
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

namespace Klacks.IntegrationTest.Imports;

[TestFixture]
[Category("RealDatabase")]
public class ErpOrderImportFlowIntegrationTests
{
    private const string TestDataPrefix = "INTEGRATION_TEST_ERP_";
    private const string DropPointSourceSystemId = "INTEGRATION_TEST_ERP_SOURCE";
    private const string ProcessedSegment = "processed";
    private const string ErrorSegment = "error";
    private const string ProcessingSegment = "processing";
    private const string ForeignClaimPayload = "claimed by another instance";

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
    private string _fixtureSourceSystemId = null!;
    private string _firstCompany = null!;
    private string _secondCompany = null!;
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

        _context.ErpDropPoints.Add(new ErpDropPoint
        {
            Id = Guid.NewGuid(),
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
    public async Task RunAsync_SampleFixtureInDropPoint_ImportsCustomersAndOrdersAndArchivesFile()
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

        _objectStorage.Contains(ProcessedKey(fileName)).ShouldBeTrue("the handled file must be archived under processed/");
        _objectStorage.Contains(PendingKey(fileName)).ShouldBeFalse("the original key must be gone after the move");
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
        await _context.Database.ExecuteSqlAsync(
            $"UPDATE shift SET sum_employees = {driftedSumEmployees} WHERE id = {firstDraftId}");

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

        shifts.Single(s => s.ExternalOrderReference == SecondOrderReference).Id.ShouldBe(secondDraftId);
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
        closedOrder.UntilDate.ShouldBe(DateOnly.FromDateTime(DateTime.UtcNow), "the superseded order must be closed as of today");

        var replacementDraft = versions.Single(s => s.Id != sealedOrderId);
        replacementDraft.Status.ShouldBe(ShiftStatus.OriginalOrder);
        replacementDraft.SupersedesOrderId.ShouldBe(sealedOrderId);
        replacementDraft.EndShift.ShouldBe(new TimeOnly(16, 0));
        replacementDraft.UntilDate.ShouldBe(new DateOnly(2026, 12, 31));
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

        _objectStorage.Contains(ErrorKey(fileName)).ShouldBeTrue("a file with a rejected order must be quarantined under error/");
        _objectStorage.Contains(ProcessedKey(fileName)).ShouldBeFalse();
        _objectStorage.Contains(PendingKey(fileName)).ShouldBeFalse();

        var recordedExceptions = await _context.Set<ErpImportException>()
            .AsNoTracking()
            .Where(e => e.SourceSystemId == DropPointSourceSystemId)
            .ToListAsync();
        var recorded = recordedExceptions.ShouldHaveSingleItem();
        recorded.FileKey.ShouldBe(PendingKey(fileName));
        recorded.ExternalOrderReference.ShouldBe(SecondOrderReference);
        recorded.Reason.ShouldContain("weekday");
    }

    [Test]
    public async Task RunAsync_TruncatedXml_MovesFileToErrorAndRecordsImportException()
    {
        var fileName = UniqueFileName();
        var fixtureXml = ReadFixtureXml();
        await UploadAsync(fileName, fixtureXml[..(fixtureXml.Length / 2)]);

        await RunImportAsync();

        _objectStorage.Contains(ErrorKey(fileName)).ShouldBeTrue("an unparsable file must be quarantined under error/");
        _objectStorage.Contains(PendingKey(fileName)).ShouldBeFalse();
        _objectStorage.Contains(ProcessedKey(fileName)).ShouldBeFalse();

        (await LoadImportedShiftsAsync()).ShouldBeEmpty("an unparsable file must not create any order");
        (await LoadImportedClientsAsync()).ShouldBeEmpty("an unparsable file must not create any customer");

        var recordedExceptions = await _context.Set<ErpImportException>()
            .AsNoTracking()
            .Where(e => e.SourceSystemId == DropPointSourceSystemId)
            .ToListAsync();
        var recorded = recordedExceptions.ShouldHaveSingleItem();
        recorded.FileKey.ShouldBe(PendingKey(fileName));
        recorded.ExternalOrderReference.ShouldBeNull();
        recorded.Reason.ShouldContain(MalformedXmlReasonFragment);
        recorded.ResolvedAt.ShouldBeNull();
    }

    [Test]
    public async Task RunAsync_HandledFile_LeavesNothingBehindInTheProcessingSegment()
    {
        var fileName = UniqueFileName();
        await UploadAsync(fileName, ReadFixtureXml());

        await RunImportAsync();

        _objectStorage.Contains(ProcessedKey(fileName)).ShouldBeTrue("the handled file must end up under processed/");
        _objectStorage.Contains(ProcessingKey(fileName)).ShouldBeFalse(
            "the claim must be resolved by the move, otherwise every import leaves an orphan behind that no later run ever picks up");
        _objectStorage.Contains(PendingKey(fileName)).ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_FileClaimedByAnotherInstance_ImportsNothingAndLeavesTheFileUntouched()
    {
        var fileName = UniqueFileName();
        await UploadAsync(fileName, ReadFixtureXml());
        await OccupyProcessingSlotAsync(fileName);

        await RunImportAsync();

        _objectStorage.Contains(PendingKey(fileName)).ShouldBeTrue(
            "a file this run could not claim must stay untouched at its original key, so the run that owns it keeps working on it");
        (await ReadStoredTextAsync(ProcessingKey(fileName))).ShouldBe(ForeignClaimPayload,
            "the losing run must not overwrite what the owning run put into the processing slot");
        _objectStorage.Contains(ProcessedKey(fileName)).ShouldBeFalse();
        _objectStorage.Contains(ErrorKey(fileName)).ShouldBeFalse();

        (await LoadImportedShiftsAsync()).ShouldBeEmpty("a file claimed elsewhere must not be imported a second time");
        (await LoadImportedClientsAsync()).ShouldBeEmpty("a file claimed elsewhere must not create its customers a second time");

        var recordedExceptions = await _context.Set<ErpImportException>()
            .AsNoTracking()
            .Where(e => e.SourceSystemId == DropPointSourceSystemId)
            .ToListAsync();
        recordedExceptions.ShouldBeEmpty("losing a claim is normal concurrency, not an import failure worth reporting");
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
            new ScheduleMapper());

        var workRepository = new WorkRepository(
            context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Macros.IWorkMacroService>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Associations.IClientContractDataProvider>());

        var settingsRepository = new SettingsRepository(
            context,
            Substitute.For<ICalendarRuleFilterService>(),
            Substitute.For<ICalendarRuleSortingService>(),
            Substitute.For<ICalendarRulePaginationService>(),
            Substitute.For<Klacks.Api.Domain.Interfaces.Macros.IMacroManagementService>());

        var triggerService = Substitute.For<IAgentTriggerService>();

        var supersessionService = new OrderSupersessionService(
            shiftRepository,
            workRepository,
            clientRepository,
            triggerService,
            unitOfWork,
            Substitute.For<ILogger<OrderSupersessionService>>());

        return new ErpOrderImportRunner(
            new ErpDropPointRepository(context, Substitute.For<ILogger<ErpDropPoint>>()),
            _objectStorage,
            new XmlOrderImportParser(),
            new ErpCustomerResolver(clientRepository),
            shiftRepository,
            supersessionService,
            new ErpImportExceptionRepository(context, Substitute.For<ILogger<ErpImportException>>()),
            triggerService,
            settingsRepository,
            unitOfWork,
            Substitute.For<ILogger<ErpOrderImportRunner>>());
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

    private string ProcessedKey(string fileName)
    {
        return $"{_storagePrefix}{ProcessedSegment}/{fileName}";
    }

    private string ErrorKey(string fileName)
    {
        return $"{_storagePrefix}{ErrorSegment}/{fileName}";
    }

    private string ProcessingKey(string fileName)
    {
        return $"{_storagePrefix}{ProcessingSegment}/{fileName}";
    }

    private async Task UploadAsync(string fileName, string xml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await _objectStorage.UploadAsync(PendingKey(fileName), stream);
    }

    private async Task OccupyProcessingSlotAsync(string fileName)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ForeignClaimPayload));
        await _objectStorage.UploadAsync(ProcessingKey(fileName), stream);
    }

    private async Task<string> ReadStoredTextAsync(string key)
    {
        await using var stream = await _objectStorage.DownloadAsync(key);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task<List<Client>> LoadImportedClientsAsync()
    {
        return await _context.Client
            .AsNoTracking()
            .Include(c => c.Addresses)
            .Where(c => c.SourceSystemId == _fixtureSourceSystemId)
            .ToListAsync();
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
