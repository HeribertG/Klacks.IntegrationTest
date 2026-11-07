// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The recalculation window against PostgreSQL (IT database klacks): FrozenPeriodPolicy's two queries (distinct export
/// ranges, Closed real-plan work or break) translate on Npgsql and freeze exactly the sealed, exported periods older than
/// the person's latest two export ranges; a reopened period, a person with two ranges and a scenario stay open. End to
/// end through RecalculatePeriodHoursCommandHandler with the real PeriodHoursService, audit repository and unit of work:
/// without confirmation the frozen row keeps its exported value while open clients are written, with confirmation and
/// a reason the frozen row is recalculated and one audit row (RecalculateExportedPeriod) counts the frozen clients.
/// The group filter limits the run to the fixture's clients; every row carries the fixture prefix and lies in 2099.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Works;

[TestFixture]
[Category("RealDatabase")]
public class FrozenPeriodRecalculationPostgresTests
{
    private const string Prefix = WorkChainTestData.PrefixRoot + "FROZEN_";
    private const decimal StaleHours = 99m;
    private const decimal SealedHours = 8m;
    private const decimal OpenHours = 6m;
    private const string FormatKey = "it-frozen";

    private static readonly DateOnly FirstPeriodDay = new(2099, 1, 15);
    private static readonly Guid GroupId = Guid.NewGuid();

    private (DateOnly Start, DateOnly End) _first;
    private (DateOnly Start, DateOnly End) _second;
    private (DateOnly Start, DateOnly End) _third;
    private Guid _frozenClient;
    private Guid _openClient;
    private Guid _reopenedClient;
    private Guid _twoExportsClient;
    private Guid _sealedBreakClient;

