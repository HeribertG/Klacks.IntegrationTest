// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// M6 (2026-10-08) on PostgreSQL: the wizards read the company membership window (Membership.ValidFrom / ValidUntil,
/// inclusive) and close every day outside it. Runs the production membership query against the real provider (the
/// in-memory provider cannot prove translation) and feeds it through the Wizard 1 agent snapshot. Every row is keyed
/// by the INTEGRATION_TEST_ prefix and removed again.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
public class MembershipWindowIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_MEMBERSHIP_";
    private static readonly DateOnly PeriodFrom = new(2099, 3, 13);
    private static readonly DateOnly PeriodUntil = new(2099, 3, 17);
    private static readonly DateOnly LastMemberDay = new(2099, 3, 15);

    private DataBaseContext _context = null!;

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }

    [Test]
    public async Task Reader_ReturnsTheInclusiveWindow_AndNoEntryForAClientWithoutMembership()
    {
        var (member, outsider) = await SeedAsync();

        var windows = await new MembershipWindowReader(_context).GetWindowsAsync([member, outsider], CancellationToken.None);

        windows[member].ShouldBe(new MembershipWindow(new DateOnly(2099, 1, 1), LastMemberDay));
        windows.ContainsKey(outsider).ShouldBeFalse("a client without membership row stays unrestricted, like the schedule view");
    }

    [Test]
    public async Task Wizard1Snapshot_ClosesEveryDayAfterTheExit()
    {
        var (member, _) = await SeedAsync();
        var contractProvider = Substitute.For<IClientContractDataProvider>();
        contractProvider
            .GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(ci =>
            {
                var result = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
                for (var date = ci.ArgAt<DateOnly>(1); date <= ci.ArgAt<DateOnly>(2); date = date.AddDays(1))
                {
                    result[date] = new Dictionary<Guid, EffectiveContractData> { [member] = AllWeekContract() };
                }

                return result;
            });

        var snapshot = await new WizardAgentSnapshotBuilder(contractProvider, new MembershipWindowReader(_context))
            .BuildAsync([member], PeriodFrom, PeriodUntil, PeriodFrom, PeriodUntil, new Dictionary<Guid, double>(), CancellationToken.None);

        snapshot.ContractDays.Where(d => d.WorksOnDay).Select(d => d.Date)
            .ShouldBe([PeriodFrom, PeriodFrom.AddDays(1), LastMemberDay]);
    }

    private async Task<(Guid Member, Guid Outsider)> SeedAsync()
    {
        var member = new Client { Id = Guid.NewGuid(), Name = TestPrefix + "Member", FirstName = "Test", Company = string.Empty };
        var outsider = new Client { Id = Guid.NewGuid(), Name = TestPrefix + "Outsider", FirstName = "Test", Company = string.Empty };
        _context.Client.AddRange(member, outsider);
        _context.Membership.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = member.Id,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = LastMemberDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (member.Id, outsider.Id);
    }

    private static EffectiveContractData AllWeekContract() => new()
    {
        HasActiveContract = true,
        ContractId = Guid.NewGuid(),
        FullTime = 160,
        GuaranteedHours = 120,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
        PerformsShiftWork = true,
    };
}
