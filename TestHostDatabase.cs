// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Microsoft.AspNetCore.Hosting;

namespace Klacks.IntegrationTest;

/// <summary>
/// Central database wiring for in-process API test hosts. Overrides the host's connection string
/// with one whose pool prunes idle connections after seconds instead of Npgsql's default 300 and
/// is capped well below PostgreSQL's max_connections (100). EF Core hosts pool via a
/// NpgsqlDataSource built in Program.cs that is never disposed and that
/// NpgsqlConnection.ClearAllPools cannot drain, so without this every booted host fixture parks
/// its pooled connections for the rest of the test process and the server runs out of slots
/// (SQLSTATE 53300). The string MUST contain "Command Timeout" and "Minimum Pool Size=0":
/// Program.cs appends "...;Minimum Pool Size=5;Maximum Pool Size=150;" whenever "Command Timeout"
/// is absent, the last occurrence of a key wins in Npgsql, and any minimum above zero keeps that
/// many connections open per booted host until the process exits.
/// </summary>
public static class TestHostDatabase
{
    public const string ConnectionString =
        "User ID=postgres;Password=admin;Host=localhost;Port=5434;Database=klacks;Pooling=true;"
        + "Command Timeout=60;Timeout=30;Minimum Pool Size=0;Maximum Pool Size=40;"
        + "Connection Idle Lifetime=10;Connection Pruning Interval=5;";

    public static void UseTestConnection(IWebHostBuilder builder)
    {
        // UseSetting flows through the deferred host builder into the WebApplicationBuilder's
        // initial configuration. ConfigureAppConfiguration would run too late for minimal-hosting
        // apps: Program.cs reads the connection string while executing top-level code, before the
        // factory's app-configuration callbacks are applied.
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
    }
}
