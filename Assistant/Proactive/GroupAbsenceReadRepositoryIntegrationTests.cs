// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for GroupAbsenceReadRepository (Paket D, company-holiday detection) against the real
/// PostgreSQL database. The unit tests cannot cover the risk these queries carry: the EF InMemory provider
/// evaluates an untranslatable LINQ shape client-side and passes, while Npgsql throws at runtime and the
/// trigger tick swallows it as a failed detector - the unstaffed-shift summary would then fall silent.
///
/// Strictly read-only: no rows are inserted, updated or deleted. The fixture picks a root group that already
/// exists and compares the reader's answer against the same predicate expressed independently, so it is safe
/// against the shared database and needs no INTEGRATION_TEST_ cleanup.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.Proactive;

[TestFixture]
[Category("RealDatabase")]
public class GroupAbsenceReadRepositoryIntegrationTests
{
    private const int WindowDays = 120;

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private GroupAbsenceReadRepository _repository = null!;

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [SetUp]
    public void SetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        _context = NewContext();
        _repository = new GroupAbsenceReadRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    private static (DateOnly From, DateOnly Until) Window()
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow);
        return (from, from.AddDays(WindowDays));
    }

    private async Task<Guid?> RootWithClientMembersAsync()
    {
        var rootIds = await _context.GroupItem
            .Where(groupItem => groupItem.ClientId != null && !groupItem.IsDeleted && groupItem.AnalyseToken == null)
            .Join(_context.Group, groupItem => groupItem.GroupId, group => group.Id, (groupItem, group) => group.Root ?? group.Id)
            .Distinct()
            .Take(1)
            .ToListAsync();

        return rootIds.Count == 0 ? null : rootIds[0];
    }

    [Test]
    public async Task GetMembershipWindowsAsync_TranslatesToSql_AndOnlyReturnsMembersOfTheSubtree()
    {
        var rootId = await RootWithClientMembersAsync();
        if (rootId == null)
        {
            Assert.Ignore("No client-linked group_item rows in the test database.");
        }

        var (from, until) = Window();
        var windows = await _repository.GetMembershipWindowsAsync(rootId!.Value, from, until);

        await using var verificationContext = NewContext();
        var subtreeClientIds = await verificationContext.GroupItem
            .Where(groupItem => groupItem.ClientId != null
                && !groupItem.IsDeleted
                && groupItem.AnalyseToken == null
                && groupItem.ScenarioSourceGroupItemId == null)
            .Join(verificationContext.Group.Where(group => group.Id == rootId || group.Root == rootId),
                groupItem => groupItem.GroupId, group => group.Id, (groupItem, _) => groupItem.ClientId!.Value)
            .Distinct()
            .ToListAsync();

        windows.Select(window => window.ClientId).ShouldAllBe(clientId => subtreeClientIds.Contains(clientId));
        windows.ShouldAllBe(window => window.MembershipUntil == null || window.MembershipUntil.Value >= from);
        windows.ShouldAllBe(window => window.MembershipFrom <= until);
    }

    [Test]
    public async Task GetMembershipWindowsAsync_UnknownRoot_TranslatesAndReturnsNothing()
    {
        var (from, until) = Window();

        var windows = await _repository.GetMembershipWindowsAsync(Guid.NewGuid(), from, until);

        windows.ShouldBeEmpty();
    }

    [Test]
    public async Task GetFullDayAbsencesAsync_TranslatesToSql_AndReturnsOnlyFullDayMarkers()
    {
        await using var readContext = NewContext();
        var clientIds = await readContext.Break
            .Where(absence => !absence.IsDeleted
                && absence.AnalyseToken == null
                && absence.StartTime == DayTimeConstants.Midnight
                && absence.EndTime == DayTimeConstants.LastMinuteOfDay)
            .Select(absence => absence.ClientId)
            .Distinct()
            .Take(25)
            .ToListAsync();

        var from = new DateOnly(2000, 1, 1);
        var until = new DateOnly(2100, 12, 31);

        if (clientIds.Count == 0)
        {
            var unknownClientAbsences = await _repository.GetFullDayAbsencesAsync([Guid.NewGuid()], from, until);
            unknownClientAbsences.ShouldBeEmpty();
            return;
        }

        var absences = await _repository.GetFullDayAbsencesAsync(clientIds, from, until);

        var expectedCount = await readContext.Break
            .Where(absence => clientIds.Contains(absence.ClientId)
                && !absence.IsDeleted
                && absence.AnalyseToken == null
                && absence.StartTime == DayTimeConstants.Midnight
                && absence.EndTime == DayTimeConstants.LastMinuteOfDay)
            .Select(absence => new { absence.ClientId, absence.CurrentDate })
            .Distinct()
            .CountAsync();

        absences.Count.ShouldBe(expectedCount);
        absences.Select(absence => absence.ClientId).ShouldAllBe(clientId => clientIds.Contains(clientId));
    }

    [Test]
    public async Task GetFullDayAbsencesAsync_WithNoClients_ShortCircuitsWithoutQuerying()
    {
        var (from, until) = Window();

        (await _repository.GetFullDayAbsencesAsync(Array.Empty<Guid>(), from, until)).ShouldBeEmpty();
    }
}
