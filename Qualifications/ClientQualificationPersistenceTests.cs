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

namespace Klacks.IntegrationTest.Qualifications;

[TestFixture]
[Category("RealDatabase")]
public class ClientQualificationPersistenceTests
{
    private const string TestPrefix = "INTEGRATION_TEST_CLIENTQUAL_";

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
            DELETE FROM client_qualification WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM qualification WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Client> CreateClientAsync()
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Client>().AddAsync(client);
        await _context.SaveChangesAsync();
        return client;
    }

    private async Task<Qualification> CreateQualificationAsync()
    {
        var qualification = new Qualification
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = TestPrefix + "FIRSTAID" }
        };
        await _context.Set<Qualification>().AddAsync(qualification);
        await _context.SaveChangesAsync();
        return qualification;
    }

    [Test]
    public async Task ClientQualification_Persists_AllFields()
    {
        var client = await CreateClientAsync();
        var qualification = await CreateQualificationAsync();
        var validFrom = new DateOnly(2025, 1, 1);
        var validUntil = new DateOnly(2026, 12, 31);
        const string note = "Certificate renewed in 2025";

        var clientQualification = new ClientQualification
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            QualificationId = qualification.Id,
            Level = QualificationLevel.Advanced,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Note = note
        };
        await _context.Set<ClientQualification>().AddAsync(clientQualification);
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var reloaded = await _context.Set<ClientQualification>()
            .FirstAsync(cq => cq.Id == clientQualification.Id);

        reloaded.ClientId.ShouldBe(client.Id);
        reloaded.QualificationId.ShouldBe(qualification.Id);
        reloaded.Level.ShouldBe(QualificationLevel.Advanced);
        reloaded.ValidFrom.ShouldBe(validFrom);
        reloaded.ValidUntil.ShouldBe(validUntil);
        reloaded.Note.ShouldBe(note);
    }

    [Test]
    public async Task ClientQualification_LoadedViaClientNavigation_WhenIncluded()
    {
        var client = await CreateClientAsync();
        var qualification = await CreateQualificationAsync();
        await _context.Set<ClientQualification>().AddAsync(new ClientQualification
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            QualificationId = qualification.Id,
            Level = QualificationLevel.Basic
        });
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var reloaded = await _context.Client
            .Include(c => c.Qualifications)
            .AsNoTracking()
            .FirstAsync(c => c.Id == client.Id);

        reloaded.Qualifications.Count.ShouldBe(1);
        reloaded.Qualifications.First().QualificationId.ShouldBe(qualification.Id);
    }

    [Test]
    public async Task ClientQualification_Update_Level_Persists()
    {
        var client = await CreateClientAsync();
        var qualification = await CreateQualificationAsync();
        var clientQualification = new ClientQualification
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            QualificationId = qualification.Id,
            Level = QualificationLevel.Basic
        };
        await _context.Set<ClientQualification>().AddAsync(clientQualification);
        await _context.SaveChangesAsync();

        clientQualification.Level = QualificationLevel.Expert;
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var reloaded = await _context.Set<ClientQualification>()
            .FirstAsync(cq => cq.Id == clientQualification.Id);
        reloaded.Level.ShouldBe(QualificationLevel.Expert);
    }

    [Test]
    public async Task ClientQualification_SoftDeleted_ExcludedByQueryFilter()
    {
        var client = await CreateClientAsync();
        var qualification = await CreateQualificationAsync();
        var clientQualification = new ClientQualification
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            QualificationId = qualification.Id,
            Level = QualificationLevel.Basic
        };
        await _context.Set<ClientQualification>().AddAsync(clientQualification);
        await _context.SaveChangesAsync();

        clientQualification.IsDeleted = true;
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var active = await _context.Set<ClientQualification>()
            .Where(cq => cq.Id == clientQualification.Id)
            .ToListAsync();
        active.ShouldBeEmpty();

        var includingDeleted = await _context.Set<ClientQualification>()
            .IgnoreQueryFilters()
            .Where(cq => cq.Id == clientQualification.Id)
            .ToListAsync();
        includingDeleted.Count.ShouldBe(1);
    }
}
