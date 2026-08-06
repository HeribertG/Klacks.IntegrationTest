// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The batched sealed-day guard answers a whole bulk insert in two queries instead of two per pair, and
/// the group-scoped half of it is a three-table join (SealedDay to Work to GroupItem) that only a real
/// database executes. These tests hold that join to the same verdicts the per-pair check gives: a global
/// seal closes the date for everyone, a group seal closes it only for people whose work that day belongs
/// to that group, and an unsealed date stays open.
/// Cleanup deletes ONLY rows carrying the INTEGRATION_TEST_ prefix - this database is shared with the
/// running dev application.
/// </summary>

using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
[Category("RealDatabase")]
public class SealedDayLockedPairsIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_LOCKEDPAIRS_";

    private static readonly DateOnly SealedDate = new(2031, 3, 10);
    private static readonly DateOnly OpenDate = new(2031, 3, 11);

    private DataBaseContext _context = null!;
    private SealedDayRepository _repository = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp()
    {
        _context = NewContext();
        _repository = new SealedDayRepository(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task GetLockedPairsAsync_NothingSealed_ReturnsNothing()
    {
        var client = await CreateClientAsync("A");

        var locked = await _repository.GetLockedPairsAsync([(SealedDate, client.Id)]);

        locked.ShouldBeEmpty();
    }

    [Test]
    public async Task GetLockedPairsAsync_GlobalSeal_ClosesTheDateForEveryone()
    {
        var first = await CreateClientAsync("A");
        var second = await CreateClientAsync("B");
        await CreateSealedDayAsync(SealedDate, groupId: null);

        var locked = await _repository.GetLockedPairsAsync(
            [(SealedDate, first.Id), (SealedDate, second.Id), (OpenDate, first.Id)]);

        locked.ShouldBe(new HashSet<(DateOnly, Guid)>
        {
            (SealedDate, first.Id),
            (SealedDate, second.Id),
        });
    }

    [Test]
    public async Task GetLockedPairsAsync_GroupSeal_ClosesOnlyThoseWorkingForThatGroup()
    {
        var inGroup = await CreateClientAsync("IN");
        var outsideGroup = await CreateClientAsync("OUT");
        var groupId = await CreateGroupAsync();
        var shift = await CreateShiftAsync();
        // A second shift that is NOT a member of the sealed group - that is what puts its worker outside.
        var otherShift = await CreateShiftAsync("OTHER");
        await CreateGroupItemAsync(groupId, shift.Id);
        await CreateWorkAsync(inGroup.Id, shift.Id, SealedDate);
        await CreateWorkAsync(outsideGroup.Id, otherShift.Id, SealedDate);
        await CreateSealedDayAsync(SealedDate, groupId);

        var locked = await _repository.GetLockedPairsAsync(
            [(SealedDate, inGroup.Id), (SealedDate, outsideGroup.Id)]);

        locked.ShouldContain((SealedDate, inGroup.Id));
        locked.ShouldNotContain((SealedDate, outsideGroup.Id));
    }

    [Test]
    public async Task GetLockedPairsAsync_GroupSeal_IgnoresScenarioWork()
    {
        // A scenario copy is not the real plan; treating it as one would seal a day for somebody who
        // has no real work on it at all.
        var client = await CreateClientAsync("SCENARIO");
        var groupId = await CreateGroupAsync();
        var shift = await CreateShiftAsync();
        await CreateGroupItemAsync(groupId, shift.Id);
        await CreateWorkAsync(client.Id, shift.Id, SealedDate, analyseToken: Guid.NewGuid());
        await CreateSealedDayAsync(SealedDate, groupId);

        var locked = await _repository.GetLockedPairsAsync([(SealedDate, client.Id)]);

        locked.ShouldBeEmpty();
    }

    [Test]
    public async Task GetLockedPairsAsync_AgreesWithThePerPairCheck()
    {
        // The batch exists only to save queries; the moment its verdict differs from IsDayLockedAsync
        // the bulk insert and the single insert disagree about the same day.
        var client = await CreateClientAsync("PARITY");
        var groupId = await CreateGroupAsync();
        var shift = await CreateShiftAsync();
        await CreateGroupItemAsync(groupId, shift.Id);
        await CreateWorkAsync(client.Id, shift.Id, SealedDate);
        await CreateSealedDayAsync(SealedDate, groupId);

        var pairs = new (DateOnly Date, Guid ClientId)[] { (SealedDate, client.Id), (OpenDate, client.Id) };
        var locked = await _repository.GetLockedPairsAsync(pairs);

        foreach (var pair in pairs)
        {
            var perPair = await _repository.IsDayLockedAsync(pair.Date, pair.ClientId);
            locked.Contains(pair).ShouldBe(perPair, $"{pair.Date:yyyy-MM-dd} / {pair.ClientId}");
        }
    }

    [Test]
    public async Task GetLockedPairsAsync_EmptyInput_ReturnsNothing()
    {
        var locked = await _repository.GetLockedPairsAsync([]);

        locked.ShouldBeEmpty();
    }

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
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%')
                OR shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            DELETE FROM sealed_day WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%')
                OR sealed_by LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync(string suffix)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false,
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Guid> CreateGroupAsync()
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "GROUP",
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        await _context.Set<Group>().AddAsync(group);
        await _context.SaveChangesAsync();
        return group.Id;
    }

    private async Task<Shift> CreateShiftAsync(string suffix = "SHIFT")
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "LP",
            Description = "Integration test locked pairs",
            FromDate = new DateOnly(2020, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
        };
        await _context.Set<Shift>().AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    private async Task CreateGroupItemAsync(Guid groupId, Guid shiftId)
    {
        await _context.Set<GroupItem>().AddAsync(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftId,
        });
        await _context.SaveChangesAsync();
    }

    private async Task CreateWorkAsync(Guid clientId, Guid shiftId, DateOnly date, Guid? analyseToken = null)
    {
        await _context.Set<Work>().AddAsync(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            AnalyseToken = analyseToken,
        });
        await _context.SaveChangesAsync();
    }

    private async Task CreateSealedDayAsync(DateOnly date, Guid? groupId)
    {
        await _context.Set<SealedDay>().AddAsync(new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = date,
            GroupId = groupId,
            SealedBy = TestPrefix + "SEALER",
        });
        await _context.SaveChangesAsync();
    }
}
