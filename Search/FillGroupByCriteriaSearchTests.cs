// Copyright (c) Heribert Gasparoli Private. All rights reserved.

// Verifies the contract filter added to ClientSearchRepository.SearchAsync against a real PostgreSQL
// database: the EF 'Any' subquery translates and executes, narrows the result set to active contract
// holders, and every returned client actually holds the requested active contract.

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
    private const string TargetCanton = "BE";
    private const string TargetContractName = "Vollzeit 180 BE";

    private DataBaseContext _context = null!;
    private ClientSearchRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new ClientSearchRepository(_context, Substitute.For<IClientGroupFilterService>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
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
