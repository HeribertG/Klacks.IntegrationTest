// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.IntegrationTest.TestHelpers;

/// <summary>
/// The English-default ScheduleCommandKeywordSet (FREE/-FREE/EARLY/-EARLY/LATE/-LATE/NIGHT/-NIGHT),
/// shared by every test that needs a configured keyword set instead of re-declaring the literal.
/// </summary>
internal static class ScheduleCommandKeywordTestFactory
{
    public static readonly ScheduleCommandKeywordSet Default = new()
    {
        FreeToken = "FREE",
        NegFreeToken = "-FREE",
        EarlyToken = "EARLY",
        NegEarlyToken = "-EARLY",
        LateToken = "LATE",
        NegLateToken = "-LATE",
        NightToken = "NIGHT",
        NegNightToken = "-NIGHT",
    };
}
