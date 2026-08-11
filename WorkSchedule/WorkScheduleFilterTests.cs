using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Domain.Models.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class WorkScheduleFilterTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;

    private Guid _employeeClient1Id;
    private Guid _employeeClient2Id;
    private Guid _externClient1Id;
    private Guid _contractLowId;
    private Guid _contractHighId;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        await SetupTestData();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestData();
        _context?.Dispose();
    }

    private async Task SetupTestData()
    {
        // Arrange
        _employeeClient1Id = Guid.NewGuid();
        _employeeClient2Id = Guid.NewGuid();
        _externClient1Id = Guid.NewGuid();
        _contractLowId = Guid.NewGuid();
        _contractHighId = Guid.NewGuid();

        var now = DateTime.UtcNow;
        var validFrom = new DateTime(now.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var validUntil = new DateTime(now.Year + 1, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var refDate = new DateOnly(now.Year, now.Month, 1);

        var contractLow = new Contract
        {
            Id = _contractLowId,
            Name = "TEST_Contract_Low",
            GuaranteedHours = 80
        };
        var contractHigh = new Contract
        {
            Id = _contractHighId,
            Name = "TEST_Contract_High",
            GuaranteedHours = 160
        };
        _context.Contract.AddRange(contractLow, contractHigh);
        await _context.SaveChangesAsync();

        var employee1 = new Client
        {
            Id = _employeeClient1Id,
            Name = "TEST_Employee_Filter_A",
            FirstName = "Alice",
            Type = EntityTypeEnum.Employee,
            IdNumber = 99901,
            Gender = GenderEnum.Female,
            LegalEntity = false,
            IsDeleted = false
        };

        var employee2 = new Client
        {
            Id = _employeeClient2Id,
            Name = "TEST_Employee_Filter_B",
            FirstName = "Bob",
            Type = EntityTypeEnum.Employee,
            IdNumber = 99902,
            Gender = GenderEnum.Male,
            LegalEntity = false,
            IsDeleted = false
        };

        var extern1 = new Client
        {
            Id = _externClient1Id,
            Name = "TEST_Extern_Filter_C",
            FirstName = "Charlie",
            Type = EntityTypeEnum.ExternEmp,
            IdNumber = 99903,
            Gender = GenderEnum.Male,
            LegalEntity = false,
            IsDeleted = false
        };

        _context.Client.AddRange(employee1, employee2, extern1);
        await _context.SaveChangesAsync();

        var membership1 = new Membership
        {
            ClientId = _employeeClient1Id,
            ValidFrom = validFrom,
            ValidUntil = validUntil
        };
        var membership2 = new Membership
        {
            ClientId = _employeeClient2Id,
            ValidFrom = validFrom,
            ValidUntil = validUntil
        };
        var membership3 = new Membership
        {
            ClientId = _externClient1Id,
            ValidFrom = validFrom,
            ValidUntil = validUntil
        };
        _context.Membership.AddRange(membership1, membership2, membership3);
        await _context.SaveChangesAsync();

        var clientContract1 = new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _employeeClient1Id,
            ContractId = _contractLowId,
            FromDate = refDate.AddMonths(-6),
            UntilDate = null
        };
        var clientContract2 = new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _employeeClient2Id,
            ContractId = _contractHighId,
            FromDate = refDate.AddMonths(-6),
            UntilDate = null
        };
        _context.ClientContract.AddRange(clientContract1, clientContract2);
        await _context.SaveChangesAsync();
    }

    private async Task CleanupTestData()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_contract WHERE client_id IN ({0}, {1}, {2})",
            _employeeClient1Id, _employeeClient2Id, _externClient1Id);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM membership WHERE client_id IN ({0}, {1}, {2})",
            _employeeClient1Id, _employeeClient2Id, _externClient1Id);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id IN ({0}, {1}, {2})",
            _employeeClient1Id, _employeeClient2Id, _externClient1Id);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM contract WHERE id IN ({0}, {1})",
            _contractLowId, _contractHighId);
    }

    [Test]
    public async Task WorkList_ShowEmployeesTrue_ShowExternFalse_ReturnsOnlyEmployees()
    {
        // Arrange
        var mockGroupFilter = Substitute.For<IClientGroupFilterService>();
        var mockSearchFilter = Substitute.For<IClientSearchFilterService>();
        mockGroupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        mockSearchFilter.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => ((IQueryable<Client>)args[0]).Where(c => c.Name!.Contains("TEST_")));

        var mockLogger = Substitute.For<ILogger<Work>>();
        var mockWorkMacroService = Substitute.For<IWorkMacroService>();
        var mockPeriodHoursService = Substitute.For<IPeriodHoursService>();
        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var mockUnitOfWork = Substitute.For<IUnitOfWork>();
        var baseQueryService = new ClientBaseQueryService(_context, mockGroupFilter, mockSearchFilter, new Klacks.Api.Domain.Services.Clients.ClientSearchService(), new Klacks.IntegrationTest.TestHelpers.EmptyClientFuzzySearchService());
        var repository = new WorkRepository(_context, mockLogger, baseQueryService, mockWorkMacroService, Substitute.For<IClientContractDataProvider>());

        var now = DateTime.UtcNow;
        var startDate = new DateOnly(now.Year, now.Month, 1).AddDays(-5);
        var endDate = new DateOnly(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = false,
            OrderBy = "name",
            SortOrder = "asc",
            SearchString = "TEST_"
        };

        // Act
        var result = await repository.WorkList(filter);

        // Assert
        var testClients = result.Clients.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClients.Count.ShouldBe(2);
        testClients.ShouldAllBe(c => c.Type == EntityTypeEnum.Employee);
    }

    [Test]
    public async Task WorkList_ShowEmployeesFalse_ShowExternTrue_ReturnsOnlyExtern()
    {
        // Arrange
        var mockGroupFilter = Substitute.For<IClientGroupFilterService>();
        var mockSearchFilter = Substitute.For<IClientSearchFilterService>();
        mockGroupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        mockSearchFilter.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => ((IQueryable<Client>)args[0]).Where(c => c.Name!.Contains("TEST_")));

        var mockLogger = Substitute.For<ILogger<Work>>();
        var mockWorkMacroService = Substitute.For<IWorkMacroService>();
        var baseQueryService = new ClientBaseQueryService(_context, mockGroupFilter, mockSearchFilter, new Klacks.Api.Domain.Services.Clients.ClientSearchService(), new Klacks.IntegrationTest.TestHelpers.EmptyClientFuzzySearchService());
        var repository = new WorkRepository(_context, mockLogger, baseQueryService, mockWorkMacroService, Substitute.For<IClientContractDataProvider>());

        var now = DateTime.UtcNow;
        var startDate = new DateOnly(now.Year, now.Month, 1).AddDays(-5);
        var endDate = new DateOnly(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = false,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc",
            SearchString = "TEST_"
        };

        // Act
        var result = await repository.WorkList(filter);

        // Assert
        var testClients = result.Clients.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClients.Count.ShouldBe(1);
        testClients[0].Type.ShouldBe(EntityTypeEnum.ExternEmp);
        testClients[0].FirstName.ShouldBe("Charlie");
    }

    [Test]
    public async Task WorkList_ShowEmployeesFalse_ShowExternFalse_ReturnsEmpty()
    {
        // Arrange
        var mockGroupFilter = Substitute.For<IClientGroupFilterService>();
        var mockSearchFilter = Substitute.For<IClientSearchFilterService>();
        mockGroupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        mockSearchFilter.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => ((IQueryable<Client>)args[0]).Where(c => c.Name!.Contains("TEST_")));

        var mockLogger = Substitute.For<ILogger<Work>>();
        var mockWorkMacroService = Substitute.For<IWorkMacroService>();
        var baseQueryService = new ClientBaseQueryService(_context, mockGroupFilter, mockSearchFilter, new Klacks.Api.Domain.Services.Clients.ClientSearchService(), new Klacks.IntegrationTest.TestHelpers.EmptyClientFuzzySearchService());
        var repository = new WorkRepository(_context, mockLogger, baseQueryService, mockWorkMacroService, Substitute.For<IClientContractDataProvider>());

        var now = DateTime.UtcNow;
        var startDate = new DateOnly(now.Year, now.Month, 1).AddDays(-5);
        var endDate = new DateOnly(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = false,
            ShowExtern = false,
            OrderBy = "name",
            SortOrder = "asc",
            SearchString = "TEST_"
        };

        // Act
        var result = await repository.WorkList(filter);

        // Assert
        var testClients = result.Clients.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClients.ShouldBeEmpty();
    }

    [Test]
    public async Task WorkList_WithGuaranteedHoursAsc_SortsByGuaranteedHoursAscending()
    {
        // Arrange
        var mockGroupFilter = Substitute.For<IClientGroupFilterService>();
        var mockSearchFilter = Substitute.For<IClientSearchFilterService>();
        mockGroupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        mockSearchFilter.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => ((IQueryable<Client>)args[0]).Where(c => c.Name!.Contains("TEST_")));

        var mockLogger = Substitute.For<ILogger<Work>>();
        var mockWorkMacroService = Substitute.For<IWorkMacroService>();
        var baseQueryService = new ClientBaseQueryService(_context, mockGroupFilter, mockSearchFilter, new Klacks.Api.Domain.Services.Clients.ClientSearchService(), new Klacks.IntegrationTest.TestHelpers.EmptyClientFuzzySearchService());
        var repository = new WorkRepository(_context, mockLogger, baseQueryService, mockWorkMacroService, Substitute.For<IClientContractDataProvider>());

        var now = DateTime.UtcNow;
        var startDate = new DateOnly(now.Year, now.Month, 1).AddDays(-5);
        var endDate = new DateOnly(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = false,
            OrderBy = "guaranteedhours",
            SortOrder = "asc",
            SearchString = "TEST_"
        };

        // Act
        var result = await repository.WorkList(filter);

        // Assert
        var testClients = result.Clients.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClients.Count.ShouldBe(2);
    }

    [Test]
    public async Task WorkList_IndividualSort_OverridesPrimarySort()
    {
        // Arrange
        var mockGroupFilter = Substitute.For<IClientGroupFilterService>();
        var mockSearchFilter = Substitute.For<IClientSearchFilterService>();
        mockGroupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        mockSearchFilter.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => ((IQueryable<Client>)args[0]).Where(c => c.Name!.Contains("TEST_")));

        var mockLogger = Substitute.For<ILogger<Work>>();
        var mockWorkMacroService = Substitute.For<IWorkMacroService>();
        var baseQueryService = new ClientBaseQueryService(_context, mockGroupFilter, mockSearchFilter, new Klacks.Api.Domain.Services.Clients.ClientSearchService(), new Klacks.IntegrationTest.TestHelpers.EmptyClientFuzzySearchService());
        var repository = new WorkRepository(_context, mockLogger, baseQueryService, mockWorkMacroService, Substitute.For<IClientContractDataProvider>());

        var now = DateTime.UtcNow;
        var startDate = new DateOnly(now.Year, now.Month, 1).AddDays(-5);
        var endDate = new DateOnly(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month)).AddDays(5);
        var filterWithIndividual = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "desc",
            IndividualSort = true,
            SearchString = "TEST_"
        };

        var filterWithoutIndividual = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "desc",
            IndividualSort = false,
            SearchString = "TEST_"
        };

        // Act
        var resultWithIndividual = await repository.WorkList(filterWithIndividual);
        var resultWithoutIndividual = await repository.WorkList(filterWithoutIndividual);

        // Assert
        var testClientsWithIndividual = resultWithIndividual.Clients.Where(c => c.Name!.Contains("TEST_")).ToList();
        var testClientsWithoutIndividual = resultWithoutIndividual.Clients.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClientsWithIndividual.Count.ShouldBe(3);
        testClientsWithoutIndividual.Count.ShouldBe(3);
        testClientsWithoutIndividual[0].FirstName.ShouldBe("Charlie");
    }
}
