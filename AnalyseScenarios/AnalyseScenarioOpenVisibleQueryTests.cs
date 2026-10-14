// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration test for IAnalyseScenarioRepository.AnyOpenVisibleAsync against real Npgsql: the scope filter
/// (Root ?? Id, so a child group resolves to its root and a root group to itself), the soft-delete and Active-status
/// filters, and the recency window over CreateTime/UpdateTime. The query backs the scenario follow-up guarantee, so
/// a wrong translation would silently pin list_scenarios into every turn or never. Assertions that need a negative
/// answer always use a root filter of the test's own groups, because the database is shared; only INTEGRATION_TEST_
/// rows are written and removed.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class AnalyseScenarioOpenVisibleQueryTests
{
    private const string TestPrefix = "INTEGRATION_TEST_OPENVISIBLE_";
    private const int WindowHours = 48;

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioRepository _repository = null!;
    private DateTime _since;
    private Group _root = null!;
    private Group _child = null!;
    private Group _foreignRoot = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _repository = new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>());
        _since = DateTime.UtcNow.AddHours(-WindowHours);
        _root = await CreateGroupAsync("ROOT", null, null);
        _child = await CreateGroupAsync("CHILD", _root.Id, _root.Id);
        _foreignRoot = await CreateGroupAsync("FOREIGN", null, null);
    }

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
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Group> CreateGroupAsync(string suffix, Guid? parent, Guid? root)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = parent,
            Root = root
        };
        await _context.Set<Group>().AddAsync(group);
        await _context.SaveChangesAsync();
        return group;
    }

    private async Task<AnalyseScenario> CreateScenarioAsync(
        string suffix, Guid groupId, AnalyseScenarioStatus status = AnalyseScenarioStatus.Active,
        TimeSpan? createdAgo = null, TimeSpan? updatedAgo = null, bool deleted = false)
    {
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            GroupId = groupId,
            Token = Guid.NewGuid(),
            FromDate = new DateOnly(2099, 6, 1),
            UntilDate = new DateOnly(2099, 6, 30),
            Status = status,
            CreatedByUser = TestPrefix + "USER"
        };
        await _context.Set<AnalyseScenario>().AddAsync(scenario);
        await _context.SaveChangesAsync();

        var created = DateTime.UtcNow - (createdAgo ?? TimeSpan.Zero);
        DateTime? updated = updatedAgo.HasValue ? DateTime.UtcNow - updatedAgo.Value : null;
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE analyse_scenarios SET create_time = {created}, update_time = {updated}, is_deleted = {deleted} WHERE id = {scenario.Id}");
        return scenario;
    }

    private Task<bool> AnyIn(params Group[] roots) =>
        _repository.AnyOpenVisibleAsync(roots.Select(g => g.Id).ToList(), _since);

    [Test]
    public async Task ARecentOpenScenarioInAChildGroup_IsVisibleThroughItsRoot()
    {
        await CreateScenarioAsync("CHILD_SCENARIO", _child.Id);

        (await AnyIn(_root)).ShouldBeTrue();
    }

    [Test]
    public async Task ARecentOpenScenarioInARootGroup_IsVisibleThroughItself()
    {
        await CreateScenarioAsync("ROOT_SCENARIO", _root.Id);

        (await AnyIn(_root)).ShouldBeTrue();
    }

    [Test]
    public async Task AScenarioOfAnotherRoot_IsNotVisible()
    {
        await CreateScenarioAsync("FOREIGN_SCENARIO", _foreignRoot.Id);

        (await AnyIn(_root)).ShouldBeFalse();
        (await AnyIn(_foreignRoot)).ShouldBeTrue();
    }

    [Test]
    public async Task WithoutARootFilter_TheRecentScenarioCounts()
    {
        await CreateScenarioAsync("UNSCOPED", _foreignRoot.Id);

        (await _repository.AnyOpenVisibleAsync(null, _since)).ShouldBeTrue();
    }

    [Test]
    public async Task ADeletedScenario_IsNotVisible()
    {
        await CreateScenarioAsync("DELETED", _child.Id, deleted: true);

        (await AnyIn(_root)).ShouldBeFalse();
    }

    [TestCase(AnalyseScenarioStatus.Accepted)]
    [TestCase(AnalyseScenarioStatus.Rejected)]
    [TestCase(AnalyseScenarioStatus.Superseded)]
    public async Task AScenarioThatIsNotActive_IsNotVisible(AnalyseScenarioStatus status)
    {
        await CreateScenarioAsync("CLOSED", _child.Id, status);

        (await AnyIn(_root)).ShouldBeFalse();
    }

    [Test]
    public async Task AScenarioCreatedBeforeTheWindowAndNeverUpdated_IsNotVisible()
    {
        await CreateScenarioAsync("STALE", _child.Id, createdAgo: TimeSpan.FromHours(WindowHours + 24));

        (await AnyIn(_root)).ShouldBeFalse();
    }

    [Test]
    public async Task AScenarioCreatedBeforeTheWindowButUpdatedInsideIt_IsVisible()
    {
        await CreateScenarioAsync(
            "TOUCHED", _child.Id, createdAgo: TimeSpan.FromHours(WindowHours + 24), updatedAgo: TimeSpan.FromHours(1));

        (await AnyIn(_root)).ShouldBeTrue();
    }

    [Test]
    public async Task AScenarioCreatedAndUpdatedBeforeTheWindow_IsNotVisible()
    {
        await CreateScenarioAsync(
            "OLD_TOUCHED", _child.Id, createdAgo: TimeSpan.FromHours(WindowHours + 48),
            updatedAgo: TimeSpan.FromHours(WindowHours + 24));

        (await AnyIn(_root)).ShouldBeFalse();
    }
}
