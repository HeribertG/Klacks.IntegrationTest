// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end PostgreSQL test of PayrollPeriodExportService with the real completeness gate, data loader,
/// repositories, unit of work, DATEV formatter and a file-system artifact storage in a temporary directory: the first
/// export contains every selected person at revision 1 and stores a readable artifact, an unchanged re-export is
/// "nothing new", a changed person is exported alone at revision 2 as a supplementary export, an incomplete period is
/// blocked without writing anything and an over-long period is rejected up front. The service is scoped to the test's
/// own persons; rows and artifacts are removed again by their own ids and the INTEGRATION_TEST_ prefix.
/// </summary>
using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Services.Exports;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Domain.Services.Exports;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Exports;

[TestFixture]
[Category("RealDatabase")]
public class PayrollPeriodExportServicePostgresTests : PayrollPostgresFixtureBase
{
    private const string Format = PayrollExportConstants.FormatKeyDatevLug;
    private const decimal ChangedHoursPerDay = 7m;

    private string _artifactRoot = null!;
    private PayrollArtifactStorage _storage = null!;
    private PayrollPeriodExportService _service = null!;

    [SetUp]
    public void SetUpService()
    {
        _artifactRoot = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        _storage = new PayrollArtifactStorage(Options.Create(new PayrollObjectStorageOptions { RootPath = _artifactRoot }));

        var policy = Substitute.For<IExportFormatPolicy>();
        policy.IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var configRepository = Substitute.For<IPayrollExportConfigRepository>();
        configRepository.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new PayrollExportGroupConfig { TargetSystem = Format, BaseWageType = "1000" });
        var overrideApplier = Substitute.For<IExportFormatOverrideApplier>();

