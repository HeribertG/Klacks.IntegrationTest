// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Runs the schedule timeline worker through the real DI graph of Program.cs against PostgreSQL: one range check
/// and one single check under a fresh scenario token (no rows match, nothing is written - the compensatory-rest
/// reconciler is real-mode only), each of which must end in its validation push. A missing registration or an
/// untranslatable query would surface here only as a logged job failure, so the test waits for the pushes.
/// </summary>

using Klacks.Api.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.WorkSchedule.ScheduleTimeline;

[TestFixture]
public class ScheduleTimelineRealHostTests
{
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(60);
    private static readonly DateOnly RangeStart = new(2099, 1, 5);
    private static readonly DateOnly RangeEnd = new(2099, 1, 11);

    [Test]
    public async Task RangeAndSingleCheck_ThroughTheRealHost_EachPushTheirValidationList()
    {
        await using var factory = new ScheduleTimelineTestWebApplicationFactory();
        var worker = factory.Services.GetRequiredService<ScheduleTimelineBackgroundService>();
        var token = Guid.NewGuid();
        var clientId = Guid.NewGuid();

        worker.QueueRangeCheck(RangeStart, RangeEnd, token);
        worker.QueueCheck(clientId, RangeStart.AddDays(1), token);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var arrived = await factory.Notifications.WaitForValidationsAsync(2, PushTimeout);

            arrived.ShouldBeTrue("both checks must push a validation list; a failed job only logs an error");
            var pushes = factory.Notifications.Validations.ToList();
            pushes.Select(p => p.IsFullRefresh).ShouldBe([true, false]);
            pushes.ShouldAllBe(p => p.AnalyseToken == token);
            pushes[1].CheckedClientId.ShouldBe(clientId);
            factory.Notifications.Collisions.Select(c => c.IsFullRefresh).ShouldBe([true, false]);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
