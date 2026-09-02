// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

/// <summary>
/// Verifies the WorkList ClientId / ClientIds[] scope branches: single-client
/// refresh round-trips and bulk refresh round-trips must skip pagination so
/// the targeted employee(s) always surface regardless of where they sit in
/// the alphabetic 200-page window of the default list mode.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class WorkListClientScopeTests
{
    private const string TestPrefix = "INTEGRATION_TEST_WorkListScope_";

    private DataBaseContext _context = null!;
    private WorkRepository _repo = null!;

    private readonly List<Guid> _clientIds = new();
    private Guid _shiftId;

    private static readonly DateOnly StartDate = new DateOnly(2026, 6, 1);
    private static readonly DateOnly EndDate = new DateOnly(2026, 6, 30);

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

        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        groupFilterService
            .FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        var searchFilterService = Substitute.For<IClientSearchFilterService>();
        searchFilterService
            .ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Client>)args[0]);

        var baseQueryService = new ClientBaseQueryService(
            _context,
            groupFilterService,
            searchFilterService, new Klacks.Api.Domain.Services.Clients.ClientSearchService(), new Klacks.IntegrationTest.TestHelpers.EmptyClientFuzzySearchService());

        _repo = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            baseQueryService,
            Substitute.For<IWorkMacroService>(),
            Substitute.For<IClientContractDataProvider>());

        await SeedThreeClients();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.Work.Where(w => _clientIds.Contains(w.ClientId)).ExecuteDeleteAsync();
        await _context.Membership.Where(m => _clientIds.Contains(m.ClientId)).ExecuteDeleteAsync();
        await _context.Client.Where(c => _clientIds.Contains(c.Id)).ExecuteDeleteAsync();
        await _context.Shift.Where(s => s.Id == _shiftId).ExecuteDeleteAsync();
        _context.Dispose();
    }

    private async Task SeedThreeClients()
    {
        _clientIds.Clear();
        _shiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = TestPrefix + "shift",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsDeleted = false,
        });

        for (var i = 0; i < 3; i++)
        {
            var clientId = Guid.NewGuid();
            _clientIds.Add(clientId);
            _context.Client.Add(new Client
            {
                Id = clientId,
                Name = TestPrefix + $"client_{i}",
                FirstName = $"Probe{i}",
                Type = EntityTypeEnum.Employee,
                LegalEntity = false,
                IsDeleted = false,
            });
        }

        await _context.SaveChangesAsync();

        var validFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validUntil = new DateTime(2027, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        foreach (var clientId in _clientIds)
        {
            _context.Membership.Add(new Membership
            {
                ClientId = clientId,
                ValidFrom = validFrom,
                ValidUntil = validUntil,
            });
            _context.Work.Add(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ShiftId = _shiftId,
                CurrentDate = new DateOnly(2026, 6, 15),
                WorkTime = 480,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(16, 0),
                IsDeleted = false,
            });
        }
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task WorkList_WithClientIdScope_ReturnsOnlyThatClientAndSkipsPagination()
    {
        var target = _clientIds[1];
        var filter = new WorkFilter
        {
            StartDate = StartDate,
            EndDate = EndDate,
            ClientId = target,
            RowCount = 1,
            StartRow = 9999,
            ShowEmployees = true,
            ShowExtern = true,
        };

        var (clients, totalCount) = await _repo.WorkList(filter);

        clients.Count.ShouldBe(1, "ClientId scope must skip the pagination window.");
        clients[0].Id.ShouldBe(target);
        totalCount.ShouldBe(1);
    }

    [Test]
    public async Task WorkList_WithClientIdsBulkScope_ReturnsAllListedClientsInOneCall()
    {
        var targets = new List<Guid> { _clientIds[0], _clientIds[2] };
        var filter = new WorkFilter
        {
            StartDate = StartDate,
            EndDate = EndDate,
            ClientIds = targets,
            RowCount = 1,
            StartRow = 9999,
        };

        var (clients, totalCount) = await _repo.WorkList(filter);

        clients.Count.ShouldBe(2, "ClientIds[] scope must return all listed clients and ignore pagination.");
        clients.Select(c => c.Id).ShouldBeSubsetOf(targets);
        clients.Select(c => c.Id).ShouldContain(_clientIds[0]);
        clients.Select(c => c.Id).ShouldContain(_clientIds[2]);
        clients.Select(c => c.Id).ShouldNotContain(_clientIds[1]);
        totalCount.ShouldBe(2);
    }

    [Test]
    public async Task WorkList_WithClientIdsScope_TakesPrecedenceOverClientId()
    {
        var bulkTargets = new List<Guid> { _clientIds[0], _clientIds[1] };
        var filter = new WorkFilter
        {
            StartDate = StartDate,
            EndDate = EndDate,
            ClientId = _clientIds[2],
            ClientIds = bulkTargets,
        };

        var (clients, _) = await _repo.WorkList(filter);

        clients.Count.ShouldBe(2);
        clients.Select(c => c.Id).ShouldNotContain(_clientIds[2],
            "ClientIds[] (bulk) must take precedence over ClientId (single) when both are present.");
    }
}
