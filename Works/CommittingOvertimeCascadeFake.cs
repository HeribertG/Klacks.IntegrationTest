// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Stands in for OvertimeCascadeService and reproduces its extra commit: it adds a marker to the surcharges of one
/// seeded successor Work and commits through the real UnitOfWork, as the real service does after reprocessing at least
/// one successor. Without an overtime configuration the real service changes nothing and that commit never happens;
/// the fake makes it visible. With throwBeforeCommit it stages the marker and throws a plain Exception before the commit.
/// </summary>
/// <param name="context">The handler's context, used to load the successor tracked</param>
/// <param name="unitOfWork">The handler's UnitOfWork that commits the marker</param>
/// <param name="successorWorkId">The seeded Work that receives the marker</param>
/// <param name="throwBeforeCommit">True to fail after staging the marker and before the commit</param>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Klacks.IntegrationTest.Works;

public sealed class CommittingOvertimeCascadeFake : IOvertimeCascadeService
{
    public const decimal SurchargeMarker = 0.25m;
    public const string FailureMessage = "Injected overtime cascade failure before its commit";

    private readonly DataBaseContext _context;
    private readonly IUnitOfWork _unitOfWork;
    private readonly Guid _successorWorkId;
    private readonly bool _throwBeforeCommit;

    public CommittingOvertimeCascadeFake(
        DataBaseContext context, IUnitOfWork unitOfWork, Guid successorWorkId, bool throwBeforeCommit = false)
    {
        _context = context;
        _unitOfWork = unitOfWork;
        _successorWorkId = successorWorkId;
        _throwBeforeCommit = throwBeforeCommit;
    }

    public int CallCount { get; private set; }

    public async Task ReprocessSuccessorsAsync(Work work, Work? previousState = null)
    {
        CallCount++;
        var successor = await _context.Work.SingleAsync(w => w.Id == _successorWorkId);
        successor.Surcharges += SurchargeMarker;

        if (_throwBeforeCommit)
        {
            throw new Exception(FailureMessage);
        }

        await _unitOfWork.CompleteAsync();
    }

    public Task ReprocessSuccessorsAsync(IReadOnlyCollection<Work> works)
        => throw new NotSupportedException(nameof(CommittingOvertimeCascadeFake) + " serves the single-Work handlers only.");
}
