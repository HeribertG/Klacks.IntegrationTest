// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Wraps the real options provider and overrides only the learning mode, so a test host can force Gate or
/// AutoApply while every other threshold still comes from the settings table as in production.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.IntegrationTest.Assistant.Learning;

public sealed class FixedModeSkillLearningOptionsProvider : ISkillLearningOptionsProvider
{
    private readonly SkillLearningOptionsProvider _inner;
    private readonly SkillLearningMode _mode;

    public FixedModeSkillLearningOptionsProvider(SkillLearningOptionsProvider inner, SkillLearningMode mode)
    {
        _inner = inner;
        _mode = mode;
    }

    public async Task<SkillLearningOptions> GetAsync(CancellationToken cancellationToken = default) =>
        (await _inner.GetAsync(cancellationToken)) with { Mode = _mode };
}