    [SetUp]
    public async Task SetUp()
    {
        await CleanupAsync();

        await using (var boundaries = new WorkWriteChainStack())
        {
            _first = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(FirstPeriodDay);
            _second = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(_first.End.AddDays(1));
            _third = await boundaries.PeriodHoursService.GetPeriodBoundariesAsync(_second.End.AddDays(1));
        }

        var shiftId = await WorkChainTestData.SeedShiftAsync(Prefix);
        var absenceId = await WorkChainTestData.SeedAbsenceAsync(Prefix, "ABSENCE", isOnCall: false);
        var day = _first.Start.AddDays(2);

        _frozenClient = await SeedMemberAsync("FROZEN");
        await SeedWorkAsync(_frozenClient, shiftId, day, WorkLockLevel.Closed, SealedHours);
        await SeedExportsAsync(_frozenClient, _first, _second, _third);
        await WorkChainTestData.SeedPeriodHoursAsync(_frozenClient, _first, StaleHours, 0m);

        _openClient = await SeedMemberAsync("OPEN");
        await SeedWorkAsync(_openClient, shiftId, day, WorkLockLevel.None, OpenHours);
        await WorkChainTestData.SeedPeriodHoursAsync(_openClient, _first, StaleHours, 0m);

        _reopenedClient = await SeedMemberAsync("REOPENED");
        await SeedWorkAsync(_reopenedClient, shiftId, day, WorkLockLevel.Approved, SealedHours);
        await SeedExportsAsync(_reopenedClient, _first, _second, _third);

        _twoExportsClient = await SeedMemberAsync("TWOEXPORTS");
        await SeedWorkAsync(_twoExportsClient, shiftId, day, WorkLockLevel.Closed, SealedHours);
        await SeedExportsAsync(_twoExportsClient, _first, _second, _second);

        _sealedBreakClient = await SeedMemberAsync("SEALEDBREAK");
        await SaveAsync(context => context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _sealedBreakClient,
            AbsenceId = absenceId,
            CurrentDate = day,
            LockLevel = WorkLockLevel.Closed
        }));
        await SeedExportsAsync(_sealedBreakClient, _first, _second, _third);
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Policy_FreezesOnlySealedExportedPeriodsBehindTheLatestTwoExports()
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        var policy = new FrozenPeriodPolicy(context);
        var allClients = new[] { _frozenClient, _openClient, _reopenedClient, _twoExportsClient, _sealedBreakClient };

        var frozen = await policy.GetFrozenClientIdsAsync(allClients, _first.Start, _first.End, null);
        var inWindow = await policy.GetFrozenClientIdsAsync(allClients, _second.Start, _second.End, null);
        var scenario = await policy.GetFrozenClientIdsAsync(allClients, _first.Start, _first.End, Guid.NewGuid());

        frozen.ShouldBe([_frozenClient, _sealedBreakClient], ignoreOrder: true);
        inWindow.ShouldBeEmpty();
        scenario.ShouldBeEmpty();
    }

    [Test]
    public async Task Policy_RowFilter_KeepsOnlyRowsOfOpenPeriods()
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        var rows = await context.ClientPeriodHours
            .Where(row => row.ClientId == _frozenClient || row.ClientId == _openClient)
            .ToListAsync();

        var kept = await new FrozenPeriodPolicy(context).ExcludeFrozenRowsAsync(rows);

        kept.Select(row => row.ClientId).ShouldBe([_openClient]);
    }

    [Test]
    public async Task Handler_WithoutConfirmation_LeavesTheFrozenRow_AndWritesTheOpenClients()
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        var handler = NewHandler(context);

        var result = await handler.Handle(
            new RecalculatePeriodHoursCommand(_first.Start, _first.End, GroupId), CancellationToken.None);

        result.FrozenSkipped.ShouldBe(2);
        (await HoursAsync(_frozenClient)).ShouldBe(StaleHours);
        (await HoursAsync(_openClient)).ShouldBe(OpenHours);
        (await HoursAsync(_reopenedClient)).ShouldBe(SealedHours);
        (await HoursAsync(_twoExportsClient)).ShouldBe(SealedHours);
        (await HoursAsync(_sealedBreakClient)).ShouldBeNull();
        (await AuditRowsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Handler_WithConfirmation_RecalculatesTheFrozenRow_AndAuditsTheFrozenClients()
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        var handler = NewHandler(context);

        var result = await handler.Handle(
            new RecalculatePeriodHoursCommand(_first.Start, _first.End, GroupId, ConfirmFrozen: true, Reason: Prefix + "correction"),
            CancellationToken.None);

        result.FrozenSkipped.ShouldBe(0);
        (await HoursAsync(_frozenClient)).ShouldBe(SealedHours);
        var audit = (await AuditRowsAsync()).ShouldHaveSingleItem();
        audit.Action.ShouldBe(PeriodAuditAction.RecalculateExportedPeriod);
        audit.AffectedCount.ShouldBe(2);
        audit.GroupId.ShouldBe(GroupId);
        audit.StartDate.ShouldBe(_first.Start);
        audit.EndDate.ShouldBe(_first.End);
    }

    private static RecalculatePeriodHoursCommandHandler NewHandler(DataBaseContext context)
    {
        var groupFilter = Substitute.For<IClientGroupFilterService>();
        groupFilter.FilterClientsByGroupId(GroupId, Arg.Any<IQueryable<Client>>())
            .Returns(call => call.Arg<IQueryable<Client>>().Where(client => client.Name.StartsWith(Prefix)));
        groupFilter.IsCallerUnrestrictedAsync().Returns(true);

        var contractData = Substitute.For<IClientContractDataProvider>();
        contractData.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        contractData.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());

        var weeks = Substitute.For<IWeekConfiguration>();
        weeks.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => MondayOf(call.Arg<DateOnly>()));

        var service = new PeriodHoursService(
            context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            groupFilter,
            contractData,
            weeks,
            new FrozenPeriodPolicy(context));
        var audit = new FrozenPeriodOverrideAudit(
            new PeriodAuditLogRepository(context),
            new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>()),
            Substitute.For<IUserService>());
        return new RecalculatePeriodHoursCommandHandler(
            service,
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IBreakMacroService>(),
            audit,
            Substitute.For<IUserService>(),
            Substitute.For<ILogger<RecalculatePeriodHoursCommandHandler>>());
    }

    private static DateOnly MondayOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7));

    private static async Task<Guid> SeedMemberAsync(string suffix)
    {
        var clientId = await WorkChainTestData.SeedClientAsync(Prefix, suffix);
        await SaveAsync(context => context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ValidFrom = new DateTime(2098, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }));
        return clientId;
    }

    private static Task SeedWorkAsync(Guid clientId, Guid shiftId, DateOnly day, WorkLockLevel lockLevel, decimal workTime) =>
        SaveAsync(context => context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = day,
            StartTime = WorkChainTestData.MorningStart,
            EndTime = WorkChainTestData.MorningEnd,
            WorkTime = workTime,
            LockLevel = lockLevel
        }));

    private static Task SeedExportsAsync(Guid clientId, params (DateOnly Start, DateOnly End)[] ranges) =>
        SaveAsync(context =>
        {
            var revisions = new Dictionary<(DateOnly Start, DateOnly End), int>();
            foreach (var (start, end) in ranges)
            {
                var revision = revisions[(start, end)] = revisions.GetValueOrDefault((start, end)) + 1;
                var log = new ExportLog
                {
                    Id = Guid.NewGuid(),
                    Format = FormatKey,
                    StartDate = start,
                    EndDate = end,
                    FileName = Prefix + "export.csv",
                    ExportedAt = DateTime.UtcNow,
                    ExportedBy = Prefix
                };
                context.ExportLog.Add(log);
                context.ExportLogItem.Add(new ExportLogItem
                {
                    Id = Guid.NewGuid(),
                    ExportLogId = log.Id,
                    ClientId = clientId,
                    StartDate = start,
                    EndDate = end,
                    Format = FormatKey,
                    Revision = revision,
                    IsSupplementary = revision > 1
                });
            }
        });

    private async Task<decimal?> HoursAsync(Guid clientId)
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        var row = await context.ClientPeriodHours.AsNoTracking().SingleOrDefaultAsync(p =>
            p.ClientId == clientId && p.StartDate == _first.Start && p.EndDate == _first.End && p.AnalyseToken == null);
        return row?.Hours;
    }

    private static async Task<List<PeriodAuditLog>> AuditRowsAsync()
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        return await context.PeriodAuditLog.AsNoTracking()
            .Where(log => log.Reason != null && log.Reason.StartsWith(Prefix))
            .ToListAsync();
    }

    private static async Task SaveAsync(Action<DataBaseContext> stage)
    {
        await using var context = WorkChainTestData.NewRetryingContext();
        stage(context);
        await context.SaveChangesAsync();
    }

    private static async Task CleanupAsync()
    {
        var pattern = Prefix.Replace("'", "''");
        var sql = $@"
            DELETE FROM export_log_item WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{pattern}'));
            DELETE FROM export_log WHERE starts_with(file_name, '{pattern}');
            DELETE FROM period_audit_log WHERE starts_with(reason, '{pattern}');
        ";
        await using (var context = WorkChainTestData.NewRetryingContext())
        {
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        await WorkChainTestData.CleanupAsync(Prefix);
    }
}
