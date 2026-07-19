// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Npgsql;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

// No namespace: the assembly-level action must apply to every fixture in the assembly,
// independent of which fixtures or filters are executed.

[assembly: ClearNpgsqlPoolsAfterTest]

/// <summary>
/// Assembly-wide NUnit action that drains all Npgsql connection pools after each test case.
/// The suite runs many sequential fixtures (plain DbContexts plus full WebApplicationFactory
/// hosts) whose pooled connections stay idle for Npgsql's default idle lifetime of 300 seconds -
/// longer than a whole run - so idle server connections accumulate monotonically across fixtures
/// until PostgreSQL's max_connections (100) is exhausted and late fixtures fail with SQLSTATE
/// 53300. Clearing the pools after every test bounds the server-side connection count to a
/// single test's real concurrency. ActionTargets.Test is required: an assembly-level action with
/// ActionTargets.Suite wraps only the one assembly-level suite, so it would never fire between
/// fixtures. AfterTest runs after the fixture's TearDown, so per-test transactions are already
/// closed and only genuinely idle pooled connections are pruned; a live host simply reopens its
/// next connection on demand against the local database.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ClearNpgsqlPoolsAfterTestAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
    }

    public void AfterTest(ITest test)
    {
        NpgsqlConnection.ClearAllPools();
    }
}
