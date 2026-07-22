// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Klacks.IntegrationTest;

/// <summary>
/// Central database wiring for in-process API test hosts. Overrides the host's connection string
/// with one whose pool prunes idle connections after seconds instead of Npgsql's default 300 and
/// is capped well below PostgreSQL's max_connections (100). EF Core hosts pool via a
/// NpgsqlDataSource built in Program.cs that is never disposed and that
/// NpgsqlConnection.ClearAllPools cannot drain, so without this every booted host fixture parks
/// its pooled connections for the rest of the test process and the server runs out of slots
/// (SQLSTATE 53300). The emitted string always contains "Command Timeout" so Program.cs keeps the
/// tighter pool settings instead of appending its own "Minimum Pool Size=5;Maximum Pool Size=90;"
/// (Program.cs only appends when "Command Timeout" is absent, and the last occurrence of a key wins
/// in Npgsql, so any minimum above zero would keep that many connections open per booted host).
/// The base host/port/database/credentials default to the shared 5434 klacks database but can be
/// redirected with the DATABASE_URL environment variable (the same variable the direct-connection
/// fixtures already read), so a single override points the whole assembly — factory hosts and
/// direct DbContexts alike — at one database (e.g. a throwaway fresh database for a fresh-DB proof).
/// The pool tuning above is always re-applied on top of the override, whatever format it uses.
/// </summary>
public static class TestHostDatabase
{
    /// <summary>
    /// Environment variable that overrides the base connection (host/port/database/credentials).
    /// Shared with the direct-connection fixtures so one override redirects the whole assembly.
    /// </summary>
    public const string ConnectionStringEnvVar = "DATABASE_URL";

    private const string DefaultBaseConnectionString =
        "User ID=postgres;Password=admin;Host=localhost;Port=5434;Database=klacks";

    public static string ConnectionString => BuildConnectionString();

    private static string BuildConnectionString()
    {
        var baseConnection = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);

        var builder = new NpgsqlConnectionStringBuilder(
            string.IsNullOrWhiteSpace(baseConnection) ? DefaultBaseConnectionString : baseConnection)
        {
            Pooling = true,
            CommandTimeout = 60,
            Timeout = 30,
            MinPoolSize = 0,
            MaxPoolSize = 40,
            ConnectionIdleLifetime = 10,
            ConnectionPruningInterval = 5,
        };

        return builder.ConnectionString;
    }

    public static void UseTestConnection(IWebHostBuilder builder)
    {
        // UseSetting flows through the deferred host builder into the WebApplicationBuilder's
        // initial configuration. ConfigureAppConfiguration would run too late for minimal-hosting
        // apps: Program.cs reads the connection string while executing top-level code, before the
        // factory's app-configuration callbacks are applied.
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
    }
}
