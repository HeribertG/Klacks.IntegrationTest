// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// No-op replacements for the startup services a golden-set DI-host fixture must keep away from the
/// shared dev database. Shared by the fixtures that boot the real application host so the same
/// three workarounds are not re-typed per fixture: the UiControl/RegionSetup pair works around the
/// "Kind=Utc into timestamp without time zone" startup bug, and the synchronizer replacement stops
/// KnowledgeIndexStartupService from re-embedding and overwriting knowledge_index rows when the host
/// resolves a different embedding provider than the one that wrote them.
/// </summary>

using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;

namespace Klacks.IntegrationTest.KnowledgeIndex;

internal sealed class NoOpUiControlRepository : IUiControlRepository
{
    public Task<List<UiControl>> GetByPageKeyAsync(string pageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<UiControl>());

    public Task<List<string>> GetDistinctPageKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<string>());

    public Task<List<UiControl>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<UiControl>());

    public Task AddRangeAsync(IEnumerable<UiControl> controls, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpdateAsync(UiControl control, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task UpsertAsync(UiControl control, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<int> GetCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}

internal sealed class NoOpRegionSetupService : IRegionSetupService
{
    public Task ApplyAsync() => Task.CompletedTask;
}

internal sealed class NoOpKnowledgeIndexSynchronizer : IKnowledgeIndexSynchronizer
{
    public Task SyncAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
