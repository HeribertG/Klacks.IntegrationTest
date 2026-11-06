// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proof for KeywordWishCountService, whose queries the unit tests only run on the in-memory provider: the
/// schedule commands of the employees (ScheduleCommandRepository.GetByClientsAndDateRangeAsync), the absences without a
/// parent work, the top-level works, each filtered by the exact plan token (null = real plan), and the batched display
/// names (ClientRepository.GetDisplayNamesAsync). Every row carries an INTEGRATION_TEST_ prefixed key or belongs to such a
/// client; the cleanup deletes nothing else.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Staffs;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class KeywordWishCountNpgsqlTests
{
    private const string TestPrefix = "INTEGRATION_TEST_KWCOUNT_";
    private const string FirstName = "Test";
    private const string Free = "FREE";
    private const string NotFree = "-FREE";
    private const string Late = "LATE";
    private const string BrokenFreeOutcome = "BrokenFree";
    private const string UnfulfilledOutcome = "Unfulfilled";

    private static readonly DateOnly From = new(2099, 9, 6);
    private static readonly DateOnly Until = new(2099, 9, 12);
    private static readonly DateOnly FreeDay = new(2099, 9, 7);
    private static readonly DateOnly AbsenceDay = new(2099, 9, 8);
    private static readonly DateOnly LateDay = new(2099, 9, 9);

    private static readonly ScheduleCommandKeywordSet Keywords = new()
    {
        FreeToken = Free,
        NegFreeToken = NotFree,
        EarlyToken = "EARLY",
        NegEarlyToken = "-EARLY",
        LateToken = Late,
        NegLateToken = "-LATE",
        NightToken = "NIGHT",
        NegNightToken = "-NIGHT",
    };

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private KeywordWishCountService _sut = null!;
    private readonly Guid _token = Guid.NewGuid();
    private Guid _clientA;
    private Guid _clientB;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _clientA = Guid.NewGuid();
        _clientB = Guid.NewGuid();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TKW",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(7, 0),
            EndShift = new TimeOnly(15, 0),
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        };
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = TestPrefix + "ABSENCE" },
            Abbreviation = new MultiLanguage { De = "TKW" },
            Description = new MultiLanguage { De = "keyword wish count absence" },
            Color = "#cccccc"
        };
        _context.Shift.Add(shift);
        _context.Set<Absence>().Add(absence);
        _context.Client.AddRange(
            new Client { Id = _clientA, Name = TestPrefix + "A", FirstName = FirstName, Company = string.Empty },
            new Client { Id = _clientB, Name = TestPrefix + "B", FirstName = FirstName, Company = string.Empty });

        _context.Set<ScheduleCommand>().AddRange(
            Command(_clientA, FreeDay, Free, null),
            Command(_clientA, AbsenceDay, NotFree, null),
            Command(_clientA, LateDay, Late, null),
            Command(_clientB, FreeDay, Free, _token));
        _context.Work.AddRange(
            EarlyWork(_clientA, shift.Id, FreeDay, null),
            EarlyWork(_clientB, shift.Id, FreeDay, _token),
            EarlyWork(_clientB, shift.Id, LateDay, null));
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _clientA,
            AbsenceId = absence.Id,
            CurrentDate = AbsenceDay,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            WorkTime = 8m
        });
        await _context.SaveChangesAsync();

        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(Keywords);
        var clientRepository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            new EntityCollectionUpdateService(_context),
            Substitute.For<IClientValidator>(),
            Substitute.For<ILogger<ClientRepository>>());
        _sut = new KeywordWishCountService(
            _context,
            new ScheduleCommandRepository(_context, Substitute.For<ILogger<ScheduleCommand>>()),
            keywordProvider,
            clientRepository);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task CountAsync_RealPlan_CountsOnlyItsOwnRows_DropsTheWishOnTheAbsenceDay_AndNamesTheEmployee()
    {
        var wishes = await _sut.CountAsync([_clientA, _clientB], From, Until, analyseToken: null, CancellationToken.None);

        wishes.BrokenFree.ShouldBe(1, "A works on FREE; B's FREE belongs to the scenario");
        wishes.Unfulfilled.ShouldBe(1, "A's LATE has no shift; the -FREE on the absence day is dropped");
        wishes.Items.Select(i => (i.ClientId, i.Date, i.Outcome)).ShouldBe(
            [(_clientA, FreeDay, BrokenFreeOutcome), (_clientA, LateDay, UnfulfilledOutcome)],
            ignoreOrder: true);
        wishes.Items.ShouldAllBe(i => i.ClientName == FirstName + " " + TestPrefix + "A");
    }

    [Test]
    public async Task CountAsync_Scenario_CountsOnlyTheScenarioRows()
    {
        var wishes = await _sut.CountAsync([_clientA, _clientB], From, Until, _token, CancellationToken.None);

        wishes.BrokenFree.ShouldBe(1);
        wishes.Unfulfilled.ShouldBe(0, "the real plan's commands of A are not part of the scenario");
        var item = wishes.Items.ShouldHaveSingleItem();
        item.ClientId.ShouldBe(_clientB);
        item.Date.ShouldBe(FreeDay);
        item.ClientName.ShouldBe(FirstName + " " + TestPrefix + "B");
    }

    [Test]
    public async Task HasRestrictingWishes_ReadsTheCommandsOfThePlanAndRange()
    {
        (await _sut.HasRestrictingWishesAsync([_clientA], From, Until, null, CancellationToken.None)).ShouldBeTrue();
        (await _sut.HasRestrictingWishesAsync([_clientA], AbsenceDay, AbsenceDay, null, CancellationToken.None))
            .ShouldBeFalse("a -FREE alone restricts nothing");
        (await _sut.HasRestrictingWishesAsync([_clientB], From, Until, null, CancellationToken.None))
            .ShouldBeFalse("B's FREE belongs to the scenario");
        (await _sut.HasRestrictingWishesAsync([_clientB], From, Until, _token, CancellationToken.None)).ShouldBeTrue();
    }

    private static ScheduleCommand Command(Guid clientId, DateOnly date, string keyword, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        CurrentDate = date,
        CommandKeyword = keyword,
        AnalyseToken = token
    };

    private static Work EarlyWork(Guid clientId, Guid shiftId, DateOnly date, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = shiftId,
        CurrentDate = date,
        StartTime = new TimeOnly(7, 0),
        EndTime = new TimeOnly(15, 0),
        WorkTime = 8m,
        AnalyseToken = token
    };

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM schedule_commands WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM absence WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
