// Copyright (c) Heribert Gasparoli Private. All rights reserved.

// Verifies the contract filter added to ClientSearchRepository.SearchAsync against a real PostgreSQL
// database: the EF 'Any' subquery translates and executes, narrows the result set to active contract
// holders, and every returned client actually holds the requested active contract. The test seeds its
// own contract + canton-BE employees (prefixed, so the cleanup is key-scoped and never touches real
// data) instead of relying on ambient dev-seed rows, which do not exist in a fresh database.

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Search;

[TestFixture]
[Category("RealDatabase")]
public class FillGroupByCriteriaSearchTests
{
    private const string TestPrefix = "INTEGRATION_TEST_FILLGROUP_";
    private const string TargetCanton = "BE";
    private const string TargetContractName = TestPrefix + "Vollzeit 180 BE";

    private DataBaseContext _context = null!;
    private ClientSearchRepository _repository = null!;

    private readonly Guid _contractId = Guid.NewGuid();
    private readonly Guid _holderClientId = Guid.NewGuid();
    private readonly Guid _nonHolderClientId = Guid.NewGuid();

    [SetUp]
    public async Task Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        // SearchAsync scopes every result to the caller's visible groups. These tests cover the
        // criteria filters, not visibility, so the group filter passes the query through
        // unchanged — the substitute's default null return would NRE instead.
        var groupFilterService = Substitute.For<IClientGroupFilterService>();
        groupFilterService
            .FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>(), Arg.Any<bool>())
            .Returns(call => Task.FromResult((IQueryable<Client>)call[1]));

        _repository = new ClientSearchRepository(_context, groupFilterService, Substitute.For<IClientFuzzySearchService>());

        await CleanupTestDataAsync();
        await SeedTestDataAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataAsync();
        _context.Dispose();
    }

    private async Task SeedTestDataAsync()
    {
        _context.Contract.Add(new Contract
        {
            Id = _contractId,
            Name = TargetContractName,
            GuaranteedHours = 180
        });
        await _context.SaveChangesAsync();

        // A canton-BE employee that holds the target contract as an ACTIVE contract.
        var holder = new Client
        {
            Id = _holderClientId,
            Name = TestPrefix + "Holder",
            FirstName = "Contract",
            Type = EntityTypeEnum.Employee,
            Gender = GenderEnum.Female,
            LegalEntity = false,
            IsDeleted = false
        };
        holder.Addresses.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = _holderClientId,
            Street = TestPrefix + "Street 1",
            Zip = "3000",
            City = "Bern",
            Country = "CH",
            State = TargetCanton,
            Type = AddressTypeEnum.Employee,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        // A second canton-BE employee WITHOUT the contract, so the contract filter genuinely narrows.
        var nonHolder = new Client
        {
            Id = _nonHolderClientId,
            Name = TestPrefix + "NonHolder",
            FirstName = "NoContract",
            Type = EntityTypeEnum.Employee,
            Gender = GenderEnum.Male,
            LegalEntity = false,
            IsDeleted = false
        };
        nonHolder.Addresses.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = _nonHolderClientId,
            Street = TestPrefix + "Street 2",
            Zip = "3001",
            City = "Bern",
            Country = "CH",
            State = TargetCanton,
            Type = AddressTypeEnum.Employee,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        _context.Client.AddRange(holder, nonHolder);
        await _context.SaveChangesAsync();

        _context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _holderClientId,
            ContractId = _contractId,
            FromDate = new DateOnly(2020, 1, 1),
            UntilDate = null,
            IsActive = true
        });
        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestDataAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE name LIKE {0})",
            TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE name LIKE {0})",
            TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE name LIKE {0}", TestPrefix + "%");
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM contract WHERE name LIKE {0}", TestPrefix + "%");
    }

    [Test]
    public async Task SearchByCantonAndContract_ReturnsOnlyActiveContractHolders()
    {
        var contract = await _context.Set<Contract>()
            .FirstOrDefaultAsync(c => !c.IsDeleted && c.Name == TargetContractName);
        contract.ShouldNotBeNull($"Test data requires a contract named '{TargetContractName}'.");

        var result = await _repository.SearchAsync(
            canton: TargetCanton,
            entityType: EntityTypeEnum.Employee,
            contractId: contract!.Id,
            limit: 100);

        result.TotalCount.ShouldBeGreaterThan(0);
        result.Items.Count.ShouldBeGreaterThan(0);

        foreach (var item in result.Items)
        {
            var holdsContract = await _context.Set<ClientContract>().AnyAsync(cc =>
                cc.ClientId == item.Id && cc.ContractId == contract.Id && cc.IsActive && !cc.IsDeleted);
            holdsContract.ShouldBeTrue($"Client {item.Id} was returned but does not hold the active contract.");
        }
    }

    [Test]
    public async Task ContractFilter_NarrowsTheResultSet()
    {
        var contract = await _context.Set<Contract>()
            .FirstOrDefaultAsync(c => !c.IsDeleted && c.Name == TargetContractName);
        contract.ShouldNotBeNull();

        var withoutContract = await _repository.SearchAsync(
            canton: TargetCanton, entityType: EntityTypeEnum.Employee, contractId: null, limit: 1);

        var withContract = await _repository.SearchAsync(
            canton: TargetCanton, entityType: EntityTypeEnum.Employee, contractId: contract!.Id, limit: 1);

        withContract.TotalCount.ShouldBeGreaterThan(0);
        withContract.TotalCount.ShouldBeLessThanOrEqualTo(withoutContract.TotalCount);
    }
}
