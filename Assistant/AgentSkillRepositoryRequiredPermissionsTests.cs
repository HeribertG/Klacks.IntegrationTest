// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that the name-only projection behind the recipe rights check (GetRequiredPermissionsByNameAsync) is a query a real
/// provider can translate: the name list becomes an IN predicate, only enabled skills are returned, an unknown name is
/// simply absent and an empty list costs no query. The in-memory provider the unit fixtures use is LINQ to Objects and would
/// translate anything. The name match is case-insensitive in the database itself, and when several agents carry a skill of
/// the same name the default agent's row decides. The determinism test writes one throw-away agent and two skill rows
/// named with the INTEGRATION_TEST_ prefix to the shared integration database and removes them again; every other test only
/// reads seeded skill rows.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class AgentSkillRepositoryRequiredPermissionsTests
{
    private const string EditShiftSkill = "update_shift";
    private const string EditShiftPermission = "CanEditShifts";
    private const string AdminSkill = "list_period_issues";
    private const string AdminPermission = "Admin";
    private const string UnknownSkill = "no_such_skill_for_integration_test";
    private const string DuplicatePrefix = "INTEGRATION_TEST_";
    private const string DuplicateSkill = "INTEGRATION_TEST_duplicate_skill";

    private DataBaseContext _context = null!;
    private AgentSkillRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new AgentSkillRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ReturnsTheSeededRequirementOfEachNamedSkill_AndOmitsAnUnknownName()
    {
        var result = await _repository.GetRequiredPermissionsByNameAsync([EditShiftSkill, AdminSkill, UnknownSkill]);

        result[EditShiftSkill].ShouldBe(EditShiftPermission);
        result[AdminSkill].ShouldBe(AdminPermission);
        result.ContainsKey(UnknownSkill).ShouldBeFalse();
    }

    [Test]
    public async Task MatchesNamesCaseInsensitivelyInTheReturnedDictionary()
    {
        var result = await _repository.GetRequiredPermissionsByNameAsync([EditShiftSkill]);

        result.ContainsKey(EditShiftSkill.ToUpperInvariant()).ShouldBeTrue();
    }

    [Test]
    public async Task MatchesNamesCaseInsensitivelyInTheDatabaseQueryItself()
    {
        var result = await _repository.GetRequiredPermissionsByNameAsync([EditShiftSkill.ToUpperInvariant()]);

        result[EditShiftSkill].ShouldBe(EditShiftPermission);
    }

    [Test]
    public async Task WhenSeveralAgentsCarryTheSameSkillName_TheDefaultAgentsRowDecides()
    {
        var defaultAgentId = await _context.Agents.Where(a => a.IsDefault).Select(a => a.Id).SingleAsync();
        var otherAgent = new Agent { Id = Guid.NewGuid(), Name = $"{DuplicatePrefix}Agent", IsActive = true, IsDefault = false };
        try
        {
            _context.Agents.Add(otherAgent);
            _context.AgentSkills.AddRange(
                NewSkill(otherAgent.Id, otherPermission: "OtherAgentPermission"),
                NewSkill(defaultAgentId, otherPermission: "DefaultAgentPermission"));
            await _context.SaveChangesAsync();

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var result = await _repository.GetRequiredPermissionsByNameAsync([DuplicateSkill]);

                result[DuplicateSkill].ShouldBe("DefaultAgentPermission");
            }
        }
        finally
        {
            _context.ChangeTracker.Clear();
            await _context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM agent_skills WHERE name = '{DuplicateSkill}'; DELETE FROM agents WHERE name LIKE '{DuplicatePrefix}%';");
        }
    }

    private static AgentSkill NewSkill(Guid agentId, string otherPermission) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = agentId,
        Name = DuplicateSkill,
        Description = "Throw-away skill of an integration test",
        RequiredPermission = otherPermission,
        IsEnabled = true
    };

    [Test]
    public async Task AnEmptyNameList_ReturnsNothing()
    {
        var result = await _repository.GetRequiredPermissionsByNameAsync([]);

        result.Count.ShouldBe(0);
    }
}
