// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// PostgreSQL proof for the per-person export record (ExportLogItem): the jsonb snapshot round-trips, the constructor
/// projection of GetLatestItemsAsync and the overlap query translate on Npgsql, and the partial unique revision index
/// rejects a second live row for the same person, period, format and revision as DatabaseUpdateException.IsDuplicate
/// through the UnitOfWork while a soft-deleted row does not block it. Test rows carry the INTEGRATION_TEST_ prefix and
/// are removed by their own ids.
/// </summary>
using System.Text.Json;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Domain.Services.Exports;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Exports;

[TestFixture]
[Category("RealDatabase")]
public class ExportLogItemPostgresTests : PayrollPostgresFixtureBase
{
    private const string FormatKey = "datev-lug-bewegungsdaten";
    private const string OtherFormatKey = "paxml-se";

    private ExportLogItemRepository _repository = null!;
    private UnitOfWork _unitOfWork = null!;

    [SetUp]
    public void SetUpRepository()
    {
        _repository = new ExportLogItemRepository(Context);
        _unitOfWork = new UnitOfWork(Context, Substitute.For<ILogger<UnitOfWork>>());
    }

    [Test]
    public async Task InsertAndRead_RoundTripsTheJsonbSnapshotAndEveryColumn()
    {
        var clientId = Guid.NewGuid();
        var log = NewLog();
        var entries = new List<PayrollDayEntry>
        {
            new() { Date = Day1, Kind = PayrollEntryKind.WorkHours, Quantity = 8.5m },
            new() { Date = Day2, Kind = PayrollEntryKind.Surcharge, Quantity = 1.25m },
        };
        var snapshot = PayrollEntriesSnapshot.ToJson(entries);
        var hash = PayrollPersonContentHash.Compute(entries);
        var item = NewItem(log, clientId, 1, hash, snapshot);

        Context.ExportLog.Add(log);
        await _repository.AddRangeAsync([item]);
        await _unitOfWork.CompleteAsync();

        Context.ChangeTracker.Clear();
        var stored = await Context.ExportLogItem.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        stored.ExportLogId.ShouldBe(log.Id);
        stored.ClientId.ShouldBe(clientId);
        stored.StartDate.ShouldBe(Day1);
        stored.EndDate.ShouldBe(Day3);
        stored.Format.ShouldBe(FormatKey);
        stored.Revision.ShouldBe(1);
        stored.ContentHash.ShouldBe(hash);
        stored.EntryCount.ShouldBe(2);
        stored.IsSupplementary.ShouldBeFalse();

        using var expected = JsonDocument.Parse(snapshot);
        using var actual = JsonDocument.Parse(stored.EntriesJson);
        actual.RootElement.GetArrayLength().ShouldBe(2);
        actual.RootElement[0].GetProperty("Date").GetString().ShouldBe("2037-03-02");
        actual.RootElement[0].GetProperty("Quantity").GetDecimal().ShouldBe(8.5m);
        actual.RootElement[1].GetProperty("Kind").GetInt32().ShouldBe((int)PayrollEntryKind.Surcharge);
        actual.RootElement.GetArrayLength().ShouldBe(expected.RootElement.GetArrayLength());
    }

    [Test]
    public async Task GetLatestItemsAsync_ProjectsTheHighestRevisionPerPerson_ForTheExactPeriodAndFormat()
    {
        var personA = Guid.NewGuid();
        var personB = Guid.NewGuid();
        var log = NewLog();
        Context.ExportLog.Add(log);
        Context.ExportLogItem.AddRange(
            NewItem(log, personA, 1, Hash('a')),
            NewItem(log, personA, 2, Hash('b')),
            NewItem(log, personB, 1, Hash('c')),
            NewItem(log, personA, 1, Hash('d'), format: OtherFormatKey),
            NewItem(log, personB, 1, Hash('e'), start: Day1, end: Day2));
        await _unitOfWork.CompleteAsync();
        Context.ChangeTracker.Clear();

        var all = await _repository.GetLatestItemsAsync(Day1, Day3, FormatKey, [personA, personB]);
        var onlyB = await _repository.GetLatestItemsAsync(Day1, Day3, FormatKey, [personB]);
        var otherPeriod = await _repository.GetLatestItemsAsync(Day1, Day2, FormatKey, [personA, personB]);

        all.Count.ShouldBe(2);
        all[personA].Revision.ShouldBe(2);
        all[personA].ContentHash.ShouldBe(Hash('b'));
        all[personB].Revision.ShouldBe(1);
        all[personB].ContentHash.ShouldBe(Hash('c'));
        onlyB.Keys.ShouldBe([personB]);
        otherPeriod.Keys.ShouldBe([personB]);
        otherPeriod[personB].ContentHash.ShouldBe(Hash('e'));
    }

