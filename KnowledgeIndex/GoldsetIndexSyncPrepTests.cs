// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Brings a database up to the state of the binaries under test before a retrieval goldset runs: boots the
/// real host (migrations, skill and recipe seed sync) and then runs the knowledge index synchronisation, which
/// the goldset hosts deliberately do not do. Without it a before/after comparison of a skill description change
/// reads the same stale knowledge_index rows on both sides. Point it at a throwaway copy of the integration
/// database through DATABASE_URL, run it once per variant, then run the goldset on the same database.
/// </summary>

using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Explicit("Boots the real host and re-embeds changed knowledge_index rows in the database named by DATABASE_URL. Run manually before a goldset comparison.")]
[Category("RealDatabase")]
public class GoldsetIndexSyncPrepTests
{
    private const string SyncReason = "goldset prep";

    [Test]
    public async Task SyncSkillsAndKnowledgeIndex_ForTheBinariesUnderTest()
    {
        using var factory = new DatabaseInitializationTestWebApplicationFactory();
        _ = factory.Services;

        await factory.Services.GetRequiredService<IKnowledgeIndexSyncScheduler>()
            .RunNowAsync(SyncReason, CancellationToken.None);
    }
}
