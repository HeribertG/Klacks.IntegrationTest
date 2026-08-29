// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Az1, Az2, Az9 and Az10 of the Klacksy-Autonomie test spec
/// (docs/knowledge/klacksy-autonomie-testspezifikation-2026-08-28.md §4): governance at Execute, three
/// empty_container-shaped findings, the REAL EmptyContainerRemediationBinder and create_container_template
/// argument shape end to end - but a SYNTHETIC trigger kind, a CAPTURING fake executor rather than the real
/// skill, and a stubbed IProactiveGovernanceResolver rather than a real settings row, for reasons an
/// incident on 2026-08-29 made concrete rather than theoretical:
///
/// Az2 (Idempotenz) reuses the Az1 fixture unchanged and adds two ways the same three conditions could be
/// acted on twice: the same service instance ticking again after everything is already Executed (Az2a), and
/// two instances racing the same tick concurrently via Task.WhenAll (Az2b, same double-dispatcher proof as
/// AgentConditionActionServiceIntegrationTests.TwoDispatchersOnTheSameCondition_ProduceExactlyOneExecution,
/// extended from one synthetic condition to three real-binder-shaped ones). Both assert the executor sees
/// exactly 3 invocations total and agent_condition_events carries exactly one Prepared and one Executed
/// event per condition - never a duplicate from either path.
///
/// INCIDENT, KEPT AS A RECORD: the first version of this fixture used the real AgentTriggerKinds.EmptyContainer
/// kind against the real ConditionRemediationRegistry. AgentConditionRepository.GetActionableByKindAsync
/// filters ONLY by TriggerKind and status - it has no fingerprint scoping, because production is SUPPOSED to
/// pick up every open row of a kind. The dev database already carries real empty_container Reported rows
/// from the live detector's own earlier runs (145 at the time of writing); two test runs against the real
/// kind pulled 5 of them each into this fixture's tick cap alongside the 3 seeded rows and "executed" them
/// with the fake executor, silently corrupting 10 real ledger rows into a false Executed state (Prepared and
/// Executed events, AttemptCount, LastAttemptAtUtc). No real remediation ran and no real report was sent -
/// the executor and reporter were substitutes throughout - but the ledger rows themselves are real, shared
/// state the dev app's own detector and reports read. All 10 were identified from agent_condition_events
/// (each had only Detected/Reported before this fixture's two runs) and repaired by hand back to
/// Status=Reported/HandlingKind=None/AttemptCount=0 with the two spurious events removed.
///
/// FIX: this fixture now uses a SYNTHETIC kind (TestPrefix-namespaced) registered through a private
/// SingleKindRegistry wrapping the REAL EmptyContainerRemediationBinder - so the binder's weekday/argument
/// logic is still exercised unchanged, but AgentConditionRepository's kind-scoped query can never again see
/// real empty_container rows, no matter how many accumulate in the shared dev database. Az0 is the ONLY
/// scenario in this spec allowed to touch the real kind, and only by asserting on its own seeded IDs, never
/// on a global ledger count - see its own fixture once written.
///
/// PayloadJson is still built from the REAL EmptyContainerTriggerEvent.Payload (round-tripped through JSON
/// exactly like the production tick does, see EmptyContainerRemediationBinderTests.RoundTrip in
/// Klacks.UnitTest), so the binder under test sees production-shaped data without ever touching the
/// shift/container_template tables. Governance is a direct IProactiveGovernanceResolver substitute, following
/// AgentConditionActionServiceIntegrationTests.NewService's established pattern - a settings-table row for
/// KLACKSY_PROACTIVE_AUTONOMY_LEVEL is a singleton the INTEGRATION_TEST_ prefix cleanup rule cannot legally
/// touch, so this fixture never writes one.
///
/// Cleanup deletes ONLY rows this fixture created, by its own fingerprint prefix.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.Proactive;

[TestFixture]
[Category("RealDatabase")]
public class EmptyContainerActionScenarioTests
{
    private const string TestPrefix = "INTEGRATION_TEST_AZ1_";
    private const string Kind = TestPrefix + "empty_container_like";

