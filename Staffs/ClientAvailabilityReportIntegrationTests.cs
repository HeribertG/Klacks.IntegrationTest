// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services.ClientAvailabilitySchedule;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Staffs;

/// <summary>
/// Integration tests for the client availability report data sources against a real Postgres database:
/// the stored procedure get_client_availability_for_schedule (range aggregation, raw SQL bypasses the
/// EF soft-delete query filter) and ClientAvailabilityRepository.GetTotalsByClientsAndDateRange
/// (real Npgsql GroupBy translation incl. the global HasQueryFilter).
///
/// Connection String: "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin"
/// Or use environment variable DATABASE_URL.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class ClientAvailabilityReportIntegrationTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private const string TestClientPrefix = "INTEGRATION_TEST_";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        using var context = new DataBaseContext(options, mockHttpContextAccessor);

        var orphanedTestClients = await context.Client
            .Where(c => c.FirstName != null && c.FirstName.StartsWith(TestClientPrefix))
            .ToListAsync();

        if (orphanedTestClients.Count > 0)
        {
            Console.WriteLine($"[OneTimeSetUp] Found {orphanedTestClients.Count} orphaned test clients. Cleaning up...");
            await CleanupTestDataWithContext(context);
            Console.WriteLine("[OneTimeSetUp] Cleanup completed.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataWithContext(_context);
        _context?.Dispose();
    }

    private static async Task CleanupTestDataWithContext(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM client_availability WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM client_image WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM membership WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM communication WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM annotation WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{TestClientPrefix}%');
            DELETE FROM client WHERE first_name LIKE '{TestClientPrefix}%';
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Guid> CreateTestClientAsync(string nameSuffix)
    {
        var clientId = Guid.NewGuid();
        var client = new Client
        {
            Id = clientId,
            FirstName = $"{TestClientPrefix}{nameSuffix}",
            Name = "AvailabilityReport",
            Gender = GenderEnum.Male,
            Type = EntityTypeEnum.Employee,
            LegalEntity = false
        };

        await _context.Client.AddAsync(client);
        await _context.SaveChangesAsync();
        return clientId;
    }

    private async Task<Guid> AddAvailabilityAsync(Guid clientId, DateOnly date, int hour, bool isAvailable = true)
    {
        var row = new ClientAvailability
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Date = date,
            Hour = hour,
            IsAvailable = isAvailable
        };

        await _context.ClientAvailability.AddAsync(row);
        await _context.SaveChangesAsync();
        return row.Id;
    }

    private async Task SoftDeleteAvailabilityAsync(Guid availabilityId)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE client_availability SET is_deleted = true WHERE id = {availabilityId}");
        _context.ChangeTracker.Clear();
    }

    private ClientAvailabilityScheduleService CreateScheduleService()
    {
        return new ClientAvailabilityScheduleService(_context);
    }

    private ClientAvailabilityRepository CreateRepository()
    {
        return new ClientAvailabilityRepository(_context, Substitute.For<ILogger<ClientAvailability>>());
    }

    [Test]
    public async Task Ranges_ConsecutiveAvailableHours_Should_Merge_Into_Single_Range()
    {
        var clientId = await CreateTestClientAsync("RangesMerge");
        var date = new DateOnly(2030, 1, 15);

        foreach (var hour in new[] { 8, 9, 10, 11 })
        {
            await AddAvailabilityAsync(clientId, date, hour);
        }

        await AddAvailabilityAsync(clientId, date, 12, isAvailable: false);

        var service = CreateScheduleService();
        var entries = await service.GetClientAvailabilityQuery(date, date, new List<Guid> { clientId }).ToListAsync();

        Console.WriteLine("=== SP RANGE MERGE TEST ===");
        foreach (var entry in entries)
        {
            Console.WriteLine($"  {entry.ClientId} {entry.AvailabilityDate:yyyy-MM-dd}: {entry.AvailabilityRanges}");
        }

        entries.Count.ShouldBe(1, "Exactly one row per client and date expected");
        entries[0].ClientId.ShouldBe(clientId);
        DateOnly.FromDateTime(entries[0].AvailabilityDate).ShouldBe(date);
        entries[0].AvailabilityRanges.ShouldBe("08:00-12:00", "Hours 8-11 must merge into one range; the unavailable hour 12 must not extend it");
    }

    [Test]
    public async Task Ranges_GapBetweenHours_Should_Produce_Multiple_CommaSeparated_Ranges()
    {
        var clientId = await CreateTestClientAsync("RangesGap");
        var date = new DateOnly(2030, 1, 16);

        foreach (var hour in new[] { 8, 9, 10, 11, 14, 15, 16, 17 })
        {
            await AddAvailabilityAsync(clientId, date, hour);
        }

        var service = CreateScheduleService();
        var entries = await service.GetClientAvailabilityQuery(date, date, new List<Guid> { clientId }).ToListAsync();

        Console.WriteLine("=== SP RANGE GAP TEST ===");
        foreach (var entry in entries)
        {
            Console.WriteLine($"  {entry.ClientId} {entry.AvailabilityDate:yyyy-MM-dd}: {entry.AvailabilityRanges}");
        }

        entries.Count.ShouldBe(1);
        entries[0].AvailabilityRanges.ShouldBe("08:00-12:00,14:00-18:00", "Gap at hours 12/13 must split the day into two comma-separated ranges ordered by start hour");
    }

    [Test]
    public async Task Ranges_SoftDeleted_Rows_Should_Be_Excluded_By_StoredProcedure()
    {
        var clientId = await CreateTestClientAsync("RangesSoftDelete");
        var date = new DateOnly(2030, 1, 17);

        await AddAvailabilityAsync(clientId, date, 8);
        var hour9Id = await AddAvailabilityAsync(clientId, date, 9);
        await AddAvailabilityAsync(clientId, date, 10);

        await SoftDeleteAvailabilityAsync(hour9Id);

        var service = CreateScheduleService();
        var entries = await service.GetClientAvailabilityQuery(date, date, new List<Guid> { clientId }).ToListAsync();

        Console.WriteLine("=== SP SOFT-DELETE TEST ===");
        foreach (var entry in entries)
        {
            Console.WriteLine($"  {entry.ClientId} {entry.AvailabilityDate:yyyy-MM-dd}: {entry.AvailabilityRanges}");
        }

        entries.Count.ShouldBe(1);
        entries[0].AvailabilityRanges.ShouldBe("08:00-09:00,10:00-11:00", "Raw SQL bypasses the EF query filter, so the SP itself must exclude the soft-deleted hour 9 and split the range");
    }

    [Test]
    public async Task Ranges_Rows_Outside_DateRange_Or_For_Unrequested_Clients_Should_Be_Excluded()
    {
        var requestedClientId = await CreateTestClientAsync("RangesScopeIn");
        var otherClientId = await CreateTestClientAsync("RangesScopeOut");
        var date = new DateOnly(2030, 1, 20);
        var dateOutsideRange = date.AddDays(10);

        await AddAvailabilityAsync(requestedClientId, date, 8);
        await AddAvailabilityAsync(requestedClientId, date, 9);
        await AddAvailabilityAsync(requestedClientId, dateOutsideRange, 8);
        await AddAvailabilityAsync(otherClientId, date, 8);

        var service = CreateScheduleService();
        var entries = await service
            .GetClientAvailabilityQuery(date, date.AddDays(2), new List<Guid> { requestedClientId })
            .ToListAsync();

        Console.WriteLine("=== SP SCOPE FILTER TEST ===");
        foreach (var entry in entries)
        {
            Console.WriteLine($"  {entry.ClientId} {entry.AvailabilityDate:yyyy-MM-dd}: {entry.AvailabilityRanges}");
        }

        entries.Count.ShouldBe(1, "Only the requested client's row inside the date range must be returned");
        entries[0].ClientId.ShouldBe(requestedClientId);
        DateOnly.FromDateTime(entries[0].AvailabilityDate).ShouldBe(date);
        entries[0].AvailabilityRanges.ShouldBe("08:00-10:00");
    }

    [Test]
    public async Task Ranges_Multiple_Requested_Clients_Should_Get_Separate_Entries()
    {
        var firstClientId = await CreateTestClientAsync("RangesMultiA");
        var secondClientId = await CreateTestClientAsync("RangesMultiB");
        var date = new DateOnly(2030, 1, 22);

        await AddAvailabilityAsync(firstClientId, date, 8);
        await AddAvailabilityAsync(firstClientId, date, 9);
        await AddAvailabilityAsync(secondClientId, date, 20);
        await AddAvailabilityAsync(secondClientId, date, 21);
        await AddAvailabilityAsync(secondClientId, date, 22);

        var service = CreateScheduleService();
        var entries = await service
            .GetClientAvailabilityQuery(date, date, new List<Guid> { firstClientId, secondClientId })
            .ToListAsync();

        Console.WriteLine("=== SP MULTI-CLIENT TEST ===");
        foreach (var entry in entries)
        {
            Console.WriteLine($"  {entry.ClientId} {entry.AvailabilityDate:yyyy-MM-dd}: {entry.AvailabilityRanges}");
        }

        entries.Count.ShouldBe(2, "One row per client and date expected");

        var firstEntry = entries.Single(e => e.ClientId == firstClientId);
        firstEntry.AvailabilityRanges.ShouldBe("08:00-10:00", "Window function must partition per client, not mix hour groups across clients");

        var secondEntry = entries.Single(e => e.ClientId == secondClientId);
        secondEntry.AvailabilityRanges.ShouldBe("20:00-23:00");
    }

    [Test]
    public async Task Totals_Should_Sum_Hours_And_Count_Distinct_Days_Per_Client()
    {
        var firstClientId = await CreateTestClientAsync("TotalsSum");
        var secondClientId = await CreateTestClientAsync("TotalsSumOther");
        var firstDay = new DateOnly(2030, 2, 10);
        var secondDay = firstDay.AddDays(1);
        var dayOutsideRange = firstDay.AddDays(5);

        foreach (var hour in new[] { 8, 9, 10 })
        {
            await AddAvailabilityAsync(firstClientId, firstDay, hour);
        }

        await AddAvailabilityAsync(firstClientId, firstDay, 11, isAvailable: false);
        await AddAvailabilityAsync(firstClientId, secondDay, 8);
        await AddAvailabilityAsync(firstClientId, secondDay, 9);
        await AddAvailabilityAsync(firstClientId, dayOutsideRange, 8);
        await AddAvailabilityAsync(secondClientId, firstDay, 8);

        var repository = CreateRepository();
        var totals = await repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { firstClientId, secondClientId }, firstDay, secondDay);

        Console.WriteLine("=== TOTALS GROUPBY TEST ===");
        foreach (var total in totals)
        {
            Console.WriteLine($"  {total.ClientId}: TotalHours={total.TotalHours}, DaysWithAvailability={total.DaysWithAvailability}");
        }

        totals.Count.ShouldBe(2);

        var firstTotal = totals.Single(t => t.ClientId == firstClientId);
        firstTotal.TotalHours.ShouldBe(5, "3 hours on day one plus 2 hours on day two; unavailable hour and out-of-range day must not count");
        firstTotal.DaysWithAvailability.ShouldBe(2);

        var secondTotal = totals.Single(t => t.ClientId == secondClientId);
        secondTotal.TotalHours.ShouldBe(1);
        secondTotal.DaysWithAvailability.ShouldBe(1);
    }

    [Test]
    public async Task Totals_SoftDeleted_Rows_Should_Be_Excluded_By_QueryFilter_On_Postgres()
    {
        var clientId = await CreateTestClientAsync("TotalsSoftDelete");
        var firstDay = new DateOnly(2030, 2, 20);
        var secondDay = firstDay.AddDays(1);

        await AddAvailabilityAsync(clientId, firstDay, 8);
        await AddAvailabilityAsync(clientId, firstDay, 9);
        var firstDayHour10Id = await AddAvailabilityAsync(clientId, firstDay, 10);
        var secondDayHour8Id = await AddAvailabilityAsync(clientId, secondDay, 8);

        await SoftDeleteAvailabilityAsync(firstDayHour10Id);
        await SoftDeleteAvailabilityAsync(secondDayHour8Id);

        var repository = CreateRepository();
        var totals = await repository.GetTotalsByClientsAndDateRange(
            new List<Guid> { clientId }, firstDay, secondDay);

        Console.WriteLine("=== TOTALS SOFT-DELETE TEST ===");
        foreach (var total in totals)
        {
            Console.WriteLine($"  {total.ClientId}: TotalHours={total.TotalHours}, DaysWithAvailability={total.DaysWithAvailability}");
        }

        totals.Count.ShouldBe(1);
        totals[0].TotalHours.ShouldBe(2, "The global HasQueryFilter must exclude soft-deleted rows in the Npgsql-translated GroupBy");
        totals[0].DaysWithAvailability.ShouldBe(1, "Day two only had a soft-deleted row and must not count as a day with availability");
    }
}
