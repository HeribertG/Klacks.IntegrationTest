// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Stand-in clients against PostgreSQL (port 5434): the Wizard-4 agent resolver plans them, the replacement person
/// list leaves them out like the replacement write guard, and the contract data provider gives a contractless
/// stand-in no target hours while a contractless employee keeps the settings defaults. Every row carries the prefix
/// INTEGRATION_TEST_StandIn_ and only those rows are removed.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Clients;

[TestFixture]
public class StandInClientTypeNpgsqlTests
{
    private const string TestPrefix = "INTEGRATION_TEST_StandIn_";

    private static readonly DateOnly PeriodFrom = new(2026, 11, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 11, 30);

    private DataBaseContext _context = null!;
    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _employeeId = Guid.NewGuid();
    private readonly Guid _standInId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();

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

        await CleanupTestDataAsync();
        await SeedTestDataAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataAsync();
        _context.Dispose();
    }

    [Test]
    public async Task Wizard4Agents_IncludeTheStandIn_LeaveTheCustomerOut()
    {
        var resolved = await new Wizard4AgentResolver(_context)
            .ResolveAsync(_groupId, PeriodFrom, PeriodUntil, CancellationToken.None);

        resolved.ShouldBe([_employeeId, _standInId], ignoreOrder: true);
    }

    [Test]
    public async Task ClientsForReplacement_ExcludeTheStandIn_LikeTheWriteGuard()
    {
        var groupFilter = Substitute.For<IClientGroupFilterService>();
        groupFilter.FilterClientsByGroupId(null, Arg.Any<IQueryable<Client>>())
            .Returns(callInfo => Task.FromResult(callInfo.ArgAt<IQueryable<Client>>(1)));
        var repository = new ClientSearchRepository(
            _context, groupFilter, Substitute.For<IClientFuzzySearchService>(), TestCompanyClock.Utc());

        var ids = (await repository.GetClientsForReplacement()).Select(client => client.Id).ToList();

        ids.ShouldContain(_employeeId);
        ids.ShouldNotContain(_standInId);
        ids.ShouldNotContain(_customerId);
    }

    [Test]
    public async Task StandIn_EffectiveContract_OnNpgsql_HasNoTargetHours_TheEmployeeKeepsTheDefaults()
    {
        var provider = new ClientContractDataProvider(
            _context, NullLogger<ClientContractDataProvider>.Instance, new SettingsChangeVersion());
        var unknownId = Guid.NewGuid();

        var single = await provider.GetEffectiveContractDataForClientsAsync([_standInId, _employeeId, unknownId], PeriodFrom);
        var range = await provider.GetEffectiveContractDataForClientsRangeAsync([_standInId, _employeeId], PeriodFrom, PeriodFrom.AddDays(1));

        single[_employeeId].GuaranteedHours.ShouldBeGreaterThan(0m);
        single[_employeeId].GuaranteedHours.ShouldBe(single[unknownId].GuaranteedHours);
        single[_employeeId].MinimumHours.ShouldBe(single[unknownId].MinimumHours);
        single[_standInId].GuaranteedHours.ShouldBe(0m);
        single[_standInId].MinimumHours.ShouldBe(0m);
        single[_standInId].MaxWeeklyHours.ShouldBe(single[_employeeId].MaxWeeklyHours);
        single[_standInId].MinPauseHours.ShouldBe(single[_employeeId].MinPauseHours);
        foreach (var perDay in range.Values)
        {
            perDay[_standInId].GuaranteedHours.ShouldBe(0m);
            perDay[_standInId].MinimumHours.ShouldBe(0m);
            perDay[_employeeId].GuaranteedHours.ShouldBe(single[_employeeId].GuaranteedHours);
        }
    }

    private async Task SeedTestDataAsync()
    {
        _context.Group.Add(new Group
        {
            Id = _groupId,
            Name = TestPrefix + "Group",
            Description = "Integration test stand-in group",
            ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        _context.Client.AddRange(
            NewClient(_employeeId, "Employee", EntityTypeEnum.Employee),
            NewClient(_standInId, "Placeholder", EntityTypeEnum.StandIn),
            NewClient(_customerId, "Customer", EntityTypeEnum.Customer));
        await _context.SaveChangesAsync();

        _context.GroupItem.AddRange(
            new GroupItem { Id = Guid.NewGuid(), ClientId = _employeeId, GroupId = _groupId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _standInId, GroupId = _groupId },
            new GroupItem { Id = Guid.NewGuid(), ClientId = _customerId, GroupId = _groupId });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static Client NewClient(Guid id, string suffix, EntityTypeEnum type) => new()
    {
        Id = id,
        Name = TestPrefix + suffix,
        FirstName = type == EntityTypeEnum.StandIn ? string.Empty : "StandInTest",
        Type = type,
        Gender = GenderEnum.Female,
        LegalEntity = false
    };

    private async Task CleanupTestDataAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE name LIKE {0})",
            TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE name LIKE {0}", TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE name LIKE {0}", TestPrefix + "%");
    }
}
