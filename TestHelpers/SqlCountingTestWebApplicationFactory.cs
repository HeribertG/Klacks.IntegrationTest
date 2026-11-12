// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The in-process test host of the recovery fixtures (real Program.cs, the shared integration database, group and
/// scenario visibility unrestricted like an admin) with one addition: every DataBaseContext the host hands out
/// reports its SQL commands to SqlCommands. The interceptor is added to the options the host already configures
/// (ConfigureDbContext with the singleton lifetime of the pooled context's options), so the production registration
/// in Program.cs stays as it is. Commands of contexts a test builds by hand (seeding, cleanup) are not counted.
/// </summary>

using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.IntegrationTest.TestHelpers;

public sealed class SqlCountingTestWebApplicationFactory : SignalRTestWebApplicationFactory
{
    public SqlCommandCountingInterceptor SqlCommands { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<DataBaseContext>(
                options => options.AddInterceptors(SqlCommands),
                ServiceLifetime.Singleton));
    }
}
