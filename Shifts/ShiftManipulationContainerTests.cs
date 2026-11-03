// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A container has no order, against PostgreSQL: every creation path ends with one row, status OriginalShift and no
/// OriginalId, the template skill works on that row, an administrator deletes it as a family of one, and the migration
/// ContainersHaveNoOrder converts live pairs with the exact shipped SQL, in the real plan and in a scenario, and leaves a
/// variant and every row it cannot decide alone.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Application.Validation.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Shifts;

public partial class ShiftManipulationIntegrationTests
{
    private const int TuesdayIso = 2;
    private static readonly DateOnly ContainerFrom = new(2026, 1, 1);

    private ShiftResource ContainerResource(string suffix, ShiftStatus status)
    {
        var resource = CreateTestShiftResource(suffix, status, fromDate: ContainerFrom);
        resource.ShiftType = ShiftType.IsContainer;
        resource.ClientId = null;
        resource.Groups = [new() { Id = _groupAId }];
        resource.Quantity = 1;
        resource.SumEmployees = 1;
        return resource;
    }

    private async Task<List<Shift>> RowsNamedAsync(string name)
    {
        _context.ChangeTracker.Clear();
        return await _context.Shift.IgnoreQueryFilters().AsNoTracking().Where(s => s.Name == name).ToListAsync();
    }

    private static void ShouldHaveTheOneRowShape(Shift row)
    {
        row.Status.ShouldBe(ShiftStatus.OriginalShift);
        row.OriginalId.ShouldBeNull();
        row.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task Post_ASealedContainer_StoresExactlyOneRowWithoutAnOrder()
    {
        var resource = ContainerResource("Container_Post", ShiftStatus.SealedOrder);

        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        var row = (await RowsNamedAsync(resource.Name)).ShouldHaveSingleItem();
        ShouldHaveTheOneRowShape(row);
        created!.Id.ShouldBe(row.Id);
        (await _context.Shift.AsNoTracking().CountAsync(s => s.OriginalId == row.Id)).ShouldBe(0);
    }

    [Test]
    public async Task Put_SealingADraftContainer_KeepsTheRowAndItsId_WithoutACopy()
    {
        var resource = ContainerResource("Container_Put", ShiftStatus.OriginalOrder);
        var draft = await _postHandler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);
        _context.ChangeTracker.Clear();
        (await RowsNamedAsync(resource.Name)).ShouldHaveSingleItem().Status.ShouldBe(ShiftStatus.OriginalOrder);

        var sealing = await LoadThroughTheWireAsync(draft!.Id);
        sealing.Status = ShiftStatus.SealedOrder;
        var saved = await _putHandler.Handle(new PutCommand<ShiftResource>(sealing), CancellationToken.None);

        saved!.Id.ShouldBe(draft.Id);
        ShouldHaveTheOneRowShape((await RowsNamedAsync(resource.Name)).ShouldHaveSingleItem());
    }

    [Test]
    public async Task SealService_ADraftContainer_ConfirmsTheOneRow_AndNamesItAsThePlannableShift()
    {
        var resource = ContainerResource("Container_Seal", ShiftStatus.OriginalOrder);
        var draft = await _postHandler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);
        _context.ChangeTracker.Clear();
        var service = new OrderSealingService(_shiftRepository, _unitOfWork, AllShiftsVisible());

        var outcome = await service.SealAsync(draft!.Id);

