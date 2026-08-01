// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests proving that every skill declared as handlerType 'knowledge-happen' in
/// skill-seeds.json can actually resolve its curated markdown from agent_memories at runtime.
/// The unit-test suite cannot cover this: a memoryKey that matches no seeded happen produces a
/// SkillResult.Error at call time ("Knowledge entry ... is not available"), never a failing test,
/// so the skill looks healthy until a user asks the question. The chain has three independent
/// failure points — the frontmatter name field, the csproj copy of the markdown folder, and the
/// memory seed itself — and only a real database can observe all three at once.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills.Generic;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class KnowledgeHappenSkillResolutionTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string KnowledgeHappenHandlerType = "knowledge-happen";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private string _connectionString = null!;

    public sealed record HappenSkill(string SkillName, string MemoryKey)
    {
        public override string ToString() => SkillName;
    }

    public static IEnumerable<HappenSkill> KnowledgeHappenSkills()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            if (!skill.TryGetProperty("handlerType", out var handlerType) ||
                handlerType.ValueKind != JsonValueKind.String ||
                !string.Equals(handlerType.GetString(), KnowledgeHappenHandlerType, StringComparison.Ordinal))
            {
                continue;
            }

            var name = skill.GetProperty("name").GetString()!;
            var memoryKey = skill.TryGetProperty("handlerConfig", out var config) &&
                            config.ValueKind == JsonValueKind.Object &&
                            config.TryGetProperty("memoryKey", out var key)
                ? key.GetString() ?? string.Empty
                : string.Empty;

            yield return new HappenSkill(name, memoryKey);
        }
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [TestCaseSource(nameof(KnowledgeHappenSkills))]
    public async Task EveryKnowledgeHappenSkill_ResolvesItsCuratedContent(HappenSkill skill)
    {
        skill.MemoryKey.ShouldNotBeNullOrWhiteSpace(
            $"{skill.SkillName} declares handlerType '{KnowledgeHappenHandlerType}' but carries no " +
            "memoryKey in handlerConfig, so the executor rejects every call as misconfigured.");

        var result = await ExecuteAsync(skill.MemoryKey);

        result.Success.ShouldBeTrue(
            $"{skill.SkillName} could not load its knowledge happen '{skill.MemoryKey}'. " +
            $"Executor said: {result.Message}. Check that a markdown file under " +
            "Infrastructure/Persistence/Seed/KlacksyKnowledge carries exactly 'name: " +
            $"{skill.MemoryKey}' in its frontmatter, that it reached the build output, and that " +
            "the backend has run its knowledge memory seed since the file was added.");

        var knowledge = ReadKnowledge(result);
        knowledge.ShouldNotBeNullOrWhiteSpace($"{skill.SkillName} resolved an empty happen.");
    }

    [TestCaseSource(nameof(KnowledgeHappenSkills))]
    public async Task EveryKnowledgeHappenSkill_ContentNeedsNoSanitising(HappenSkill skill)
    {
        if (string.Equals(skill.MemoryKey, SkillNames.ExplainShiftLifecycle, StringComparison.Ordinal))
        {
            Assert.Ignore("The shift lifecycle happen is exempt: it teaches the internal names on purpose.");
        }

        await using var context = NewContext();
        var memory = await new AgentMemoryRepository(context, NullLogger<AgentMemoryRepository>.Instance)
            .GetByKeyAsync(skill.MemoryKey);

        if (memory is null)
        {
            Assert.Ignore($"'{skill.MemoryKey}' is not seeded; covered by the resolution test.");
        }

        var sanitized = KnowledgeContentSanitizer.Sanitize(memory!.Content);

        sanitized.ShouldBe(memory.Content,
            $"The curated content of {skill.SkillName} still carries internal entity names that the " +
            "sanitiser had to replace before the assistant saw them. Users do not know these words — " +
            "fix the seed markdown instead of relying on the safety net.");
    }

    [TestCaseSource(nameof(KnowledgeHappenSkills))]
    public async Task HappensWithLevelMarkers_ReturnOnlyTheRequestedSection(HappenSkill skill)
    {
        var full = await ExecuteAsync(skill.MemoryKey);
        if (!full.Success)
        {
            Assert.Ignore($"'{skill.MemoryKey}' does not resolve; covered by the resolution test.");
        }

        var fullText = ReadKnowledge(full)!;
        if (!fullText.Contains(KnowledgeHappenLevels.MarkerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore($"{skill.SkillName} carries no level markers, so it always returns everything.");
        }

        foreach (var level in KnowledgeHappenLevels.All)
        {
            if (!fullText.Contains($"{KnowledgeHappenLevels.MarkerPrefix}{level}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var scoped = await ExecuteAsync(skill.MemoryKey, level);

            scoped.Success.ShouldBeTrue($"{skill.SkillName} failed for level '{level}'.");
            var scopedText = ReadKnowledge(scoped);

            scopedText.ShouldNotBeNullOrWhiteSpace(
                $"{skill.SkillName} returned nothing for level '{level}'.");
            scopedText!.Length.ShouldBeLessThan(fullText.Length,
                $"{skill.SkillName} returned the whole document for level '{level}'. The level markers " +
                "are then decorative and the depth parameter has no effect.");
        }
    }

    private async Task<Klacks.Api.Domain.Models.Assistant.SkillResult> ExecuteAsync(
        string memoryKey, string? level = null)
    {
        await using var context = NewContext();
        var executor = new KnowledgeHappenExecutor(
            new AgentMemoryRepository(context, NullLogger<AgentMemoryRepository>.Instance),
            NullLogger<KnowledgeHappenExecutor>.Instance);

        var parameters = level is null
            ? null
            : new Dictionary<string, object> { [KnowledgeHappenLevels.ParameterName] = level };

        return await executor.ExecuteAsync(new KnowledgeHappenConfig { MemoryKey = memoryKey }, parameters);
    }

    private static string? ReadKnowledge(Klacks.Api.Domain.Models.Assistant.SkillResult result)
    {
        if (result.Data is null)
        {
            return null;
        }

        var property = result.Data.GetType().GetProperty("Knowledge");
        return property?.GetValue(result.Data) as string;
    }

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(DefinitionsRelativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{fileName} by walking up " +
            "from the test base directory.");
    }
}
