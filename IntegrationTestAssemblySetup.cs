// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

// No namespace: an assembly-level SetUpFixture runs once for the entire integration-test assembly,
// independent of which fixtures or filters are executed.

/// <summary>
/// Assembly-wide one-time setup for the integration tests. Two responsibilities, both required
/// before any fixture touches the database:
/// (1) Enables Npgsql's legacy timestamp behaviour (mirrors the production runtime in Program.cs) so
///     DateTimes with Kind=Unspecified/Local can be written to timestamptz columns; the test host
///     never runs Program.cs, so without this any entity storing such a DateTime is rejected. The
///     switch is permissive (a superset of the strict behaviour), so it cannot break stricter fixtures.
/// (2) Guarantees the database schema and seeds exist before the first fixture runs. Many fixtures
///     open a direct DbContext/connection in their own OneTimeSetUp (they do not boot a host), so on a
///     fresh database — the CI first-run case — whichever such fixture NUnit orders first fails with
///     42P01 "relation ... does not exist" because no host has yet run DatabaseInitializer. Booting one
///     real host here (which runs the production init path: create database, migrations, stored
///     procedures, and the full seed set incl. the default agent that only full host startup creates)
///     removes that ordering dependency. On an already-migrated database (the shared local dev DB) the
///     pending-migration probe short-circuits to a fast no-op, so no host is booted here.
/// </summary>
[SetUpFixture]
public class IntegrationTestAssemblySetup
{
    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        await EnsureDatabaseInitializedAsync();
    }

    private static async Task EnsureDatabaseInitializedAsync()
    {
        if (await IsDatabaseSchemaReadyAsync())
        {
            // Already migrated (e.g. the shared local dev DB): the direct-connection fixtures can run
            // as-is, so booting a host would be pure overhead. Fast no-op.
            return;
        }

        // Fresh or unmigrated database: run the production initialisation path exactly once, before any
        // fixture. Booting the real host (not just DatabaseInitializer.InitializeAsync) is deliberate —
        // the default agent and the skill/recipe/global-rule seeds are created by the host-startup
        // seeders in Program.cs, not inside InitializeAsync, and fixtures such as
        // PendingUserNoteRepositoryTests read that agent in their OneTimeSetUp. Program.cs runs
        // InitializeAsync (migrations + stored procedures + base seed) and those host seeders in one boot.
        using var factory = new SignalRTestWebApplicationFactory();
        _ = factory.Services;
    }

    private static async Task<bool> IsDatabaseSchemaReadyAsync()
    {
        try
        {
            var options = new DbContextOptionsBuilder<DataBaseContext>()
                .UseNpgsql(TestHostDatabase.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options;

            await using var context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
            return !pendingMigrations.Any();
        }
        catch
        {
            // Database missing, unreachable, or without a migrations-history table: treat as not ready
            // so the full host-boot initialisation path runs.
            return false;
        }
    }
}