        outcome.IsSealed.ShouldBeTrue(outcome.FailureReason);
        outcome.PlannableShiftId.ShouldBe(draft.Id);
        ShouldHaveTheOneRowShape((await RowsNamedAsync(resource.Name)).ShouldHaveSingleItem());
    }

    [Test]
    public async Task FactoryRow_AsBundleAndTourPersistIt_IsOneRow_AndTheTemplateSkillWritesOnIt()
    {
        var name = $"{TestShiftPrefix}Container_Tour";
        var spec = new ContainerShiftSpec(
            name, "TOUR", new TimeOnly(7, 0), new TimeOnly(18, 0), ContainerFrom, null, 11m, null,
            new HashSet<int> { TuesdayIso },
            [new GroupItem { GroupId = _groupAId, ValidFrom = ContainerFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) }],
            "tester");

        var persisted = await _shiftRepository.AddWithSealedOrderHandling(ContainerShiftFactory.Build(spec));
        await _unitOfWork.CompleteAsync();

        ShouldHaveTheOneRowShape((await RowsNamedAsync(name)).ShouldHaveSingleItem());

        var result = await TemplateSkill().ExecuteAsync(
            new SkillExecutionContext { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), UserName = "tester", UserPermissions = [] },
            new Dictionary<string, object>
            {
                ["containerId"] = persisted.Id.ToString(),
                ["weekday"] = TuesdayIso,
                ["fromTime"] = "07:00",
                ["untilTime"] = "18:00"
            });

        result.Success.ShouldBeTrue(result.Message);
        _context.ChangeTracker.Clear();
        (await _context.Set<ContainerTemplate>().AsNoTracking().CountAsync(t => t.ContainerId == persisted.Id)).ShouldBe(1);
    }

    private CreateContainerTemplateSkill TemplateSkill()
    {
        var selfApi = Substitute.For<IKlacksSelfApiClient>();
        var validator = new ContainerTemplateResourceValidator(_shiftRepository);
        selfApi.PostAsync<ContainerLockResource>(
                Arg.Any<string>(), Arg.Any<object>(), Arg.Any<SkillExecutionContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SelfApiResult<ContainerLockResource>(
                true, new ContainerLockResource { Id = Guid.NewGuid(), Acquired = true }, StatusCodesOk, null));
        selfApi.PostAsync<List<ContainerTemplateResource>>(
                Arg.Any<string>(), Arg.Any<object>(), Arg.Any<SkillExecutionContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var posted = call.ArgAt<List<ContainerTemplateResource>>(1);
                (await validator.ValidateAsync(posted[0])).IsValid.ShouldBeTrue();
                var id = Guid.NewGuid();
                _context.Set<ContainerTemplate>().Add(new ContainerTemplate
                {
                    Id = id,
                    ContainerId = posted[0].ContainerId,
                    FromTime = posted[0].FromTime,
                    UntilTime = posted[0].UntilTime,
                    Weekday = posted[0].Weekday
                });
                await _context.SaveChangesAsync();
                posted[0].Id = id;
                return new SelfApiResult<List<ContainerTemplateResource>>(true, posted, StatusCodesOk, null);
            });

        var templates = new ContainerTemplateRepository(
            _context,
            Substitute.For<ILogger<ContainerTemplate>>(),
            new EntityCollectionUpdateService(_context),
            new ContainerTemplateService(_unitOfWork, NullLogger<ContainerTemplateService>.Instance));
        return new CreateContainerTemplateSkill(
            _shiftRepository, templates, selfApi, new Klacks.Api.Infrastructure.Services.Assistant.SelfApiRouteResolver(), AllShiftsVisible());
    }

    private const int StatusCodesOk = 200;

    [Test]
    public async Task Delete_AContainer_ByAnAdministrator_RemovesItsOneRow()
    {
        var resource = ContainerResource("Container_Delete", ShiftStatus.SealedOrder);
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var deleted = await DeleteHandlerFor(Roles.Admin).Handle(new DeleteCommand<ShiftResource>(created!.Id), CancellationToken.None);

        deleted!.Id.ShouldBe(created.Id);
        (await RowsNamedAsync(resource.Name)).ShouldHaveSingleItem().IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task Delete_AContainer_WithoutTheAdministratorRole_OrWithAWork_IsRefused()
    {
        var resource = ContainerResource("Container_DeleteRefused", ShiftStatus.SealedOrder);
        var created = await _postHandler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);
        _context.ChangeTracker.Clear();

        await Should.ThrowAsync<ShiftLockedException>(() =>
            _deleteHandler.Handle(new DeleteCommand<ShiftResource>(created!.Id), CancellationToken.None));

        _context.ChangeTracker.Clear();
        await AddWorkOnAsync(created!.Id, ContainerFrom);
        await Should.ThrowAsync<ShiftLockedException>(() =>
            DeleteHandlerFor(Roles.Admin).Handle(new DeleteCommand<ShiftResource>(created.Id), CancellationToken.None));

        _context.ChangeTracker.Clear();
        (await RowsNamedAsync(resource.Name)).ShouldHaveSingleItem().IsDeleted.ShouldBeFalse();
    }

    private Shift ContainerRow(string suffix, ShiftStatus status, Guid? originalId = null, Guid? token = null, Guid? source = null)
    {
        var row = _scheduleMapper.ToShiftEntity(CreateTestShiftResource(suffix, status, fromDate: ContainerFrom));
        row.ShiftType = ShiftType.IsContainer;
        row.ClientId = null;
        row.OriginalId = originalId;
        row.AnalyseToken = token;
        row.ScenarioSourceShiftId = source;
        return row;
    }

    private async Task<(Shift Sealed, Shift Plannable)> AddPairAsync(string suffix, Guid? token = null, (Guid Sealed, Guid Plannable)? source = null)
    {
        var sealedRow = ContainerRow($"{suffix}_Sealed", ShiftStatus.SealedOrder, token: token, source: source?.Sealed);
        var plannable = ContainerRow($"{suffix}_Plannable", ShiftStatus.OriginalShift, sealedRow.Id, token, source?.Plannable);
        _context.Shift.AddRange(sealedRow, plannable);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (sealedRow, plannable);
    }

    private async Task<Guid> AddScenarioRowAsync(string suffix, AnalyseScenarioKind kind)
    {
        var token = Guid.NewGuid();
        _context.Set<AnalyseScenario>().Add(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = token,
            Name = $"{TestShiftPrefix}{suffix}",
            Kind = kind,
            Status = AnalyseScenarioStatus.Active,
            FromDate = ContainerFrom,
            UntilDate = ContainerFrom.AddDays(30)
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return token;
    }

    private async Task RunMigrationAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(ContainersHaveNoOrder.ConversionSql);
        _context.ChangeTracker.Clear();
    }

    private async Task DeleteScenarioRowsAsync(params Guid[] tokens)
    {
        await _context.Set<AnalyseScenario>().IgnoreQueryFilters().Where(s => tokens.Contains(s.Token)).ExecuteDeleteAsync();
    }

    private async Task<Shift> AnyStoredAsync(Guid id)
    {
        _context.ChangeTracker.Clear();
        return await _context.Shift.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id);
    }

    [Test]
    public async Task Migration_ConvertsARealPair_KeepsThePlannableRow_AndSoftDeletesTheSealedRowWithItsLinks()
    {
        var pair = await AddPairAsync("Mig_Real");
        _context.Set<GroupItem>().AddRange(
            new GroupItem { Id = Guid.NewGuid(), ShiftId = pair.Sealed.Id, GroupId = _groupAId },
            new GroupItem { Id = Guid.NewGuid(), ShiftId = pair.Plannable.Id, GroupId = _groupAId });
        _context.Set<ShiftExpenses>().Add(new ShiftExpenses { Id = Guid.NewGuid(), ShiftId = pair.Sealed.Id, Amount = 5m, Description = TestShiftPrefix });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        await AddWorkOnAsync(pair.Plannable.Id, ContainerFrom);

        await RunMigrationAsync();
        await RunMigrationAsync();

        var plannable = await AnyStoredAsync(pair.Plannable.Id);
        ShouldHaveTheOneRowShape(plannable);
        var sealedRow = await AnyStoredAsync(pair.Sealed.Id);
        sealedRow.IsDeleted.ShouldBeTrue();
        sealedRow.DeletedTime.ShouldNotBeNull();
        sealedRow.CurrentUserDeleted.ShouldBe("migration:ContainersHaveNoOrder");
        (await _context.Set<GroupItem>().IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.ShiftId == pair.Sealed.Id)).IsDeleted.ShouldBeTrue();
        (await _context.Set<GroupItem>().IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.ShiftId == pair.Plannable.Id)).IsDeleted.ShouldBeFalse();
        (await _context.Set<ShiftExpenses>().IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.ShiftId == pair.Sealed.Id)).IsDeleted.ShouldBeTrue();
        (await _context.Work.AsNoTracking().CountAsync(w => w.ShiftId == pair.Plannable.Id && !w.IsDeleted)).ShouldBe(1);
    }

    [Test]
    public async Task Migration_ConvertsAScenarioPairInItsOwnWorld_AndLeavesAVariantPairAlone()
    {
        var scenarioToken = await AddScenarioRowAsync("Mig_Scenario", AnalyseScenarioKind.Scenario);
        var variantToken = await AddScenarioRowAsync("Mig_Variant", AnalyseScenarioKind.Variant);
        try
        {
            var real = await AddPairAsync("Mig_Source");
            var clone = await AddPairAsync("Mig_Clone", scenarioToken, (real.Sealed.Id, real.Plannable.Id));
            var variant = await AddPairAsync("Mig_Key", variantToken);

            await RunMigrationAsync();

            var clonedPlannable = await AnyStoredAsync(clone.Plannable.Id);
            clonedPlannable.OriginalId.ShouldBeNull();
            clonedPlannable.IsDeleted.ShouldBeFalse();
            clonedPlannable.AnalyseToken.ShouldBe(scenarioToken);
            clonedPlannable.ScenarioSourceShiftId.ShouldBe(real.Plannable.Id);
            (await AnyStoredAsync(clone.Sealed.Id)).IsDeleted.ShouldBeTrue();

            (await AnyStoredAsync(variant.Sealed.Id)).IsDeleted.ShouldBeFalse();
            (await AnyStoredAsync(variant.Plannable.Id)).OriginalId.ShouldBe(variant.Sealed.Id);
        }
        finally
        {
            await DeleteScenarioRowsAsync(scenarioToken, variantToken);
        }
    }

    [Test]
    public async Task Migration_RepointsAScenarioCloneOfOnlyTheSealedRow_AtTheSurvivingPlannableRow()
    {
        var scenarioToken = await AddScenarioRowAsync("Mig_HalfClone", AnalyseScenarioKind.Scenario);
        try
        {
            var real = await AddPairAsync("Mig_HalfSource");
            var halfClone = ContainerRow("Mig_HalfClone_Sealed", ShiftStatus.SealedOrder, token: scenarioToken, source: real.Sealed.Id);
            _context.Shift.Add(halfClone);
            await _context.SaveChangesAsync();
            _context.ChangeTracker.Clear();

            await RunMigrationAsync();
            await RunMigrationAsync();

            ShouldHaveTheOneRowShape(await AnyStoredAsync(real.Plannable.Id));
            (await AnyStoredAsync(real.Sealed.Id)).IsDeleted.ShouldBeTrue();
            var clone = await AnyStoredAsync(halfClone.Id);
            clone.IsDeleted.ShouldBeFalse();
            clone.Status.ShouldBe(ShiftStatus.OriginalShift);
            clone.ScenarioSourceShiftId.ShouldBe(real.Plannable.Id);
        }
        finally
        {
            await DeleteScenarioRowsAsync(scenarioToken);
        }
    }

    [Test]
    public async Task Migration_TurnsASealedRowWithoutAChildIntoTheOneRow_AndLeavesADraftAlone()
    {
        var alone = ContainerRow("Mig_Alone", ShiftStatus.SealedOrder);
        var draft = ContainerRow("Mig_Draft", ShiftStatus.OriginalOrder);
        _context.Shift.AddRange(alone, draft);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await RunMigrationAsync();

        ShouldHaveTheOneRowShape(await AnyStoredAsync(alone.Id));
        (await AnyStoredAsync(draft.Id)).Status.ShouldBe(ShiftStatus.OriginalOrder);
    }

    [Test]
    public async Task Migration_LeavesAPairItCannotDecide_WithACutPieceOrALiveReferenceOnTheSealedRow()
    {
        var withCut = await AddPairAsync("Mig_Cut");
        _context.Shift.Add(ContainerRow("Mig_Cut_Piece", ShiftStatus.SplitShift, withCut.Sealed.Id));
        var referenced = await AddPairAsync("Mig_Referenced");
        _context.Set<ContainerTemplate>().Add(new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = referenced.Sealed.Id,
            FromTime = new TimeOnly(7, 0),
            UntilTime = new TimeOnly(18, 0),
            Weekday = TuesdayIso
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await RunMigrationAsync();

        (await AnyStoredAsync(withCut.Sealed.Id)).IsDeleted.ShouldBeFalse();
        (await AnyStoredAsync(withCut.Plannable.Id)).OriginalId.ShouldBe(withCut.Sealed.Id);
        (await AnyStoredAsync(referenced.Sealed.Id)).IsDeleted.ShouldBeFalse();
        (await AnyStoredAsync(referenced.Plannable.Id)).OriginalId.ShouldBe(referenced.Sealed.Id);
    }
}
