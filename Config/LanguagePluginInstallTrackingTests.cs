// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Reproduces a tracking conflict in LanguagePluginService.InstallAsync: InstallSkillSynonymsAsync
/// and InstallSkillLabelsAsync both fetch AgentSkill rows via the no-tracking GetAllEnabledAsync in
/// the SAME scope, then Update() a different in-memory instance of the same row. The first Update
/// commits and stays attached; the second collides (EF IdentityMap conflict), which
/// InstallSkillLabelsAsync swallows and logs, leaving the change tracker in a state that makes the
/// final unitOfWork.CompleteAsync() throw ArgumentOutOfRangeException ("Unexpected entry.EntityState:
/// Detached"). Reproduced live against POST /api/config/language-plugins/es/install and
/// .../zh-CN/install on 2026-09-18, both failing identically.
/// </summary>

using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Config;

[TestFixture]
[Category("Config")]
public class LanguagePluginInstallTrackingTests
{
    private const string FillGroupSkillName = "fill_group_by_criteria";

    [TestCase("es")]
    [TestCase("zh-CN")]
    public async Task InstallAsync_WritesBothSynonymsAndLabels_ForTheSameSkillInOneScope(string code)
    {
        using var factory = new SignalRTestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ILanguagePluginService>();
        var skillRepo = scope.ServiceProvider.GetRequiredService<IAgentSkillRepository>();

        await service.InstallAsync(code);

        var skills = await skillRepo.GetAllEnabledAsync();
        var fillGroup = skills.SingleOrDefault(s => s.Name == FillGroupSkillName);

        fillGroup.ShouldNotBeNull($"skill '{FillGroupSkillName}' must exist and be enabled");
        fillGroup!.Synonyms.ShouldNotBeNull();
        fillGroup.Synonyms!.ShouldContainKey(code, "InstallSkillSynonymsAsync must have written the pack");
        fillGroup.Labels.ShouldNotBeNull();
        fillGroup.Labels!.ShouldContainKey(code,
            "InstallSkillLabelsAsync must have written the label - this is the call that previously " +
            "collided with InstallSkillSynonymsAsync's tracked instance of the same row");
    }
}
