// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins against PostgreSQL (IT database klacks) which exception surfaces when a write committed through
/// IUnitOfWork.CompleteAsync is rejected: the Domain DatabaseUpdateException (with IsDuplicate / IsForeignKeyViolation
/// read from the SQLSTATE), never EF Core's DbUpdateException. The set_client_qualification,
/// set_shift_required_qualification and set_shift_preferences skills run end to end over their real command handlers,
/// so an unknown employee, shift or qualification escapes them as that Domain exception; the digest watermark seed
/// (unique settings type) fails the same way. Nothing is stored by the failing writes; the one seeded settings row
/// carries the INTEGRATION_TEST_UOW_ prefix and the cleanup deletes exactly those rows.
/// </summary>

using Klacks.Api.Application.Commands.ClientShiftPreferences;
using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Handlers.ClientShiftPreferences;
using Klacks.Api.Application.Handlers.Qualifications;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.IntegrationTest.Infrastructure;

[TestFixture]
[Category("RealDatabase")]
public class UnitOfWorkFailureTranslationTests
{
    private const string Prefix = "INTEGRATION_TEST_UOW_";

    private DataBaseContext _context = null!;
    private UnitOfWork _unitOfWork = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new DataBaseContext(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        _unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task SetClientQualificationSkill_UnknownEmployeeAndQualification_EscapesAsDomainForeignKeyViolation()
    {
        var visibility = Substitute.For<IClientVisibilityGuard>();
        visibility.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = new SetClientQualificationCommandHandler(
            new ClientQualificationRepository(_context, Substitute.For<ILogger<ClientQualification>>()), visibility, _unitOfWork);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetClientQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<SetClientQualificationCommand>(), CancellationToken.None));

        var thrown = await Should.ThrowAsync<Exception>(() => new SetClientQualificationSkill(mediator).ExecuteAsync(
            Ctx(),
            new Dictionary<string, object>
            {
                ["clientId"] = Guid.NewGuid().ToString(),
                ["qualificationId"] = Guid.NewGuid().ToString(),
                ["level"] = 3
            }));

        ShouldBeDomainForeignKeyViolation(thrown);
    }

    [Test]
    public async Task SetShiftRequiredQualificationSkill_UnknownShiftAndQualification_EscapesAsDomainForeignKeyViolation()
    {
        var visibility = Substitute.For<IShiftVisibilityGuard>();
        visibility.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = new SetShiftRequiredQualificationCommandHandler(
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()), _unitOfWork, visibility);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftRequiredQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<SetShiftRequiredQualificationCommand>(), CancellationToken.None));

        var thrown = await Should.ThrowAsync<Exception>(() => new SetShiftRequiredQualificationSkill(mediator).ExecuteAsync(
            Ctx(),
            new Dictionary<string, object>
            {
                ["shiftId"] = Guid.NewGuid().ToString(),
                ["qualificationId"] = Guid.NewGuid().ToString(),
                ["minLevel"] = 2
            }));

        ShouldBeDomainForeignKeyViolation(thrown);
    }

    [Test]
    public async Task SetShiftPreferencesSkill_UnknownEmployeeAndShift_EscapesAsDomainForeignKeyViolation()
    {
        var visibility = Substitute.For<IClientVisibilityGuard>();
        visibility.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var handler = new SetShiftPreferenceCommandHandler(
            new ClientShiftPreferenceRepository(_context, Substitute.For<ILogger<ClientShiftPreference>>()), visibility, _unitOfWork);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftPreferenceCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<SetShiftPreferenceCommand>(), CancellationToken.None));

        var thrown = await Should.ThrowAsync<Exception>(() => new SetShiftPreferencesSkill(mediator).ExecuteAsync(
            Ctx(),
            new Dictionary<string, object>
            {
                ["clientId"] = Guid.NewGuid().ToString(),
                ["shiftId"] = Guid.NewGuid().ToString(),
                ["preference"] = "preferred"
            }));

        ShouldBeDomainForeignKeyViolation(thrown);
    }

    [Test]
    public async Task SecondSettingsRowOfTheSameType_FailsAsDomainDuplicate()
    {
        var type = Prefix + Guid.NewGuid().ToString("N");
        _context.Settings.Add(new SettingsEntity { Id = Guid.NewGuid(), Type = type, Value = string.Empty });
        await _unitOfWork.CompleteAsync();
        _context.ChangeTracker.Clear();

        _context.Settings.Add(new SettingsEntity { Id = Guid.NewGuid(), Type = type, Value = string.Empty });
        var thrown = await Should.ThrowAsync<Exception>(() => _unitOfWork.CompleteAsync());

        var domain = thrown.ShouldBeOfType<DatabaseUpdateException>();
        domain.IsDuplicate.ShouldBeTrue();
        thrown.ShouldNotBeAssignableTo<DbUpdateException>();
    }

    private static void ShouldBeDomainForeignKeyViolation(Exception thrown)
    {
        var domain = thrown.ShouldBeOfType<DatabaseUpdateException>();
        domain.IsForeignKeyViolation.ShouldBeTrue();
        thrown.ShouldNotBeAssignableTo<DbUpdateException>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanEditShifts" }
    };

    private async Task CleanupAsync()
    {
        SupervisorVisibilityScenario.EnsureIntegrationTestPrefix(Prefix);
        await _context.Database.ExecuteSqlRawAsync($"DELETE FROM settings WHERE starts_with(type, '{Prefix}');");
    }
}
