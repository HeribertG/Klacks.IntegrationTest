// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end proof of the Prepare rung against the real PostgreSQL database, using a SYNTHETIC trigger
/// kind rather than one of the shipped ones: after the 2026-08-25 decision that open_order,
/// empty_container and uncut_fullday_shift are Execute-only, no real kind is scenario-capable, so the
/// generic infrastructure has to be demonstrated on a kind that exists only here.
///
/// What only a real database can show, and what this fixture is therefore for: the AnalyseScenario row
/// really lands with CreatedByUser = "klacksy" (the audit would otherwise fall to DataBaseContext's
/// "Anonymous" outside an HTTP request), the ledger row really carries the ScenarioId and Status
/// Prepared afterwards, and above all the ORDERING holds - AgentConditionRepository.TryTransitionAsync
/// opens its own database transaction, so the unit of work around the scenario must have committed
/// before it runs. The unit tests above this cannot see that: their fakes have no transactions to nest.
/// The rejection half then proves the write-back through the real command handler.
///
/// The fixture's own EMPTY group is what keeps the clone harmless. A null group would make
/// CloneScenarioDataWithMapsAsync clone every shift in the installation (CloneShifts filters by group,
/// not by date), which in this shared database means the dev app's whole shift catalogue. A childless
/// group with no memberships resolves to an empty shift set, so the scenario is created with nothing
/// cloned under its token - which is exactly what the persistence claims above need and no more.
///
/// NOT proven here: the per-planner notification. It needs the identity stack for a live audience, is
/// unrelated to the persistence claims above, and is covered by ConditionScenarioPreparationServiceTests.
///
/// Cleanup deletes ONLY rows this fixture created - the dev app shares this database. Conditions and
/// their events go by the INTEGRATION_TEST_ trigger-kind prefix, the group by its prefixed name, and
/// scenarios by the ids this run generated. Nothing is ever deleted by author or by a
/// production-plausible value, which is what the 2026-07-03 incident cost.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Handlers.AnalyseScenarios;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class ConditionScenarioPreparationIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PREPARE_";
    private const string SyntheticKind = TestPrefix + "demo_kind";

    private static readonly DateOnly FromDate = new(2031, 3, 3);
    private static readonly DateOnly UntilDate = new(2031, 3, 3);

    private readonly List<Guid> _createdScenarioIds = new();

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
    public async Task PreparingASyntheticFinding_WritesAKlacksyAuthoredScenarioAndPointsTheLedgerAtIt()
    {
        // Arrange
        var condition = await GivenConditionAsync(AgentConditionStatus.Reported);

        // Act
        await using var context = NewContext();
        var result = await NewService(context)
            .PrepareScenarioForConditionAsync(condition, new ConditionScenarioRequest(FromDate, UntilDate));

        // Assert
        result.Outcome.ShouldBe(ConditionScenarioPreparationOutcome.Prepared);
        result.ScenarioId.ShouldNotBeNull();
        Remember(result.ScenarioId!.Value);

        await using var verify = NewContext();
        var scenario = await verify.Set<AnalyseScenario>().AsNoTracking()
            .SingleAsync(s => s.Id == result.ScenarioId!.Value);
        scenario.CreatedByUser.ShouldBe(KlacksyIdentity.SystemUserName);
        scenario.Status.ShouldBe(AnalyseScenarioStatus.Active);
        scenario.GroupId.ShouldBe(condition.GroupId);
        scenario.FromDate.ShouldBe(FromDate);
        scenario.UntilDate.ShouldBe(UntilDate);
        scenario.Name.ShouldContain(KlacksyIdentity.SystemUserName);

        var stored = await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == condition.Id);
        stored.Status.ShouldBe(AgentConditionStatus.Prepared);
        stored.ScenarioId.ShouldBe(scenario.Id);
        stored.HandlingKind.ShouldBe(AgentConditionHandlingKind.ScenarioPrepared);
        stored.HandledAtUtc.ShouldNotBeNull();

        // The transition's audit event is written inside the ledger's own database transaction. Its
        // presence is what shows that transaction ran at all: EF refuses to open one while an ambient
        // transaction is active, so this row could not exist if the scenario's unit of work had not
        // already committed and closed.
        var events = await verify.AgentConditionEvents.AsNoTracking()
            .Where(e => e.ConditionId == condition.Id)
            .ToListAsync();
        events.ShouldContain(e => e.EventType == AgentConditionStatus.Prepared.ToString());
    }

    [Test]
    public async Task RejectingThePreparedScenario_ClosesTheFindingWithAReason()
    {
        // Arrange
        var condition = await GivenConditionAsync(AgentConditionStatus.Reported);
        await using var prepareContext = NewContext();
        var prepared = await NewService(prepareContext)
            .PrepareScenarioForConditionAsync(condition, new ConditionScenarioRequest(FromDate, UntilDate));
        prepared.Outcome.ShouldBe(ConditionScenarioPreparationOutcome.Prepared);
        Remember(prepared.ScenarioId!.Value);

        // Act
        await using var rejectContext = NewContext();
        var rejected = await NewRejectHandler(rejectContext).Handle(
            new RejectAnalyseScenarioCommand(prepared.ScenarioId!.Value, RejectReason.CoverageDrop, "not this way"),
            CancellationToken.None);

        // Assert
        rejected.ShouldBeTrue();

        await using var verify = NewContext();
        var scenario = await verify.Set<AnalyseScenario>().AsNoTracking()
            .SingleAsync(s => s.Id == prepared.ScenarioId!.Value);
        scenario.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
        scenario.RejectReason.ShouldBe(RejectReason.CoverageDrop);

        var stored = await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == condition.Id);
        stored.Status.ShouldBe(AgentConditionStatus.Rejected);
        stored.RejectReason.ShouldBe(AgentConditionRejectReason.WrongThisTime);
        stored.ScenarioId.ShouldBe(scenario.Id);

        var events = await verify.AgentConditionEvents.AsNoTracking()
            .Where(e => e.ConditionId == condition.Id)
            .ToListAsync();
        events.ShouldContain(e => e.EventType == AgentConditionStatus.Rejected.ToString());
    }

    [Test]
    public async Task RejectingAHumanAuthoredScenario_LeavesEveryLedgerRowAlone()
    {
        // Arrange - a scenario nobody prepared for a finding, which is what almost every scenario is.
        var condition = await GivenConditionAsync(AgentConditionStatus.Reported);
        var scenarioId = await GivenPlainScenarioAsync();

        // Act
        await using var rejectContext = NewContext();
        (await NewRejectHandler(rejectContext).Handle(
            new RejectAnalyseScenarioCommand(scenarioId, RejectReason.Other), CancellationToken.None))
            .ShouldBeTrue();

        // Assert
        await using var verify = NewContext();
        (await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == condition.Id))
            .Status.ShouldBe(AgentConditionStatus.Reported);
    }

    [Test]
    public async Task PreparingAFindingThatIsStillDetected_CreatesNothing()
    {
        // Arrange - Prepared is reachable only from Reported; going straight from Detected would be an
        // illegal transition, so the service has to refuse before it creates anything at all.
        var condition = await GivenConditionAsync(AgentConditionStatus.Detected);

        // Act
        await using var context = NewContext();
        var result = await NewService(context)
            .PrepareScenarioForConditionAsync(condition, new ConditionScenarioRequest(FromDate, UntilDate));

        // Assert
        result.Outcome.ShouldBe(ConditionScenarioPreparationOutcome.NotPreparable);
        result.ScenarioId.ShouldBeNull();

        // Counted inside this fixture's own freshly created group, never over the whole table: the dev
        // app shares this database and writes scenarios of its own, so a global count would turn its
        // activity into a failure of this feature.
        (await CountScenariosInGroupAsync(condition.GroupId!.Value)).ShouldBe(0);

        await using var verify = NewContext();
        (await verify.AgentConditions.AsNoTracking().SingleAsync(c => c.Id == condition.Id))
            .Status.ShouldBe(AgentConditionStatus.Detected);
    }

    private void Remember(Guid scenarioId)
    {
        _createdScenarioIds.Add(scenarioId);
    }

    private static ConditionScenarioPreparationService NewService(DataBaseContext context)
    {
        var audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        audienceResolver.GetPlanningUserIdsForGroupAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<string>>(_ => new HashSet<string>());

        return new ConditionScenarioPreparationService(
            new AnalyseScenarioRepository(context, NullLogger<AnalyseScenario>.Instance),
            new AnalyseScenarioService(context),
            new UnitOfWork(context, NullLogger<UnitOfWork>.Instance),
            new AgentConditionLedgerService(
                new AgentConditionRepository(context),
                TimeProvider.System,
                NullLogger<AgentConditionLedgerService>.Instance),
            Substitute.For<IAgentTriggerService>(),
            audienceResolver,
            TimeProvider.System,
            NullLogger<ConditionScenarioPreparationService>.Instance);
    }

    private static RejectAnalyseScenarioCommandHandler NewRejectHandler(DataBaseContext context) =>
        new(
            new AnalyseScenarioRepository(context, NullLogger<AnalyseScenario>.Instance),
            new AnalyseScenarioService(context),
            new UnitOfWork(context, NullLogger<UnitOfWork>.Instance),
            Substitute.For<IWizardRunCaptureRepository>(),
            new AgentConditionRepository(context),
            new AgentConditionLedgerService(
                new AgentConditionRepository(context),
                TimeProvider.System,
                NullLogger<AgentConditionLedgerService>.Instance),
            Substitute.For<IHttpContextAccessor>(),
            NullLogger<RejectAnalyseScenarioCommandHandler>.Instance);

    /// <summary>
    /// A childless group with no memberships. GetGroupHierarchyIdsAsync resolves it to itself alone and
    /// CloneShifts then finds no group_item rows, so the clone stays empty - see the fixture remarks.
    /// </summary>
    private static async Task<Group> GivenEmptyGroupAsync()
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "group",
            ValidFrom = DateTime.UtcNow
        };

        await using var context = NewContext();
        context.Group.Add(group);
        await context.SaveChangesAsync();

        return group;
    }

    private static async Task<AgentCondition> GivenConditionAsync(AgentConditionStatus status)
    {
        var group = await GivenEmptyGroupAsync();
        var nowUtc = DateTime.UtcNow;
        var condition = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = SyntheticKind,
            Fingerprint = SyntheticKind + ":" + Guid.NewGuid(),
            Severity = AgentTriggerSeverity.Medium,
            GroupId = group.Id,
            Status = status,
            DetectedAtUtc = nowUtc,
            LastSeenAtUtc = nowUtc,
            PayloadJson = "{}"
        };

        await using var context = NewContext();
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        return condition;
    }

    private async Task<Guid> GivenPlainScenarioAsync()
    {
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + Guid.NewGuid(),
            FromDate = FromDate,
            UntilDate = UntilDate,
            Token = Guid.NewGuid(),
            CreatedByUser = TestPrefix + "human"
        };

        await using var context = NewContext();
        context.Set<AnalyseScenario>().Add(scenario);
        await context.SaveChangesAsync();
        Remember(scenario.Id);

        return scenario.Id;
    }

    private static async Task<int> CountScenariosInGroupAsync(Guid groupId)
    {
        await using var context = NewContext();
        return await context.Set<AnalyseScenario>().IgnoreQueryFilters().CountAsync(s => s.GroupId == groupId);
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private async Task CleanupAsync()
    {
        await using var context = NewContext();

        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_condition_events WHERE condition_id IN (SELECT id FROM agent_conditions WHERE trigger_kind LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE trigger_kind LIKE {0}",
            TestPrefix + "%");

        foreach (var scenarioId in _createdScenarioIds)
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM analyse_scenarios WHERE id = {0}", scenarioId);
        }

        _createdScenarioIds.Clear();

        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE name LIKE {0}",
            TestPrefix + "%");
    }
}
