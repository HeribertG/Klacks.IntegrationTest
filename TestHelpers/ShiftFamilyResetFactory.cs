// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the real ShiftFamilyReset for the fixtures that assemble a ShiftCutFacade by hand.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.IntegrationTest.TestHelpers;

internal static class ShiftFamilyResetFactory
{
    public static ShiftFamilyReset Create(
        DataBaseContext context,
        IShiftRepository shiftRepository,
        IShiftTreeService shiftTreeService,
        IShiftVisibilityGuard shiftVisibilityGuard,
        IUnitOfWork unitOfWork) => new(
            shiftRepository,
            new OrderFamilyEndRepository(context),
            new ShiftExpensesRepository(context, Substitute.For<ILogger<ShiftExpenses>>()),
            new ShiftResetService(shiftRepository, Substitute.For<ILogger<ShiftResetService>>()),
            shiftTreeService,
            shiftVisibilityGuard,
            unitOfWork,
            Substitute.For<ILogger<ShiftFamilyReset>>());
}
