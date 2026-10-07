// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Test double that simulates losing an insert race: the first natural-key lookup reports "no row" although a
/// committed row exists, every other call goes to the real repository.
/// </summary>
/// <param name="inner">The real repository</param>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

public sealed class FirstLookupMissesRepository : IReplacementRequestRepository
{
    private readonly IReplacementRequestRepository _inner;
    private bool _missed;

    public FirstLookupMissesRepository(IReplacementRequestRepository inner)
    {
        _inner = inner;
    }

    public Task<ReplacementRequest?> FindLiveAsync(
        Guid? analyseToken, Guid candidateClientId, Guid shiftId, DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!_missed)
        {
            _missed = true;
            return Task.FromResult<ReplacementRequest?>(null);
        }

        return _inner.FindLiveAsync(analyseToken, candidateClientId, shiftId, date, cancellationToken);
    }

    public Task<List<ReplacementRequest>> ListLiveByTokenAsync(Guid analyseToken, CancellationToken cancellationToken = default)
        => _inner.ListLiveByTokenAsync(analyseToken, cancellationToken);

    public Task<ReplacementRequest?> FindManualByWorkChangeIdAsync(Guid workChangeId, CancellationToken cancellationToken = default)
        => _inner.FindManualByWorkChangeIdAsync(workChangeId, cancellationToken);

    public Task<DateTime?> FindReportedAtAsync(Guid analyseToken, Guid absentClientId, CancellationToken cancellationToken = default)
        => _inner.FindReportedAtAsync(analyseToken, absentClientId, cancellationToken);

    public Task<List<ReplacementRequest>> ListAsync(ReplacementRequestFilter filter, int maxRows, CancellationToken cancellationToken = default)
        => _inner.ListAsync(filter, maxRows, cancellationToken);

    public Task<int> DeleteReportedBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        => _inner.DeleteReportedBeforeAsync(cutoffUtc, cancellationToken);

    public Task Add(ReplacementRequest model) => _inner.Add(model);

    public Task<ReplacementRequest?> Delete(Guid id) => _inner.Delete(id);

    public void Detach(ReplacementRequest model) => _inner.Detach(model);

    public Task<bool> Exists(Guid id) => _inner.Exists(id);

    public Task<ReplacementRequest?> Get(Guid id) => _inner.Get(id);

    public Task<ReplacementRequest?> GetNoTracking(Guid id) => _inner.GetNoTracking(id);

    public Task<List<ReplacementRequest>> List() => _inner.List();

    public Task<ReplacementRequest?> Put(ReplacementRequest model) => _inner.Put(model);

    public void Remove(ReplacementRequest model) => _inner.Remove(model);
}