// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.IntegrationTest.WorkSchedule.ScheduleTimeline;

/// <summary>
/// The real Program.cs host (background services switched off by the hardened base) with only the SignalR-backed
/// work notification service swapped for a recorder, so a test can start the schedule timeline worker by hand and
/// observe what it would push to the clients.
/// </summary>
public sealed class ScheduleTimelineTestWebApplicationFactory : HardenedTestWebApplicationFactory
{
    public RecordingWorkNotificationService Notifications { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWorkNotificationService>();
            services.AddSingleton<IWorkNotificationService>(Notifications);
        });
    }
}
