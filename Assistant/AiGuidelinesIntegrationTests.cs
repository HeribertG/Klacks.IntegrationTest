using FluentAssertions;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class AiGuidelinesIntegrationTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private AiGuidelinesRepository _repository = null!;
    private const string TestPrefix = "INTEGRATION_TEST_";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        using var context = new DataBaseContext(options, mockHttpContextAccessor);
        await CleanupTestDataWithContext(context);
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _repository = new AiGuidelinesRepository(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataWithContext(_context);
        _context?.Dispose();
    }

    private static async Task CleanupTestDataWithContext(DataBaseContext context)
    {
        await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM ai_guidelines WHERE name LIKE '{TestPrefix}%'");
    }

    [Test]
    public async Task Repository_AddAndGetActive_RealDatabase()
    {
        var guidelines = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = $"{TestPrefix}Active",
            Content = "- Integration test guideline",
            IsActive = true,
            Source = "test"
        };

        await _repository.AddAsync(guidelines);

        var result = await _repository.GetActiveAsync();

        result.Should().NotBeNull();
        result!.Name.Should().Be($"{TestPrefix}Active");
        result.Content.Should().Be("- Integration test guideline");
        result.IsActive.Should().BeTrue();
        result.Source.Should().Be("test");
    }

    [Test]
    public async Task Repository_GetAll_RealDatabase()
    {
        var g1 = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = $"{TestPrefix}First",
            Content = "- First",
            IsActive = true,
            Source = "test"
        };
        var g2 = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = $"{TestPrefix}Second",
            Content = "- Second",
            IsActive = false,
            Source = "test"
        };

        await _repository.AddAsync(g1);
        await _repository.AddAsync(g2);

        var all = await _repository.GetAllAsync();
        var testItems = all.Where(g => g.Name.StartsWith(TestPrefix)).ToList();

        testItems.Should().HaveCount(2);
        testItems.First().IsActive.Should().BeTrue();
    }

    [Test]
    public async Task Repository_DeactivateAll_RealDatabase()
    {
        var guidelines = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = $"{TestPrefix}ToDeactivate",
            Content = "- Will be deactivated",
            IsActive = true,
            Source = "test"
        };

        await _repository.AddAsync(guidelines);

        await _repository.DeactivateAllAsync();

        var active = await _context.AiGuidelines
            .Where(g => g.Name.StartsWith(TestPrefix) && g.IsActive && !g.IsDeleted)
            .ToListAsync();

        active.Should().BeEmpty();
    }

    [Test]
    public async Task Repository_Update_RealDatabase()
    {
        var id = Guid.NewGuid();
        var guidelines = new AiGuidelines
        {
            Id = id,
            Name = $"{TestPrefix}ToUpdate",
            Content = "- Original content",
            IsActive = true,
            Source = "test"
        };

        await _repository.AddAsync(guidelines);

        var toUpdate = await _repository.GetByIdAsync(id);
        toUpdate.Should().NotBeNull();
        toUpdate!.Content = "- Updated content";
        await _repository.UpdateAsync(toUpdate);

        var updated = await _repository.GetByIdAsync(id);
        updated!.Content.Should().Be("- Updated content");
    }

    [Test]
    public async Task FullSkillWorkflow_GetAndUpdate_RealDatabase()
    {
        var context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "integration-test",
            UserPermissions = new[] { "CanViewSettings", "CanEditSettings" }
        };

        var getSkill = new GetAiGuidelinesSkill(_repository);
        var updateSkill = new UpdateAiGuidelinesSkill(_repository);

        var updateResult = await updateSkill.ExecuteAsync(context, new Dictionary<string, object>
        {
            { "guidelines", "- Integration test rule 1\n- Integration test rule 2" },
            { "name", $"{TestPrefix}SkillCreated" }
        });

        updateResult.Success.Should().BeTrue();
        updateResult.Message.Should().Contain($"{TestPrefix}SkillCreated");

        var getResult = await getSkill.ExecuteAsync(context, new Dictionary<string, object>());

        getResult.Success.Should().BeTrue();
        getResult.Message.Should().Contain($"{TestPrefix}SkillCreated");
    }

    [Test]
    public async Task UpdateSkill_ReplacesOldActiveGuidelines_RealDatabase()
    {
        var context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "integration-test",
            UserPermissions = new[] { "CanEditSettings" }
        };

        var updateSkill = new UpdateAiGuidelinesSkill(_repository);

        await updateSkill.ExecuteAsync(context, new Dictionary<string, object>
        {
            { "guidelines", "- First version" },
            { "name", $"{TestPrefix}V1" }
        });

        await updateSkill.ExecuteAsync(context, new Dictionary<string, object>
        {
            { "guidelines", "- Second version" },
            { "name", $"{TestPrefix}V2" }
        });

        var active = await _repository.GetActiveAsync();
        active.Should().NotBeNull();
        active!.Name.Should().Be($"{TestPrefix}V2");
        active.Content.Should().Be("- Second version");

        var all = await _repository.GetAllAsync();
        var testItems = all.Where(g => g.Name.StartsWith(TestPrefix)).ToList();
        testItems.Where(g => g.IsActive).Should().HaveCount(1);
    }

    [Test]
    public async Task SeedData_Exists_RealDatabase()
    {
        var seeded = await _context.AiGuidelines
            .Where(g => g.Source == "seed" && !g.IsDeleted)
            .FirstOrDefaultAsync();

        seeded.Should().NotBeNull("Seed data should exist in the database");
        seeded!.Content.Should().Contain("Be polite and professional");
    }
}
