// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.IntegrationTest;

/// <summary>
/// The one host that brings a fresh or unmigrated test database up to the production state before any
/// fixture runs (IntegrationTestAssemblySetup). Unlike every other test host it keeps the knowledge
/// index startup sync: on the fresh CI database this boot is what fills knowledge_index, which
/// KnowledgeIndexRecipeGoldenSetDiHostTests reads without syncing itself. All other background services
/// and the ONNX warm-up stay off.
/// </summary>
public sealed class DatabaseInitializationTestWebApplicationFactory : HardenedTestWebApplicationFactory
{
    protected override bool RunsKnowledgeIndexSync => true;
}
