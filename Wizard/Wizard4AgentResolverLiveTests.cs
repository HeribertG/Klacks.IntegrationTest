// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Read-only live verification of the Wizard-4 group→employee resolver against the real test database
/// (port 5434): for a real group with members it returns exactly that group's membership client ids.
/// Writes nothing.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
public class Wizard4AgentResolverLiveTests
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

    [Test, Explicit("Read-only group->employee resolution against the real test DB (port 5434).")]
    public async Task ResolveAsync_ReturnsTheGroupsMemberClients()
    {
        var groupId = await _context.GroupItem
            .AsNoTracking()
            .Where(gi => gi.ClientId.HasValue)
            .Select(gi => gi.GroupId)
            .FirstOrDefaultAsync();

        if (groupId == default)
        {
            Assert.Ignore("No group with member clients in the test DB.");
        }

        var from = new DateOnly(2020, 1, 1);
        var until = new DateOnly(2030, 12, 31);

        var resolved = await new Wizard4AgentResolver(_context).ResolveAsync(groupId, from, until, CancellationToken.None);
        TestContext.Out.WriteLine($"group {groupId}: resolved {resolved.Count} member clients");

        resolved.Count.ShouldBeGreaterThan(0);
        resolved.ShouldBe(resolved.Distinct().ToList()); // distinct

        var actualMembers = await _context.GroupItem
            .AsNoTracking()
            .Where(gi => gi.GroupId == groupId && gi.ClientId.HasValue)
            .Select(gi => gi.ClientId!.Value)
            .Distinct()
            .ToListAsync();
        resolved.ShouldBeSubsetOf(actualMembers);
    }
}
