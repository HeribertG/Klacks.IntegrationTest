// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Az0 of the Klacksy-Autonomie test spec (docs/knowledge/klacksy-autonomie-testspezifikation-2026-08-28.md
/// §4, "Meldelücke"): proves the REAL EmptyContainerDetector, wired against the REAL Postgres dev database
/// through its real ShiftRepository/ContainerTemplateRepository/ShiftGroupScopeReadRepository/
/// AgentConditionRepository, surfaces newly created empty containers - the one claim
/// EmptyContainerDetectorTests.cs (Klacks.UnitTest, EF InMemory) cannot make, because that provider accepts
/// LINQ shapes Npgsql has rejected before (see ShiftGroupScopeReadRepository's own doc comment) and because
/// the starvation bug this detector was fixed for (see EmptyContainerDetector's doc comment: 260 real
/// candidates sharing one FromDate, 210 of them never reaching the ledger across 14 real ticks) is a
/// property of ordering against real data volume, not of the LINQ itself.
///
/// Deliberately NOT a repeat of the original incident's shape (see EmptyContainerActionScenarioTests.cs's
/// doc comment): this fixture calls DetectAsync() directly and reads its return value - it never ingests
/// that return value into the ledger, so any real candidates the scan also returns are observed, never
/// written. Az0 is the one scenario in this spec allowed to touch the real empty_container candidate set,
/// and only by asserting on its own seeded ids among the returned events, never on a global count.
///
/// Seeded containers get a FromDate far older than 2025-01-01 (the shared FromDate of every real candidate
/// measured live in the dev database on 2026-08-29, still 260 candidates, 155 already open in the ledger at
/// the time of writing). The detector's first slice orders oldest-first before applying its cap, so an
/// older FromDate wins that ordering deterministically regardless of how the real backlog moves between
/// runs - this fixture does not depend on, or exercise, the second (RecentlyCreatedSlots) slice at all,
/// which EmptyContainerDetectorTests.cs already covers exhaustively against EF InMemory.
///
/// Cleanup deletes ONLY Shift rows named with this fixture's own prefix.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.Proactive;

[TestFixture]
[Category("RealDatabase")]
public class EmptyContainerDetectorLiveScanTests
{
    private const string TestPrefix = "INTEGRATION_TEST_AZ0_";
    private const int SeededContainerCount = 20;

    private static readonly DateOnly FarPastFromDate = new(1900, 1, 1);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
    }

    [Test]
    public async Task DetectAsync_AgainstTheRealDatabase_SurfacesEveryFreshlySeededEmptyContainer()
    {
        var seededIds = await GivenSeededEmptyContainersAsync();

        await using var context = NewContext();
        var events = await NewDetector(context).DetectAsync();
        var eventShiftIds = events
            .Cast<EmptyContainerTriggerEvent>()
            .Select(e => e.ShiftId)
            .ToHashSet();

        seededIds.ShouldBeSubsetOf(
            eventShiftIds,
            "Every freshly seeded empty container has the oldest FromDate of any candidate in the "
            + "database, so the detector's oldest-first cap must always include all of them - proving "
            + "the real Npgsql-translated query surfaces them, regardless of how large the real backlog "
            + "has grown.");
    }

    private static async Task<HashSet<Guid>> GivenSeededEmptyContainersAsync()
    {
        await using var context = NewContext();

        var containers = Enumerable.Range(0, SeededContainerCount)
            .Select(_ => new Shift
            {
                Id = Guid.NewGuid(),
                Name = TestPrefix + "container",
                Abbreviation = "AZ0",
                ShiftType = ShiftType.IsContainer,
                Status = ShiftStatus.OriginalShift,
                FromDate = FarPastFromDate,
                UntilDate = null,
                StartShift = new TimeOnly(8, 0),
                EndShift = new TimeOnly(16, 0),
                AnalyseToken = null,
                ScenarioSourceShiftId = null,
                IsDeleted = false
            })
            .ToList();

        await context.Shift.AddRangeAsync(containers);
        await context.SaveChangesAsync();

        return containers.Select(c => c.Id).ToHashSet();
    }

    private static EmptyContainerDetector NewDetector(DataBaseContext context)
    {
        var shiftLogger = NullLogger<Shift>.Instance;
        var containerTemplateLogger = NullLogger<ContainerTemplate>.Instance;
        var detectorLogger = NullLogger<EmptyContainerDetector>.Instance;

        var collectionUpdateService = new EntityCollectionUpdateService(context);
        var shiftRepository = new ShiftRepository(
            context,
            shiftLogger,
            Substitute.For<IShiftQueryPipelineService>(),
            Substitute.For<IShiftGroupManagementService>(),
            collectionUpdateService,
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper());

        var containerTemplateService = new ContainerTemplateService(
            Substitute.For<IUnitOfWork>(), NullLogger<ContainerTemplateService>.Instance);
        var containerTemplateRepository = new ContainerTemplateRepository(
            context, containerTemplateLogger, collectionUpdateService, containerTemplateService);

        var groupScopeReader = new ShiftGroupScopeReadRepository(context);
        var agentConditionRepository = new AgentConditionRepository(context);

        return new EmptyContainerDetector(
            shiftRepository, containerTemplateRepository, groupScopeReader, agentConditionRepository,
            TimeProvider.System, detectorLogger);
    }

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
            "DELETE FROM shift WHERE name LIKE {0}", TestPrefix + "%");
    }
}
