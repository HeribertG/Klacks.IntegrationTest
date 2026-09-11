// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services;
using NSubstitute;

namespace Klacks.IntegrationTest.TestHelpers;

public static class TestCompanyClock
{
    /// <summary>
    /// UTC company clock for fixtures without time-zone dependency.
    /// </summary>
    public static ICompanyClock Utc()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>()));

        return new CompanyClock(settingsReader, TimeProvider.System, Substitute.For<ISettingsChangeVersion>());
    }
}
