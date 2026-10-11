// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Group-visibility and scenario-token guards for seam tests that are not about visibility: every group and every
/// scenario token is visible and the caller unrestricted, like an admin. A bare substitute answers "not visible",
/// which would turn every scenario into a hidden one.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using NSubstitute;

namespace Klacks.IntegrationTest.TestHelpers;

public static class UnrestrictedGroupVisibility
{
    public static IGroupVisibilityGuard Create()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);
        guard.GetScopeAsync(Arg.Any<CancellationToken>()).Returns(GroupVisibilityScope.Unrestricted());
        guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.AreAllGroupsVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }

    public static IScenarioTokenAccessGuard CreateScenarioTokenAccess()
    {
        var guard = Substitute.For<IScenarioTokenAccessGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        return guard;
    }
}
