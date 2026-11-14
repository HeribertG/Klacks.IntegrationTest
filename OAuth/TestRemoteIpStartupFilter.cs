// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Startup filter for the OAuth flow tests that sets the remote address of every request from a test header before any
/// other middleware runs, so the per-address login rate limit (20 requests a minute) does not throttle a test run in
/// which each scenario registers, signs in and exchanges a code.
/// </summary>

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Klacks.IntegrationTest.OAuth;

public sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var value)
                    && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware();
            });

            next(app);
        };
    }
}
