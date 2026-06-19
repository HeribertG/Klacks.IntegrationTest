// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Read-only live verification of the C2 availability-ineligibility service against the real test
/// database (port 5434): a real explicit-unavailable record, paired with a shift slot covering that
/// hour, must yield the (agent, shift, date) ineligible triple. Exercises the real load path + the
/// overlap match. Writes nothing.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
public class AvailabilityIneligibilityServiceLiveTests
{
    private DataBaseContext _context = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _context.Dispose();

    [Test, Explicit("Read-only C2 availability service against the real test DB (port 5434).")]
    public async Task GetAsync_BlocksAnAgentExplicitlyUnavailableDuringAShift()
    {
        var unavailable = await _context.ClientAvailability
            .AsNoTracking()
            .Where(a => !a.IsAvailable && a.Hour >= 0 && a.Hour <= 22)
            .OrderBy(a => a.Date)
            .FirstOrDefaultAsync();

        if (unavailable is null)
        {
            Assert.Ignore("No explicit-unavailable availability rows in the test DB.");
        }

        var shiftId = Guid.NewGuid();
        var slot = new AvailabilityShiftSlot(
            shiftId,
            unavailable!.Date,
            new TimeOnly(unavailable.Hour, 0),
            new TimeOnly(unavailable.Hour, 0).AddHours(1));

        var service = new AvailabilityIneligibilityService(
            new ClientAvailabilityRepository(_context, Substitute.For<ILogger<ClientAvailability>>()));

        var result = await service.GetAsync([unavailable.ClientId], [slot], CancellationToken.None);
        TestContext.Out.WriteLine($"agent {unavailable.ClientId} unavailable {unavailable.Date} hour {unavailable.Hour} -> {result.Count} ineligible triple(s)");

        result.ShouldContain((unavailable.ClientId.ToString(), shiftId, unavailable.Date));
    }
}
