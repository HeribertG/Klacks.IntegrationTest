// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Az7 of the Klacksy-Autonomie test spec (docs/knowledge/klacksy-autonomie-testspezifikation-2026-08-28.md
/// §4, "Rückbau"): create_container_template's registered inverse (InverseSkillRegistry.cs:38-39,
/// delete_container_template) must restore the container's template state exactly - state-hash (I6) before
/// the create equals the hash after the inverse runs.
///
/// SCOPE, DECIDED WITH THE OWNER RATHER THAN PICKED SILENTLY (two AskUserQuestion rounds, 2026-08-29/30):
/// CreateContainerTemplateSkill and DeleteContainerTemplateSkill (Application/Skills/) contain NO
/// persistence logic of their own - every write goes through a real HTTP self-call
/// (IKlacksSelfApiClient.PostAsync/DeleteAsync against the API's own REST endpoints, including the
/// container edit lock). A "skill-level roundtrip" that substitutes IKlacksSelfApiClient would be a no-op:
/// nothing would actually be created or deleted, and any hash comparison would be true by construction.
/// The Owner chose to test one layer deeper instead: the REAL command handlers those self-calls resolve to
/// (PostContainerTemplatesCommandHandler, DeleteContainerTemplatesCommandHandler), called directly with a
/// real IContainerTemplateRepository/IUnitOfWork/ScheduleMapper against Postgres. IContainerLockRepository
/// and IUserService are substituted - the lock mechanism itself is not what Az7 is about, and stubbing
/// IsHeldBy true is the same simplification ShiftManipulationIntegrationTests already uses for skills that
/// are not under test. This proves the actual create/delete round-trip symmetry on real data; it does not
/// exercise the self-call, the container lock, or the skill parameter-binding layer above it.
///
/// I6 (ContainerTemplateStateHasher, Klacks.IntegrationTest/TestHelpers/) hashes exactly what these two
/// handlers touch: the container's ContainerTemplate rows, order-independent.
///
/// Cleanup deletes ONLY Shift/ContainerTemplate rows this fixture created, by its own name prefix.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ContainerTemplates;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.Proactive;

[TestFixture]
[Category("RealDatabase")]
public class EmptyContainerRollbackScenarioTests
{
    private const string TestPrefix = "INTEGRATION_TEST_AZ7_";

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Az7_DeleteContainerTemplateAfterCreate_RestoresTheStateHashToBeforeTheCreate()
    {
        var containerId = await GivenEmptyContainerAsync();

        var lockRepository = Substitute.For<IContainerLockRepository>();
        lockRepository
            .IsHeldBy(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var userService = Substitute.For<IUserService>();
        userService.GetId().Returns(Guid.NewGuid());
        userService.GetInstanceId().Returns(string.Empty);

        // Each phase gets its own DataBaseContext, mirroring the separate scoped DbContext each real
        // HTTP request would get. Reusing one context across Post and Delete would make the delete's
        // AsNoTracking() read collide with the still-tracked (Unchanged) entity from the preceding Add
        // + SaveChanges - an artifact of the test's single process, not of the real create/delete flow.
        string hashBefore;
        await using (var context = NewContext())
        {
            var repository = NewContainerTemplateRepository(context);
            hashBefore = await ContainerTemplateStateHasher.ComputeAsync(repository, containerId);
        }

        List<ContainerTemplateResource> created;
        string hashAfterCreate;
        await using (var context = NewContext())
        {
            var repository = NewContainerTemplateRepository(context);
            created = await NewPostHandler(context, repository, lockRepository, userService).Handle(
                new PostContainerTemplatesCommand(containerId, [BuildTemplateResource(containerId)]),
                CancellationToken.None);
            hashAfterCreate = await ContainerTemplateStateHasher.ComputeAsync(repository, containerId);
        }

        created.Count.ShouldBe(1);
        hashAfterCreate.ShouldNotBe(hashBefore, "Sanity check: creating a template must actually change the hashed state.");

        List<ContainerTemplateResource> deleted;
        string hashAfterDelete;
        await using (var context = NewContext())
        {
            var repository = NewContainerTemplateRepository(context);
            deleted = await NewDeleteHandler(context, repository, lockRepository, userService).Handle(
                new DeleteContainerTemplatesCommand(containerId), CancellationToken.None);
            hashAfterDelete = await ContainerTemplateStateHasher.ComputeAsync(repository, containerId);
        }

        deleted.Count.ShouldBe(1);
        hashAfterDelete.ShouldBe(
            hashBefore, "The inverse skill must restore the container's template state exactly, matching "
            + "DeleteContainerTemplateSkill's own documented guarantee for a container that held no "
            + "template before the create - exactly Az1's Given.");
    }

    private static ContainerTemplateResource BuildTemplateResource(Guid containerId) => new()
    {
        Id = Guid.Empty,
        ContainerId = containerId,
        FromTime = new TimeOnly(6, 0),
        UntilTime = new TimeOnly(14, 0),
        Weekday = 1,
        IsHoliday = false,
        IsWeekdayAndHoliday = false,
        StartBase = null,
        EndBase = null,
        TransportMode = ContainerTransportMode.ByCar,
        ContainerTemplateItems = []
    };

    private static async Task<Guid> GivenEmptyContainerAsync()
    {
        var containerId = Guid.NewGuid();
        var container = new Shift
        {
            Id = containerId,
            Name = TestPrefix + "container",
            Abbreviation = "AZ7",
            ShiftType = ShiftType.IsContainer,
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(1900, 1, 1),
            UntilDate = null,
            StartShift = new TimeOnly(6, 0),
            EndShift = new TimeOnly(14, 0),
            AnalyseToken = null,
            ScenarioSourceShiftId = null,
            IsDeleted = false
        };

        await using var context = NewContext();
        context.Shift.Add(container);
        await context.SaveChangesAsync();

        return containerId;
    }

    private static ContainerTemplateRepository NewContainerTemplateRepository(DataBaseContext context)
    {
        var collectionUpdateService = new EntityCollectionUpdateService(context);
        var containerTemplateService = new ContainerTemplateService(
            Substitute.For<IUnitOfWork>(), NullLogger<ContainerTemplateService>.Instance);

        return new ContainerTemplateRepository(
            context, NullLogger<ContainerTemplate>.Instance, collectionUpdateService, containerTemplateService);
    }

    private static PostContainerTemplatesCommandHandler NewPostHandler(
        DataBaseContext context, IContainerTemplateRepository repository,
        IContainerLockRepository lockRepository, IUserService userService) =>
        new(
            repository,
            new UnitOfWork(context, NullLogger<UnitOfWork>.Instance),
            new ScheduleMapper(),
            lockRepository,
            userService,
            NullLogger<PostContainerTemplatesCommandHandler>.Instance);

    private static DeleteContainerTemplatesCommandHandler NewDeleteHandler(
        DataBaseContext context, IContainerTemplateRepository repository,
        IContainerLockRepository lockRepository, IUserService userService) =>
        new(
            repository,
            new UnitOfWork(context, NullLogger<UnitOfWork>.Instance),
            new ScheduleMapper(),
            lockRepository,
            userService,
            NullLogger<DeleteContainerTemplatesCommandHandler>.Instance);

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM container_template_item WHERE container_template_id IN "
            + "(SELECT ct.id FROM container_template ct JOIN shift s ON s.id = ct.container_id WHERE s.name LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM container_template WHERE container_id IN (SELECT id FROM shift WHERE name LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE name LIKE {0}", TestPrefix + "%");
    }
}
