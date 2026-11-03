// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The real tree service with a hook in front of RecalculateTreesAsync, the step after the first save of a reset. The hook
/// gets the number of the call, so a test can fail, retry or hold exactly that point inside the open transaction.
/// </summary>

using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.IntegrationTest.TestHelpers;

internal sealed class HookedShiftTreeService : IShiftTreeService
{
    private readonly IShiftTreeService _inner;
    private readonly Func<int, Task> _beforeRecalculate;

    public HookedShiftTreeService(IShiftTreeService inner, Func<int, Task> beforeRecalculate)
    {
        _inner = inner;
        _beforeRecalculate = beforeRecalculate;
    }

    public int RecalculateCalls { get; private set; }

    public async Task RecalculateTreesAsync(IReadOnlyCollection<Guid> rootIds, CancellationToken cancellationToken)
    {
        RecalculateCalls++;
        await _beforeRecalculate(RecalculateCalls);
        await _inner.RecalculateTreesAsync(rootIds, cancellationToken);
    }

    public void SetHierarchyRelation(Shift shift, Guid? parentId, Guid? parentRootId) =>
        _inner.SetHierarchyRelation(shift, parentId, parentRootId);

    public Task RecalculateNestedSetValuesAsync(Guid rootId) => _inner.RecalculateNestedSetValuesAsync(rootId);

    public Task RecalculateAllAffectedTreesAsync(List<Shift> processedShifts) =>
        _inner.RecalculateAllAffectedTreesAsync(processedShifts);

    public int CalculateTreeWidth(int leftValue, int rightValue) => _inner.CalculateTreeWidth(leftValue, rightValue);
}
