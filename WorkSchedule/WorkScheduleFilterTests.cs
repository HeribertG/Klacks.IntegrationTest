using FluentAssertions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Domain.Models.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace IntegrationTest.WorkSchedule;

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
            ?? "Host=localhost;Port=5434;Database=klacks1;Username=postgres;Password=admin";
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
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
            GuaranteedHoursPerMonth = 80
        };
        var contractHigh = new Contract
        {
            Id = _contractHighId,
            Name = "TEST_Contract_High",
            GuaranteedHoursPerMonth = 160
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
        var repository = new WorkRepository(_context, mockLogger, mockGroupFilter, mockSearchFilter);

        var now = DateTime.UtcNow;
        var filter = new WorkFilter
        {
            CurrentYear = now.Year,
            CurrentMonth = now.Month,
            DayVisibleBeforeMonth = 5,
            DayVisibleAfterMonth = 5,
            ShowEmployees = true,
            ShowExtern = false,
            OrderBy = "name",
            SortOrder = "asc",
            SearchString = "TEST_"
        };

        // Act
        var result = await repository.WorkList(filter);

        // Assert
        var testClients = result.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClients.Should().HaveCount(2);
        testClients.Should().OnlyContain(c => c.Type == EntityTypeEnum.Employee);
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
        var repository = new WorkRepository(_context, mockLogger, mockGroupFilter, mockSearchFilter);

        var now = DateTime.UtcNow;
        var filter = new WorkFilter
        {
            CurrentYear = now.Year,
            CurrentMonth = now.Month,
            DayVisibleBeforeMonth = 5,
            DayVisibleAfterMonth = 5,
            ShowEmployees = false,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc",
            SearchString = "TEST_"
        };

        // Act
        var result = await repository.WorkList(filter);

        // Assert
        var testClients = result.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClients.Should().HaveCount(1);
        testClients[0].Type.Should().Be(EntityTypeEnum.ExternEmp);
        testClients[0].FirstName.Should().Be("Charlie");
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
        var repository = new WorkRepository(_context, mockLogger, mockGroupFilter, mockSearchFilter);

        var now = DateTime.UtcNow;
        var filter = new WorkFilter
        {
            CurrentYear = now.Year,
            CurrentMonth = now.Month,
            DayVisibleBeforeMonth = 5,
            DayVisibleAfterMonth = 5,
            ShowEmployees = false,
            ShowExtern = false,
            OrderBy = "name",
            SortOrder = "asc",
            SearchString = "TEST_"
        };

        // Act
        var result = await repository.WorkList(filter);

        // Assert
        var testClients = result.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClients.Should().BeEmpty();
    }

    [Test]
    public async Task WorkList_WithHoursSortOrderAsc_SortsSecondaryByHours()
    {
        // Arrange
        var mockGroupFilter = Substitute.For<IClientGroupFilterService>();
        var mockSearchFilter = Substitute.For<IClientSearchFilterService>();
        mockGroupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        mockSearchFilter.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => ((IQueryable<Client>)args[0]).Where(c => c.Name!.Contains("TEST_")));

        var mockLogger = Substitute.For<ILogger<Work>>();
        var repository = new WorkRepository(_context, mockLogger, mockGroupFilter, mockSearchFilter);

        var now = DateTime.UtcNow;
        var filter = new WorkFilter
        {
            CurrentYear = now.Year,
            CurrentMonth = now.Month,
            DayVisibleBeforeMonth = 5,
            DayVisibleAfterMonth = 5,
            ShowEmployees = true,
            ShowExtern = false,
            OrderBy = "name",
            SortOrder = "asc",
            HoursSortOrder = "asc",
            SearchString = "TEST_"
        };

        // Act
        var result = await repository.WorkList(filter);

        // Assert
        var testClients = result.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClients.Should().HaveCount(2);
        testClients[0].FirstName.Should().Be("Alice");
        testClients[1].FirstName.Should().Be("Bob");
    }

    [Test]
    public async Task WorkList_HoursSortOrder_IsIndependentFromPrimarySort()
    {
        // Arrange
        var mockGroupFilter = Substitute.For<IClientGroupFilterService>();
        var mockSearchFilter = Substitute.For<IClientSearchFilterService>();
        mockGroupFilter.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        mockSearchFilter.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => ((IQueryable<Client>)args[0]).Where(c => c.Name!.Contains("TEST_")));

        var mockLogger = Substitute.For<ILogger<Work>>();
        var repository = new WorkRepository(_context, mockLogger, mockGroupFilter, mockSearchFilter);

        var now = DateTime.UtcNow;
        var filterWithHours = new WorkFilter
        {
            CurrentYear = now.Year,
            CurrentMonth = now.Month,
            DayVisibleBeforeMonth = 5,
            DayVisibleAfterMonth = 5,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "desc",
            HoursSortOrder = "asc",
            SearchString = "TEST_"
        };

        var filterWithoutHours = new WorkFilter
        {
            CurrentYear = now.Year,
            CurrentMonth = now.Month,
            DayVisibleBeforeMonth = 5,
            DayVisibleAfterMonth = 5,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "desc",
            HoursSortOrder = null,
            SearchString = "TEST_"
        };

        // Act
        var resultWithHours = await repository.WorkList(filterWithHours);
        var resultWithoutHours = await repository.WorkList(filterWithoutHours);

        // Assert
        var testClientsWithHours = resultWithHours.Where(c => c.Name!.Contains("TEST_")).ToList();
        var testClientsWithoutHours = resultWithoutHours.Where(c => c.Name!.Contains("TEST_")).ToList();
        testClientsWithHours.Should().HaveCount(3);
        testClientsWithoutHours.Should().HaveCount(3);
        testClientsWithHours[0].FirstName.Should().Be("Charlie");
        testClientsWithoutHours[0].FirstName.Should().Be("Charlie");
    }
}