    private static readonly Guid OwnerUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime FarPastUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task Az1_ThreeEmptyContainersAtExecute_AreAllExecutedWithTheirOwnContainerIdAndLowestWeekday()
    {
        var containerA = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [3, 5], startShift: new TimeOnly(6, 0), endShift: new TimeOnly(14, 0));
        var containerB = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [1], startShift: new TimeOnly(7, 0), endShift: new TimeOnly(15, 30));
        var containerC = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [2, 4, 6], startShift: new TimeOnly(8, 15), endShift: new TimeOnly(16, 0));

        var executor = new CapturingSkillExecutor();
        await using var context = NewContext();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await NewService(context, executor, reporter, ProactiveMaxAction.Execute)
            .RunAsync(CancellationToken.None);

        result.Executed.ShouldBe(3);

        await using var verify = NewContext();
        var stored = await verify.AgentConditions
            .Where(c => c.Fingerprint.StartsWith(TestPrefix))
            .ToListAsync();
        stored.Count.ShouldBe(3);
        stored.ShouldAllBe(c => c.Status == AgentConditionStatus.Executed);

        executor.Invocations.Count.ShouldBe(3);
        executor.Invocations.ShouldAllBe(invocation => invocation.SkillName == CreateContainerTemplateParameters.SkillName);

        AssertInvocation(executor, containerA.Id, expectedWeekday: 3);
        AssertInvocation(executor, containerB.Id, expectedWeekday: 1);
        AssertInvocation(executor, containerC.Id, expectedWeekday: 2);

        await reporter.Received(3).ReportAsync(OwnerUserId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Az10_StufeOnePrepareOnANonScenarioCapableRemediation_NeverExecutesAndLeavesTheLedgerReported()
    {
        // Global Stufe 1 maps to Prepare (ProactiveGovernanceDefaults.MapAutonomyLevel); empty_container's
        // real remediation is Execute-only (IsScenarioCapable: false, see ConditionRemediationRegistry),
        // and this fixture's SingleKindRegistry mirrors that default. Prepare on a non-scenario-capable
        // remediation must behave like Hint - report and wait - never execute and never touch the ledger.
        var container = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [4], startShift: new TimeOnly(9, 0), endShift: new TimeOnly(17, 0));

        var executor = new CapturingSkillExecutor();
        await using var context = NewContext();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await NewService(context, executor, reporter, ProactiveMaxAction.Prepare)
            .RunAsync(CancellationToken.None);

        result.Executed.ShouldBe(0);
        executor.Invocations.ShouldBeEmpty();

        await using var verify = NewContext();
        var stored = await verify.AgentConditions.SingleAsync(c => c.Id == container.Id);
        stored.Status.ShouldBe(AgentConditionStatus.Reported);

        await reporter.DidNotReceive().ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Az9_AFindingInGroupB_NeverReachesPlannerAsScopeAndOnlyActsUnderPlannerBsIdentity()
    {
        // Two halves of Az9: the scope query a planner-relevant skill reads from (GetOpenForScopeAsync,
        // already proven against real Postgres in AgentConditionRepositoryScopedQueryIntegrationTests)
        // must withhold a Group-B-scoped row from a caller whose visible roots are Group A only; and the
        // action dispatcher, when it does act, must borrow ONLY Planner B's rights - governance's
        // ResponsibleOwnerUserId is what IProactiveActionIdentityProvider is asked to resolve for, so
        // asserting the identity call was made for plannerB proves "im Auftrag von Planer B" directly,
        // without needing a real AppUser/Group membership join this repository method does not use.
        var groupA = await GivenGroupAsync();
        var groupB = await GivenGroupAsync();
        var plannerB = Guid.NewGuid();

        var condition = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [5], startShift: new TimeOnly(6, 0), endShift: new TimeOnly(14, 0), groupId: groupB.Id);

        await using (var scopeContext = NewContext())
        {
            var scopeRepository = new AgentConditionRepository(scopeContext);

            var plannerAView = await scopeRepository.GetOpenForScopeAsync(
                isUnrestricted: false, visibleRootIds: new HashSet<Guid> { groupA.Id }, take: 50);
            plannerAView.Select(c => c.Id).ShouldNotContain(condition.Id);

            var plannerBView = await scopeRepository.GetOpenForScopeAsync(
                isUnrestricted: false, visibleRootIds: new HashSet<Guid> { groupB.Id }, take: 50);
            plannerBView.Select(c => c.Id).ShouldContain(condition.Id);
        }

        var executor = new CapturingSkillExecutor();
        await using var context = NewContext();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Resolved(
                new SkillExecutionContext
                {
                    UserId = plannerB,
                    TenantId = Guid.Empty,
                    UserName = KlacksyIdentity.SystemUserName,
                    UserPermissions = ["some.permission"],
                    BypassAutonomyGate = true
                },
                ["some.permission"]));

        await NewService(context, executor, reporter, ProactiveMaxAction.Execute, plannerB, identityProvider)
            .RunAsync(CancellationToken.None);

        await identityProvider.Received(1).ResolveForSkillAsync(
            plannerB, condition.Id, CreateContainerTemplateParameters.SkillName, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Az2a_TheSameTickRunTwiceSequentially_ExecutesEachConditionExactlyOnce()
    {
        var containerA = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [3, 5], startShift: new TimeOnly(6, 0), endShift: new TimeOnly(14, 0));
        var containerB = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [1], startShift: new TimeOnly(7, 0), endShift: new TimeOnly(15, 30));
        var containerC = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [2, 4, 6], startShift: new TimeOnly(8, 15), endShift: new TimeOnly(16, 0));

        var executor = new CapturingSkillExecutor();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await using (var firstContext = NewContext())
        {
            var firstResult = await NewService(firstContext, executor, reporter, ProactiveMaxAction.Execute)
                .RunAsync(CancellationToken.None);
            firstResult.Executed.ShouldBe(3);
        }

        await using (var secondContext = NewContext())
        {
            var secondResult = await NewService(secondContext, executor, reporter, ProactiveMaxAction.Execute)
                .RunAsync(CancellationToken.None);
            secondResult.Executed.ShouldBe(
                0, "The second tick finds all three conditions already Executed, a terminal status - "
                + "GetActionableByKindAsync must not reclaim them.");
        }

        await AssertExactlyThreeExecutionsAsync(executor, containerA.Id, containerB.Id, containerC.Id);
    }

    [Test]
    public async Task Az2b_TwoConcurrentServiceInstancesOnTheSameTick_ExecuteEachConditionExactlyOnce()
    {
        var containerA = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [3, 5], startShift: new TimeOnly(6, 0), endShift: new TimeOnly(14, 0));
        var containerB = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [1], startShift: new TimeOnly(7, 0), endShift: new TimeOnly(15, 30));
        var containerC = await GivenEmptyContainerConditionAsync(
            isoWeekdays: [2, 4, 6], startShift: new TimeOnly(8, 15), endShift: new TimeOnly(16, 0));

        var executor = new CapturingSkillExecutor();
        var reporter = Substitute.For<IProactiveActionReporter>();
        reporter.ReportAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await using var firstContext = NewContext();
        await using var secondContext = NewContext();

        var first = NewService(firstContext, executor, reporter, ProactiveMaxAction.Execute)
            .RunAsync(CancellationToken.None);
        var second = NewService(secondContext, executor, reporter, ProactiveMaxAction.Execute)
            .RunAsync(CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        (results[0].Executed + results[1].Executed).ShouldBe(
            3, "The claim is a compare-and-swap (Reported->Prepared, ExecuteUpdateAsync). Whichever "
            + "instance loses the race must see zero rows affected, not throw a unique-violation.");

        await AssertExactlyThreeExecutionsAsync(executor, containerA.Id, containerB.Id, containerC.Id);
    }

    private static async Task AssertExactlyThreeExecutionsAsync(
        CapturingSkillExecutor executor, Guid containerAId, Guid containerBId, Guid containerCId)
    {
        executor.Invocations.Count.ShouldBe(3, "Neither a second tick nor a losing concurrent instance may re-invoke the skill.");
        AssertInvocation(executor, containerAId, expectedWeekday: 3);
        AssertInvocation(executor, containerBId, expectedWeekday: 1);
        AssertInvocation(executor, containerCId, expectedWeekday: 2);

        await using var verify = NewContext();
        var stored = await verify.AgentConditions
            .Where(c => c.Fingerprint.StartsWith(TestPrefix))
            .ToListAsync();
        stored.Count.ShouldBe(3);
        stored.ShouldAllBe(c => c.Status == AgentConditionStatus.Executed);

        var events = await verify.AgentConditionEvents
            .Where(e => stored.Select(c => c.Id).Contains(e.ConditionId))
            .AsNoTracking()
            .ToListAsync();
        events.Count(e => e.EventType == AgentConditionStatus.Prepared.ToString()).ShouldBe(
            3, "No duplicate dispatch: exactly one Prepared event per condition, never two from a race or a repeat tick.");
        events.Count(e => e.EventType == AgentConditionStatus.Executed.ToString()).ShouldBe(3);
    }

    private static void AssertInvocation(CapturingSkillExecutor executor, Guid containerId, int expectedWeekday)
    {
        var invocation = executor.Invocations.SingleOrDefault(
            i => (string)i.Parameters[CreateContainerTemplateParameters.ContainerId] == containerId.ToString());

        invocation.ShouldNotBeNull($"No skill invocation carried containerId {containerId}.");
        invocation.Parameters[CreateContainerTemplateParameters.Weekday].ShouldBe(expectedWeekday);
    }

    private static async Task<AgentCondition> GivenEmptyContainerConditionAsync(
        IReadOnlyCollection<int> isoWeekdays, TimeOnly startShift, TimeOnly endShift, Guid? groupId = null)
    {
        var shiftId = Guid.NewGuid();
        var triggerEvent = new EmptyContainerTriggerEvent(
            shiftId,
            TestPrefix + "container",
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            [],
            new ContainerScheduleSnapshot(startShift, endShift, isoWeekdays, IsHoliday: false, IsWeekdayAndHoliday: false),
            IsPeriodActive: true);

        var payloadJson = JsonSerializer.Serialize(triggerEvent.Payload);

        var condition = new AgentCondition
        {
            Id = shiftId,
            TriggerKind = Kind,
            Fingerprint = TestPrefix + shiftId,
            EntityId = shiftId,
            GroupId = groupId,
            Severity = AgentTriggerSeverity.High,
            Status = AgentConditionStatus.Reported,
            // Far past, not "now": GetOpenForScopeAsync orders oldest-first within a severity tier, and
            // the dev DB carries thousands of real conditions (Az9 tripped over this once - see the
            // fixture-level doc comment). Dating this far in the past guarantees it sorts first
            // regardless of real volume, matching AgentConditionRepositoryScopedQueryIntegrationTests.
            DetectedAtUtc = FarPastUtc,
            LastSeenAtUtc = FarPastUtc,
            PayloadJson = payloadJson,
        };

        await using var context = NewContext();
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();

        return condition;
    }

    private static async Task<Group> GivenGroupAsync()
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "group",
            Root = null,
            ValidFrom = DateTime.UtcNow,
        };

        await using var context = NewContext();
        context.Group.Add(group);
        await context.SaveChangesAsync();

        return group;
    }

    private static AgentConditionActionService NewService(
        DataBaseContext context, ISkillExecutor executor, IProactiveActionReporter reporter,
        ProactiveMaxAction globalAutonomyCap, Guid? ownerUserId = null,
        IProactiveActionIdentityProvider? identityProviderOverride = null)
    {
        var owner = ownerUserId ?? OwnerUserId;
        var repository = new AgentConditionRepository(context);
        var ledger = new AgentConditionLedgerService(
            repository, TimeProvider.System, NullLogger<AgentConditionLedgerService>.Instance);

        var governance = Substitute.For<IProactiveGovernanceResolver>();
        governance
            .ResolveAsync(Kind, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDecision(
                TriggerKind: Kind,
                GroupId: null,
                EffectiveMaxAction: globalAutonomyCap < ProactiveMaxAction.Execute ? globalAutonomyCap : ProactiveMaxAction.Execute,
                ConfiguredMaxAction: ProactiveMaxAction.Execute,
                Enabled: true,
                KillSwitchActive: false,
                ResponsibleOwnerUserId: owner,
                DailyActionBudget: 5,
                WindowActionLimit: 5,
                WindowMinutes: 60,
                IsStored: true,
                GlobalAutonomyCap: globalAutonomyCap));

        var quietWindow = Substitute.For<IQuietWindowService>();
        quietWindow.IsQuietForAsync(Arg.Any<AgentCondition>(), Arg.Any<CancellationToken>()).Returns(false);

        var identityProvider = identityProviderOverride ?? Substitute.For<IProactiveActionIdentityProvider>();
        if (identityProviderOverride is null)
        {
            identityProvider
                .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ProactiveActionIdentity.Resolved(
                    new SkillExecutionContext
                    {
                        UserId = owner,
                        TenantId = Guid.Empty,
                        UserName = KlacksyIdentity.SystemUserName,
                        UserPermissions = ["some.permission"],
                        BypassAutonomyGate = true
                    },
                    ["some.permission"]));
        }

        return new AgentConditionActionService(
            repository,
            ledger,
            governance,
            new SingleKindRegistry(Kind),
            quietWindow,
            identityProvider,
            executor,
            reporter,
            TimeProvider.System,
            NullLogger<AgentConditionActionService>.Instance);
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
            "DELETE FROM agent_condition_events WHERE condition_id IN "
            + "(SELECT id FROM agent_conditions WHERE fingerprint LIKE {0})",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE fingerprint LIKE {0}",
            TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE name LIKE {0}",
            TestPrefix + "%");
    }

    /// <summary>
    /// Wraps the REAL EmptyContainerRemediationBinder behind a synthetic trigger kind, so this fixture
    /// exercises production binder logic (weekday/argument shape) without ever registering the real
    /// empty_container kind - see the fixture-level incident record for why that distinction is load-bearing.
    /// </summary>
    private sealed class SingleKindRegistry : IConditionRemediationRegistry
    {
        private readonly string _triggerKind;

        public SingleKindRegistry(string triggerKind)
        {
            _triggerKind = triggerKind;
        }

        public IReadOnlyCollection<string> RegisteredKinds => [_triggerKind];

        public bool TryGetEntry(string triggerKind, out ConditionRemediationEntry? entry)
        {
            entry = triggerKind == _triggerKind
                ? new ConditionRemediationEntry(
                    CreateContainerTemplateParameters.SkillName,
                    new EmptyContainerRemediationBinder(),
                    CreateContainerTemplateParameters.Required)
                : null;

            return entry is not null;
        }

        public ProactiveMaxAction TryGetEffectiveMaxAction(string triggerKind, ProactiveMaxAction configuredMaxAction) =>
            triggerKind == _triggerKind ? configuredMaxAction : ProactiveMaxAction.Hint;
    }

    private sealed class CapturingSkillExecutor : ISkillExecutor
    {
        private readonly List<SkillInvocation> _invocations = new();

        public IReadOnlyList<SkillInvocation> Invocations => _invocations;

        public Task<SkillResult> ExecuteAsync(
            SkillInvocation invocation, SkillExecutionContext context, CancellationToken cancellationToken = default)
        {
            lock (_invocations)
            {
                _invocations.Add(invocation);
            }

            return Task.FromResult(SkillResult.SuccessResult(null, "done"));
        }

        public Task<IReadOnlyList<SkillResult>> ExecuteChainAsync(
            IReadOnlyList<SkillInvocation> invocations, SkillExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
