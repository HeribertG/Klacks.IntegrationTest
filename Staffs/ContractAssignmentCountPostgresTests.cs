// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// PostgreSQL tests for ContractRepository.CountActiveOrFutureAssignmentsAsync, the count behind the refusal to
/// delete a contract that is still in use: open-ended and future assignments count, an assignment that ended before
/// the company's day does not, an assignment ending on that day still counts, and deleted assignments or assignments
/// of deleted clients are ignored.
/// Connection: TestHostDatabase (localhost:5434, database klacks). Fixture rows carry the INTEGRATION_TEST_ prefix.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Staffs;

[TestFixture]
[Category("RealDatabase")]
public class ContractAssignmentCountPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_";
    private const string ClientNamePrefix = Prefix + "ContractUse";
    private static readonly DateOnly Today = new(2026, 10, 26);

    private DataBaseContext _context = null!;
    private ContractRepository _repository = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _repository = new ContractRepository(_context, NullLogger<Contract>.Instance);
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task OpenEndedAndFutureAssignmentsCount_EndedOnesDoNot()
    {
        var contract = await AddContractAsync();
        await AddAssignmentAsync(contract.Id, Today.AddYears(-1), until: null);
        await AddAssignmentAsync(contract.Id, Today.AddDays(30), until: null);
        await AddAssignmentAsync(contract.Id, Today.AddYears(-2), until: Today.AddDays(-1));

        var count = await _repository.CountActiveOrFutureAssignmentsAsync(contract.Id, Today);

        count.ShouldBe(2);
    }

    [Test]
    public async Task AssignmentEndingOnTheCompanyDayStillCounts()
    {
        var contract = await AddContractAsync();
        await AddAssignmentAsync(contract.Id, Today.AddYears(-1), until: Today);

        var count = await _repository.CountActiveOrFutureAssignmentsAsync(contract.Id, Today);

        count.ShouldBe(1);
    }

    [Test]
    public async Task OnlyEndedAssignments_AreNotInUse()
    {
        var contract = await AddContractAsync();
        await AddAssignmentAsync(contract.Id, Today.AddYears(-3), until: Today.AddYears(-1));

        var count = await _repository.CountActiveOrFutureAssignmentsAsync(contract.Id, Today);

        count.ShouldBe(0);
    }

    [Test]
    public async Task DeletedAssignmentsAndAssignmentsOfDeletedClients_AreIgnored()
    {
        var contract = await AddContractAsync();
        await AddAssignmentAsync(contract.Id, Today.AddYears(-1), until: null, assignmentDeleted: true);
        await AddAssignmentAsync(contract.Id, Today.AddYears(-1), until: null, clientDeleted: true);

        var count = await _repository.CountActiveOrFutureAssignmentsAsync(contract.Id, Today);

        count.ShouldBe(0);
    }

    [Test]
    public async Task AssignmentsOfAnotherContract_AreNotCounted()
    {
        var contract = await AddContractAsync();
        var other = await AddContractAsync();
        await AddAssignmentAsync(other.Id, Today.AddYears(-1), until: null);

        var count = await _repository.CountActiveOrFutureAssignmentsAsync(contract.Id, Today);

        count.ShouldBe(0);
    }

    private async Task<Contract> AddContractAsync()
    {
        var contract = new Contract { Id = Guid.NewGuid(), Name = Prefix + "Contract " + Guid.NewGuid().ToString("N") };
        _context.Contract.Add(contract);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return contract;
    }

    private async Task AddAssignmentAsync(
        Guid contractId,
        DateOnly from,
        DateOnly? until,
        bool assignmentDeleted = false,
        bool clientDeleted = false)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            FirstName = "Contract",
            Name = ClientNamePrefix,
            Type = EntityTypeEnum.Employee,
            Gender = GenderEnum.Female
        };
        var assignment = new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ContractId = contractId,
            FromDate = from,
            UntilDate = until,
            IsActive = until == null && from <= Today
        };
        _context.Client.Add(client);
        _context.ClientContract.Add(assignment);
        await _context.SaveChangesAsync();

        if (assignmentDeleted)
        {
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE client_contract SET is_deleted = true WHERE id = {assignment.Id}");
        }

        if (clientDeleted)
        {
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE client SET is_deleted = true WHERE id = {client.Id}");
        }

        _context.ChangeTracker.Clear();
    }

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{ClientNamePrefix}'))");
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM client WHERE starts_with(name, '{ClientNamePrefix}')");
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM contract WHERE starts_with(name, '{Prefix}Contract ')");
        _context.ChangeTracker.Clear();
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }
}
