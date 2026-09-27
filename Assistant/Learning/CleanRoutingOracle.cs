// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Stands in for the golden-case routing oracle: it always reports zero regressions, so the gate's golden-case
/// check never blocks the one description proposal this test measures. The other two members are never called
/// by the description gate and throw if that ever changes.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.IntegrationTest.Assistant.Learning;

public sealed class CleanRoutingOracle : ISkillRoutingOracle
{
    private const string NotUsed = "The description gate integration test only replays golden cases.";

    public Task<SkillRoutingProbe> ProbeAsync(
        string utterance, string? locale, string targetSkill, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotUsed);

    public Task<IReadOnlyList<string>> ListReachableSkillsAsync(
        string utterance, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotUsed);

    public Task<IReadOnlyList<string>> FindFailingGoldenCasesAsync(
        IReadOnlyList<SkillLearningGoldenCase> goldenCases, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
