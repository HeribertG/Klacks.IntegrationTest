// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration test for the WP-P4.1 partial unique indexes against the real database: a soft-deleted
/// and an active row with the same (client, qualification) / (shift, qualification) key must coexist,
/// while two ACTIVE rows with the same key must violate the index. This is the whole point of the
/// "WHERE is_deleted = false" filter and cannot be proven on the in-memory provider.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.Qualifications;

[TestFixture]
[Category("RealDatabase")]
public class QualificationPartialIndexTests
{
    private const string TestPrefix = "INTEGRATION_TEST_QUAL_";

    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public void SetUp() => _context = NewContext();

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
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
            DELETE FROM client_qualification WHERE qualification_id IN (SELECT id FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%');
            DELETE FROM shift_required_qualification WHERE qualification_id IN (SELECT id FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%');
            DELETE FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Qualification> CreateQualificationAsync()
    {
        var q = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = TestPrefix + "FORKLIFT" } };
        await _context.Set<Qualification>().AddAsync(q);
        await _context.SaveChangesAsync();
        return q;
    }

    private async Task<Client> CreateClientAsync()
    {
        var c = new Client { Id = Guid.NewGuid(), Name = TestPrefix + "CLIENT", FirstName = "Test", Company = string.Empty, LegalEntity = false };
        await _context.Set<Client>().AddAsync(c);
        await _context.SaveChangesAsync();
        return c;
    }

    private async Task<Shift> CreateShiftAsync()
    {
        var s = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TST",
            Description = "Qual index test",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        };
        await _context.Shift.AddAsync(s);
        await _context.SaveChangesAsync();
        return s;
    }

    [Test]
    public async Task ClientQualification_SoftDeletedAndActive_SameKey_Coexist()
    {
        var client = await CreateClientAsync();
        var qual = await CreateQualificationAsync();

        var first = new ClientQualification
        {
            Id = Guid.NewGuid(), ClientId = client.Id, QualificationId = qual.Id, Level = QualificationLevel.Basic
        };
        await _context.Set<ClientQualification>().AddAsync(first);
        await _context.SaveChangesAsync();

        first.IsDeleted = true;
        await _context.SaveChangesAsync();

        var replacement = new ClientQualification
        {
            Id = Guid.NewGuid(), ClientId = client.Id, QualificationId = qual.Id, Level = QualificationLevel.Expert
        };
        await _context.Set<ClientQualification>().AddAsync(replacement);

        await Should.NotThrowAsync(async () => await _context.SaveChangesAsync());
    }

    [Test]
    public async Task ClientQualification_TwoActive_SameKey_Violates()
    {
        var client = await CreateClientAsync();
        var qual = await CreateQualificationAsync();

        await _context.Set<ClientQualification>().AddAsync(new ClientQualification
        {
            Id = Guid.NewGuid(), ClientId = client.Id, QualificationId = qual.Id, Level = QualificationLevel.Basic
        });
        await _context.SaveChangesAsync();

        await _context.Set<ClientQualification>().AddAsync(new ClientQualification
        {
            Id = Guid.NewGuid(), ClientId = client.Id, QualificationId = qual.Id, Level = QualificationLevel.Advanced
        });

        await Should.ThrowAsync<DbUpdateException>(async () => await _context.SaveChangesAsync());
    }

    [Test]
    public async Task ShiftRequiredQualification_TwoActive_SameKey_Violates()
    {
        var shift = await CreateShiftAsync();
        var qual = await CreateQualificationAsync();

        await _context.Set<ShiftRequiredQualification>().AddAsync(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = shift.Id, QualificationId = qual.Id, IsMandatory = true, MinLevel = QualificationLevel.Proficient
        });
        await _context.SaveChangesAsync();

        await _context.Set<ShiftRequiredQualification>().AddAsync(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(), ShiftId = shift.Id, QualificationId = qual.Id, IsMandatory = false, MinLevel = QualificationLevel.Low
        });

        await Should.ThrowAsync<DbUpdateException>(async () => await _context.SaveChangesAsync());
    }
}
