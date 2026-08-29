// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Minimal settable TimeProvider for integration tests that need to move a clock forward
/// deterministically (e.g. crossing a day boundary to prove a daily-budget rollover), without pulling
/// in the Microsoft.Extensions.TimeProvider.Testing package. Mirrors Klacks.UnitTest.TestHelpers.
/// SettableTimeProvider, kept as its own copy because Klacks.IntegrationTest does not reference
/// Klacks.UnitTest.
/// </summary>

namespace Klacks.IntegrationTest.TestHelpers;

public sealed class SettableTimeProvider : TimeProvider
{
    public DateTime Now { get; set; }

    public SettableTimeProvider(DateTime now)
    {
        Now = now;
    }

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}
