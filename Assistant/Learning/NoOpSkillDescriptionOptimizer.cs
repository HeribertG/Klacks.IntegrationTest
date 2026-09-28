// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Generates no proposals at all, so the sharpener under test only ever decides the one proposal this test
/// seeded itself, never a real one built from today's corpus by a language model.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.IntegrationTest.Assistant.Learning;

public sealed class NoOpSkillDescriptionOptimizer : ISkillDescriptionOptimizer
{
    public Task<SkillDescriptionOptimizerResult> GenerateProposalsAsync(
        int maxTrajectoriesToAnalyze, CancellationToken cancellationToken = default) =>
        Task.FromResult(SkillDescriptionOptimizerResult.Empty);
}