    [Test]
    public async Task GetOverlappingAsync_FindsOtherPeriodsOfThePerson_NotTheExactOne()
    {
        var person = Guid.NewGuid();
        var log = NewLog();
        Context.ExportLog.Add(log);
        Context.ExportLogItem.AddRange(
            NewItem(log, person, 1, Hash('a')),
            NewItem(log, person, 1, Hash('b'), start: Day1, end: Day2),
            NewItem(log, person, 1, Hash('c'), start: Day3.AddDays(5), end: Day3.AddDays(9)));
        await _unitOfWork.CompleteAsync();
        Context.ChangeTracker.Clear();

        var overlapping = await _repository.GetOverlappingAsync([person], Day1, Day3);

        var item = overlapping.ShouldHaveSingleItem();
        item.StartDate.ShouldBe(Day1);
        item.EndDate.ShouldBe(Day2);
    }

    [Test]
    public async Task RevisionIndex_RejectsASecondLiveRowForTheSamePersonPeriodFormatAndRevision()
    {
        var person = Guid.NewGuid();
        var firstLog = NewLog();
        Context.ExportLog.Add(firstLog);
        Context.ExportLogItem.Add(NewItem(firstLog, person, 1, Hash('a')));
        await _unitOfWork.CompleteAsync();

        var secondLog = NewLog();
        Context.ExportLog.Add(secondLog);
        Context.ExportLogItem.Add(NewItem(secondLog, person, 1, Hash('b')));

        var thrown = await Should.ThrowAsync<DatabaseUpdateException>(() => _unitOfWork.CompleteAsync());

        thrown.IsDuplicate.ShouldBeTrue();
        thrown.IsForeignKeyViolation.ShouldBeFalse();
    }

    [Test]
    public async Task RevisionIndex_AllowsTheSameRevisionNextToASoftDeletedRow_AndAnotherRevisionOrFormat()
    {
        var person = Guid.NewGuid();
        var log = NewLog();
        Context.ExportLog.Add(log);
        var deleted = NewItem(log, person, 1, Hash('a'));
        deleted.IsDeleted = true;
        deleted.DeletedTime = DateTime.UtcNow;
        Context.ExportLogItem.AddRange(deleted, NewItem(log, person, 1, Hash('b')));
        await _unitOfWork.CompleteAsync();

        Context.ExportLogItem.AddRange(
            NewItem(log, person, 2, Hash('c')),
            NewItem(log, person, 1, Hash('d'), format: OtherFormatKey));
        await _unitOfWork.CompleteAsync();

        Context.ChangeTracker.Clear();
        (await Context.ExportLogItem.CountAsync(i => i.ClientId == person)).ShouldBe(3);
    }

    private ExportLog NewLog()
    {
        var log = new ExportLog
        {
            Id = Guid.NewGuid(),
            Format = FormatKey,
            StartDate = Day1,
            EndDate = Day3,
            Language = "de",
            FileName = Prefix + "export.csv",
            ExportedAt = DateTime.UtcNow,
            ExportedBy = Actor,
            PersonCount = 1,
        };
        TrackExportLog(log.Id);
        return log;
    }

    private static ExportLogItem NewItem(
        ExportLog log,
        Guid clientId,
        int revision,
        string hash,
        string snapshot = "[]",
        string? format = null,
        DateOnly? start = null,
        DateOnly? end = null)
    {
        return new ExportLogItem
        {
            Id = Guid.NewGuid(),
            ExportLogId = log.Id,
            ClientId = clientId,
            StartDate = start ?? log.StartDate,
            EndDate = end ?? log.EndDate,
            Format = format ?? log.Format,
            Revision = revision,
            ContentHash = hash,
            EntryCount = snapshot == "[]" ? 0 : 2,
            EntriesJson = snapshot,
            IsSupplementary = revision > 1,
        };
    }

    private static string Hash(char fill) => new(fill, 64);
}
