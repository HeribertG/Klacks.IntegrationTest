// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The background optimiser spends minutes on a candidate and then asks the fingerprint whether the plan
/// it started from still exists. Two things about that only a real database can answer: whether the
/// repeatable-read transaction actually opens against PostgreSQL, and whether the newest-timestamp part
/// survives the round trip through timestamptz columns - an in-memory provider stores DateTime as handed
/// over and would hide a kind or precision problem.
/// Cleanup deletes ONLY rows carrying the INTEGRATION_TEST_ prefix - this database is shared with the
/// running dev application.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class Wizard4SnapshotGuardIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_W4GUARD_";

    private static readonly DateOnly From = new(2031, 4, 1);
    private static readonly DateOnly Until = new(2031, 4, 30);

    private DataBaseContext _context = null!;
    private Wizard4SnapshotGuard _guard = null!;
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
        _guard = new Wizard4SnapshotGuard(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ExecuteInSnapshotAsync_OpensAndCommitsARepeatableReadTransaction()
    {
        var client = await CreateClientAsync("TX");
        var shift = await CreateShiftAsync();
        await CreateWorkAsync(client.Id, shift.Id, From);

        var fingerprint = await _guard.ExecuteInSnapshotAsync(
            () => _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None),
            CancellationToken.None);

        fingerprint.WorkCount.ShouldBe(1);
        // The transaction has to be gone afterwards, otherwise the next read of this scope would hang
        // on a connection that still believes it is inside a snapshot.
        _context.Database.CurrentTransaction.ShouldBeNull();
    }

    [Test]
    public async Task ComputeFingerprintAsync_UnchangedPlan_IsStableAcrossReads()
    {
        var client = await CreateClientAsync("STABLE");
        var shift = await CreateShiftAsync();
        await CreateWorkAsync(client.Id, shift.Id, From);

        var first = await _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None);
        var second = await _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None);

        second.ShouldBe(first);
        first.MaxTimestamp.ShouldNotBeNull();
    }

    [Test]
    public async Task ComputeFingerprintAsync_AddedWork_ChangesTheFingerprint()
    {
        var client = await CreateClientAsync("ADD");
        var shift = await CreateShiftAsync();
        await CreateWorkAsync(client.Id, shift.Id, From);
        var before = await _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None);

        await CreateWorkAsync(client.Id, shift.Id, From.AddDays(1));
        var after = await _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None);

        after.ShouldNotBe(before);
        after.WorkCount.ShouldBe(before.WorkCount + 1);
    }

    [Test]
    public async Task ComputeFingerprintAsync_EditKeepingTheCount_ChangesTheTimestamp()
    {
        // The case counts alone cannot see: somebody moved a shift without adding or removing one.
        var client = await CreateClientAsync("EDIT");
        var shift = await CreateShiftAsync();
        var work = await CreateWorkAsync(client.Id, shift.Id, From);
        var before = await _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None);

        work.UpdateTime = DateTime.UtcNow.AddMinutes(5);
        await _context.SaveChangesAsync();
        var after = await _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None);

        after.WorkCount.ShouldBe(before.WorkCount);
        after.ShouldNotBe(before);
    }

    [Test]
    public async Task ComputeFingerprintAsync_ScenarioWork_IsNotPartOfThePlan()
    {
        var client = await CreateClientAsync("SCENARIO");
        var shift = await CreateShiftAsync();
        await CreateWorkAsync(client.Id, shift.Id, From, analyseToken: Guid.NewGuid());

        var fingerprint = await _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None);

        fingerprint.WorkCount.ShouldBe(0);
        fingerprint.MaxTimestamp.ShouldBeNull();
    }

    [Test]
    public async Task ComputeFingerprintAsync_WorkOutsideThePeriod_IsIgnored()
    {
        var client = await CreateClientAsync("OUTSIDE");
        var shift = await CreateShiftAsync();
        await CreateWorkAsync(client.Id, shift.Id, From.AddDays(-1));
        await CreateWorkAsync(client.Id, shift.Id, Until.AddDays(1));

        var fingerprint = await _guard.ComputeFingerprintAsync([client.Id], From, Until, CancellationToken.None);

        fingerprint.WorkCount.ShouldBe(0);
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
            DELETE FROM break WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
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

    private async Task<Shift> CreateShiftAsync()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "W4",
            Description = "Integration test wizard4 snapshot guard",
            FromDate = new DateOnly(2020, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
        };
        await _context.Set<Shift>().AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    private async Task<Work> CreateWorkAsync(
        Guid clientId, Guid shiftId, DateOnly date, Guid? analyseToken = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            AnalyseToken = analyseToken,
        };
        await _context.Set<Work>().AddAsync(work);
        await _context.SaveChangesAsync();
        return work;
    }
}
