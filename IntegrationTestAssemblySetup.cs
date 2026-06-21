// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using NUnit.Framework;

// No namespace: an assembly-level SetUpFixture runs once for the entire integration-test assembly,
// independent of which fixtures or filters are executed.

/// <summary>
/// Assembly-wide one-time setup for the integration tests. Mirrors the production runtime (Program.cs)
/// by enabling Npgsql's legacy timestamp behaviour, so DateTimes with Kind=Unspecified/Local can be
/// written to timestamptz columns. The test host never runs Program.cs, so without this any entity that
/// stores such a DateTime (e.g. a customer's membership/address ValidFrom) is rejected by Npgsql.
/// The switch is permissive (a superset of the strict behaviour), so it cannot break stricter fixtures.
/// </summary>
[SetUpFixture]
public class IntegrationTestAssemblySetup
{
    [OneTimeSetUp]
    public void GlobalSetup()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }
}
