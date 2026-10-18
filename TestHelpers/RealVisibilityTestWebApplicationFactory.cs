// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.IntegrationTest.TestHelpers;

/// <summary>
/// In-process API host with the real group visibility pipeline (no unrestricted substitutes), for tests that run the
/// real handlers and services as a group-restricted user through ExecutionPrincipal.
/// </summary>
public sealed class RealVisibilityTestWebApplicationFactory : HardenedTestWebApplicationFactory
{
}
