// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Plans one fixed train item and answers a miss on the first replay, a hit on every later one - the smallest
/// pair a paired gate can pass. Each replay also reads the real knowledge index for the skill under test and
/// records its text, so the caller can prove the second replay really saw the proposed description live and
/// the index was restored afterwards, not just that the sharpener returned Applied.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.IntegrationTest.Assistant.Learning;

public sealed class RecordingReplayGate : IGoldsetHoldoutReplayGate
{
    public const string ProbeItemId = "INTEGRATION_TEST_probe-item";
    private const string ProbeModel = "integration-test-model";

    public static readonly GoldsetItemRef ProbeItem = new(TurnEvalDefaults.DefaultGoldset, ProbeItemId);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly List<string> _indexTexts = [];

    public RecordingReplayGate(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string SkillName { get; set; } = string.Empty;

    public IReadOnlyList<string> IndexTextsSeenByReplays => _indexTexts;

    public Task<GoldsetReplayPlan?> PlanAsync(
        string skillName, IReadOnlyList<GoldsetItemRef> trainMisses, CancellationToken cancellationToken = default) =>
        Task.FromResult<GoldsetReplayPlan?>(new GoldsetReplayPlan(Guid.NewGuid(), ProbeModel, 0, [], [ProbeItem]));

    public async Task<IReadOnlyDictionary<GoldsetItemRef, bool?>> ReplayAsync(
        GoldsetReplayPlan plan, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<IKnowledgeIndexRepository>();
        var entries = await index.GetByKeysAsync([(KnowledgeEntryKind.Skill, SkillName)], cancellationToken);
        _indexTexts.Add(entries.FirstOrDefault()?.Text ?? string.Empty);

        return new Dictionary<GoldsetItemRef, bool?> { [ProbeItem] = _indexTexts.Count > 1 };
    }
}