        var itemRepository = new ExportLogItemRepository(Context);
        _service = new PayrollPeriodExportService(
            [new DatevLugBewegungsdatenFormatter()],
            policy,
            new PayrollCompletenessGate(Context, itemRepository),
            new PayrollExportDataLoader(Context),
            itemRepository,
            new ExportLogRepository(Context),
            configRepository,
            overrideApplier,
            _storage,
            new UnitOfWork(Context, Substitute.For<ILogger<UnitOfWork>>()),
            Substitute.For<ILogger<PayrollPeriodExportService>>());
    }

    [TearDown]
    public void RemoveArtifacts()
    {
        if (Directory.Exists(_artifactRoot))
        {
            Directory.Delete(_artifactRoot, recursive: true);
        }
    }

    [Test]
    public async Task FirstExport_IsRevisionOneForEveryPerson_AndTheArtifactCanBeReadBack()
    {
        var (personA, personB) = await SealedPeriodWithTwoPersonsAsync();

        var outcome = await ExportAsync(personA, personB);
        TrackExportLog(outcome.ExportLogId);

        outcome.PersonCount.ShouldBe(2);
        outcome.IsSupplementary.ShouldBeFalse();
        outcome.FileName.ShouldBe("payroll-export_2037-03-02_2037-03-04.csv");
        outcome.FileContent.Length.ShouldBeGreaterThan(0);

        Context.ChangeTracker.Clear();
        var items = await Context.ExportLogItem.AsNoTracking().Where(i => i.ExportLogId == outcome.ExportLogId).ToListAsync();
        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.Revision == 1 && !i.IsSupplementary && i.Format == Format);
        items.Select(i => i.ClientId).ShouldBe([personA, personB], ignoreOrder: true);

        var log = await Context.ExportLog.AsNoTracking().SingleAsync(l => l.Id == outcome.ExportLogId);
        log.PersonCount.ShouldBe(2);
        log.GroupId.ShouldBeNull();
        log.StorageKey.ShouldNotBeNull();
        (await _storage.ReadAsync(log.StorageKey!)).ShouldBe(outcome.FileContent);
    }

    [Test]
    public async Task ReExportWithoutChanges_IsNothingNew_AndWritesNothing()
    {
        var (personA, personB) = await SealedPeriodWithTwoPersonsAsync();
        TrackExportLog((await ExportAsync(personA, personB)).ExportLogId);

        await Should.ThrowAsync<PayrollExportNothingNewException>(() => ExportAsync(personA, personB));

        Context.ChangeTracker.Clear();
        (await Context.ExportLog.CountAsync(l => l.ExportedBy == Actor)).ShouldBe(1);
        (await Context.ExportLogItem.CountAsync(i => i.ClientId == personA || i.ClientId == personB)).ShouldBe(2);

        var preview = await _service.PreviewAsync(Day1, Day3, Format, [personA, personB]);
        preview.CanExport.ShouldBeFalse();
        preview.AlreadyExportedCount.ShouldBe(2);
        preview.NewOrChangedPersons.ShouldBeEmpty();
    }

    [Test]
    public async Task ChangedPerson_IsExportedAloneAtRevisionTwo_AsASupplementaryExport()
    {
        var (personA, personB) = await SealedPeriodWithTwoPersonsAsync();
        TrackExportLog((await ExportAsync(personA, personB)).ExportLogId);

        await Context.Work.Where(w => w.ClientId == personB).ExecuteUpdateAsync(s => s.SetProperty(w => w.WorkTime, ChangedHoursPerDay));
        Context.ChangeTracker.Clear();

        var preview = await _service.PreviewAsync(Day1, Day3, Format, [personA, personB]);
        preview.CanExport.ShouldBeTrue();
        preview.NewOrChangedPersons.ShouldHaveSingleItem().ClientId.ShouldBe(personB);

        var supplement = await ExportAsync(personA, personB);
        TrackExportLog(supplement.ExportLogId);

        supplement.PersonCount.ShouldBe(1);
        supplement.IsSupplementary.ShouldBeTrue();
        supplement.FileName.ShouldBe("payroll-export_2037-03-02_2037-03-04_supplement.csv");

        Context.ChangeTracker.Clear();
        var revisions = await Context.ExportLogItem.AsNoTracking()
            .Where(i => i.ClientId == personA || i.ClientId == personB)
            .OrderBy(i => i.Revision)
            .ToListAsync();
        revisions.Count.ShouldBe(3);
        revisions.Single(i => i.ClientId == personA).Revision.ShouldBe(1);
        revisions.Where(i => i.ClientId == personB).Select(i => i.Revision).ShouldBe([1, 2]);
        var second = revisions.Single(i => i.ClientId == personB && i.Revision == 2);
        second.IsSupplementary.ShouldBeTrue();
        second.ExportLogId.ShouldBe(supplement.ExportLogId);
        using var snapshot = JsonDocument.Parse(second.EntriesJson);
        snapshot.RootElement.EnumerateArray().Sum(e => e.GetProperty("Quantity").GetDecimal()).ShouldBe(2 * ChangedHoursPerDay);
    }

    [Test]
    public async Task IncompletePeriod_IsBlocked_AndNothingIsWrittenOrStored()
    {
        var personA = AddPerson("Blocked", GroupId);
        AddWork(personA, Day1, 8m, WorkLockLevel.Approved);
        SealPeriod(GroupId);
        await Context.SaveChangesAsync();

        var thrown = await Should.ThrowAsync<PayrollExportBlockedException>(() => ExportAsync(personA));

        thrown.Completeness.Blockers.ShouldHaveSingleItem().Reason.ShouldBe(PayrollExportBlockReason.EntryNotClosed);
        Context.ChangeTracker.Clear();
        (await Context.ExportLog.CountAsync(l => l.ExportedBy == Actor)).ShouldBe(0);
        (await Context.ExportLogItem.CountAsync(i => i.ClientId == personA)).ShouldBe(0);
        Directory.Exists(_artifactRoot).ShouldBeFalse();
    }

    [Test]
    public async Task PeriodLongerThanTheMaximum_IsRejectedBeforeAnyQuery()
    {
        var tooLongUntil = Day1.AddDays(PayrollExportConstants.MaxPeriodDays);

        await Should.ThrowAsync<InvalidRequestException>(() =>
            _service.ExportAsync(Day1, tooLongUntil, Format, "de", [Guid.NewGuid()], Actor));
    }

    private async Task<(Guid PersonA, Guid PersonB)> SealedPeriodWithTwoPersonsAsync()
    {
        var personA = AddPerson("PersonA", GroupId);
        var personB = AddPerson("PersonB", GroupId);
        AddWork(personA, Day1, 8m, WorkLockLevel.Closed);
        AddWork(personB, Day1, 6m, WorkLockLevel.Closed);
        AddWork(personB, Day2, 4m, WorkLockLevel.Closed);
        SealPeriod(GroupId);
        await Context.SaveChangesAsync();
        return (personA, personB);
    }

    private Task<PayrollExportOutcome> ExportAsync(params Guid[] clientIds)
    {
        return _service.ExportAsync(Day1, Day3, Format, "de", clientIds, Actor);
    }
}
