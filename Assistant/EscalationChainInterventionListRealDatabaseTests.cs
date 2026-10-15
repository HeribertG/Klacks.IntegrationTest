// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database proof for the repository queries behind the readable intervention list: the "not resolved" history
/// (Cancelled/Exhausted inside the window, newest first), the exhaust stamping ExhaustedAtUtc, the wake query and
/// the wake claim of chains registered ahead of time, the scenario re-point of a running chain and the display context
/// (group names, finding kinds, scenario statuses with soft-deleted scenarios absent) - all translated and executed by
/// Npgsql, which the in-memory fake cannot prove. Applies pending migrations first (the columns
/// scenario_id/wake_not_before_utc/exhausted_at_utc are new). Assertions only look at this fixture's own rows, the
/// shared database may hold others. Cleanup deletes ONLY rows carrying this fixture's prefix.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class EscalationChainInterventionListRealDatabaseTests
{
    private const string TestPrefix = "INTEGRATION_TEST_ESC_LIST_";

    private DateTime _nowUtc;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var migrationOptions = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var context = new DataBaseContext(migrationOptions, Substitute.For<IHttpContextAccessor>()))
        {
            await context.Database.MigrateAsync();
        }

        await CleanupAsync();
    }

    [SetUp]
    public void SetUp()
    {
        var now = DateTime.UtcNow;
        _nowUtc = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
    }

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task GetUnresolvedChainsSinceAsync_ReturnsCancelledAndExhaustedInsideTheWindow_NewestFirst()
    {
        var cancelledYesterday = await AddChainAsync(c =>
        {
            c.Status = EscalationChainStatus.Cancelled;
            c.CancelledAtUtc = _nowUtc.AddDays(-1);
            c.CancelReason = "covered by phone";
        });
        var exhaustedTwoDaysAgo = await AddChainAsync(c =>
        {
            c.Status = EscalationChainStatus.Exhausted;
            c.ExhaustedAtUtc = _nowUtc.AddDays(-2);
        });
        var exhaustedOneHourAgo = await AddChainAsync(c =>
        {
            c.Status = EscalationChainStatus.Exhausted;
            c.ExhaustedAtUtc = _nowUtc.AddHours(-1);
        });
        var cancelledTooLongAgo = await AddChainAsync(c =>
        {
            c.Status = EscalationChainStatus.Cancelled;
            c.CancelledAtUtc = _nowUtc.AddDays(-8);
        });
        var exhaustedWithoutStamp = await AddChainAsync(c => c.Status = EscalationChainStatus.Exhausted);
        var running = await AddChainAsync(_ => { });
        var acknowledged = await AddChainAsync(c => c.Status = EscalationChainStatus.Acknowledged);
        var superseded = await AddChainAsync(c => c.Status = EscalationChainStatus.Superseded);

        await using var context = NewContext();
        var found = await new EscalationChainRepository(context).GetUnresolvedChainsSinceAsync(_nowUtc.AddDays(-7));

        var own = found.Where(c => c.AbsentClientName.StartsWith(TestPrefix)).ToList();
        own.Select(c => c.Id).ToList().ShouldBe(new List<Guid> { exhaustedOneHourAgo, cancelledYesterday, exhaustedTwoDaysAgo });
        own.ShouldAllBe(c => c.Stages.Count == 2);
        own.Single(c => c.Id == cancelledYesterday).CancelReason.ShouldBe("covered by phone");
        found.Select(c => c.Id).ShouldNotContain(cancelledTooLongAgo);
        found.Select(c => c.Id).ShouldNotContain(exhaustedWithoutStamp);
        found.Select(c => c.Id).ShouldNotContain(running);
        found.Select(c => c.Id).ShouldNotContain(acknowledged);
        found.Select(c => c.Id).ShouldNotContain(superseded);
    }

    [Test]
    public async Task TryExhaustChainAsync_StampsTheExhaustInstant_AndOnlyOnce()
    {
        var chainId = await AddChainAsync(_ => { });
        var exhaustedAtUtc = _nowUtc.AddMinutes(-5);

        await using (var context = NewContext())
        {
            var repository = new EscalationChainRepository(context);
            (await repository.TryExhaustChainAsync(chainId, "test exhaust", exhaustedAtUtc)).Exhausted.ShouldBeTrue();
            (await repository.TryExhaustChainAsync(chainId, "second", _nowUtc)).Exhausted.ShouldBeFalse();
        }

        await using var verify = NewContext();
        var chain = await verify.EscalationChains.AsNoTracking().Include(c => c.Stages).SingleAsync(c => c.Id == chainId);
        chain.Status.ShouldBe(EscalationChainStatus.Exhausted);
        chain.OutcomeReason.ShouldBe("test exhaust");
        chain.ExhaustedAtUtc.ShouldBe(exhaustedAtUtc);
        chain.Stages.ShouldAllBe(s => s.Status == EscalationStageStatus.Cancelled);
    }

    [Test]
    public async Task GetChainIdsDueToWakeAsync_OnlyRunningChainsWhoseWakeInstantHasCome()
    {
        var due = await AddChainAsync(c => c.WakeNotBeforeUtc = _nowUtc.AddMinutes(-1));
        var dueExactlyNow = await AddChainAsync(c => c.WakeNotBeforeUtc = _nowUtc);
        var notYet = await AddChainAsync(c => c.WakeNotBeforeUtc = _nowUtc.AddMinutes(1));
        var wokenAtCreation = await AddChainAsync(_ => { });
        var ended = await AddChainAsync(c =>
        {
            c.Status = EscalationChainStatus.Exhausted;
            c.WakeNotBeforeUtc = _nowUtc.AddMinutes(-1);
        });

        await using var context = NewContext();
        var found = await new EscalationChainRepository(context).GetChainIdsDueToWakeAsync(_nowUtc);

        found.ShouldContain(due);
        found.ShouldContain(dueExactlyNow);
        found.ShouldNotContain(notYet);
        found.ShouldNotContain(wokenAtCreation);
        found.ShouldNotContain(ended);
    }

    [Test]
    public async Task TryClaimWakeAsync_OnlyTheFirstCallerWins_AndTheWakeInstantIsCleared()
    {
        var chainId = await AddChainAsync(c => c.WakeNotBeforeUtc = _nowUtc.AddMinutes(-1));

        bool first;
        bool second;
        await using (var a = NewContext())
        await using (var b = NewContext())
        {
            first = await new EscalationChainRepository(a).TryClaimWakeAsync(chainId, _nowUtc);
            second = await new EscalationChainRepository(b).TryClaimWakeAsync(chainId, _nowUtc);
        }

        first.ShouldBeTrue();
        second.ShouldBeFalse();
        await using var verify = NewContext();
        (await verify.EscalationChains.AsNoTracking().SingleAsync(c => c.Id == chainId)).WakeNotBeforeUtc.ShouldBeNull();
        (await new EscalationChainRepository(verify).GetChainIdsDueToWakeAsync(_nowUtc)).ShouldNotContain(chainId);
    }

    [Test]
    public async Task TryClaimWakeAsync_BeforeTheWakeInstant_OrOnAnEndedChain_ClaimsNothing()
    {
        var early = await AddChainAsync(c => c.WakeNotBeforeUtc = _nowUtc.AddMinutes(10));
        var ended = await AddChainAsync(c =>
        {
            c.Status = EscalationChainStatus.Cancelled;
            c.WakeNotBeforeUtc = _nowUtc.AddMinutes(-10);
        });

        await using var context = NewContext();
        var repository = new EscalationChainRepository(context);

        (await repository.TryClaimWakeAsync(early, _nowUtc)).ShouldBeFalse();
        (await repository.TryClaimWakeAsync(ended, _nowUtc)).ShouldBeFalse();
    }

    [Test]
    public async Task TryUpdateRunningChainScenarioAsync_RepointsOnlyTheRunningChainOfTheShift()
    {
        var workId = Guid.NewGuid();
        var olderScenario = Guid.NewGuid();
        var newerScenario = Guid.NewGuid();
        var endedEarlier = await AddChainAsync(c =>
        {
            c.WorkId = workId;
            c.Status = EscalationChainStatus.Exhausted;
            c.ScenarioId = olderScenario;
        });
        var running = await AddChainAsync(c =>
        {
            c.WorkId = workId;
            c.ScenarioId = olderScenario;
        });

        await using (var context = NewContext())
        {
            (await new EscalationChainRepository(context).TryUpdateRunningChainScenarioAsync(workId, newerScenario)).ShouldBeTrue();
            (await new EscalationChainRepository(context).TryUpdateRunningChainScenarioAsync(Guid.NewGuid(), newerScenario)).ShouldBeFalse();
        }

        await using var verify = NewContext();
        (await verify.EscalationChains.AsNoTracking().SingleAsync(c => c.Id == running)).ScenarioId.ShouldBe(newerScenario);
        (await verify.EscalationChains.AsNoTracking().SingleAsync(c => c.Id == endedEarlier)).ScenarioId.ShouldBe(olderScenario);
    }

    [Test]
    public async Task GetDisplayContextAsync_ResolvesNamesKindsAndStatuses_AndLeavesSoftDeletedScenariosOut()
    {
        var groupId = await SeedGroupAsync();
        var conditionId = await SeedConditionAsync("empty_container");
        var active = await SeedScenarioAsync(AnalyseScenarioStatus.Active);
        var accepted = await SeedScenarioAsync(AnalyseScenarioStatus.Accepted);
        var deleted = await SeedScenarioAsync(AnalyseScenarioStatus.Active, softDelete: true);
        var unknown = Guid.NewGuid();

        await using var context = NewContext();
        var display = await new EscalationChainRepository(context).GetDisplayContextAsync(
            [groupId, unknown], [conditionId, unknown], [active, accepted, deleted, unknown]);

        display.GroupNames.ShouldBe(new Dictionary<Guid, string> { [groupId] = TestPrefix + "group" });
        display.FindingKinds.ShouldBe(new Dictionary<Guid, string> { [conditionId] = "empty_container" });
        display.ScenarioStatuses.Count.ShouldBe(2);
        display.ScenarioStatuses[active].ShouldBe(AnalyseScenarioStatus.Active);
        display.ScenarioStatuses[accepted].ShouldBe(AnalyseScenarioStatus.Accepted);
        display.ScenarioStatuses.ContainsKey(deleted).ShouldBeFalse();
    }

    [Test]
    public async Task GetDisplayContextAsync_EmptyIdSets_ReturnEmptyDictionaries()
    {
        await using var context = NewContext();
        var display = await new EscalationChainRepository(context).GetDisplayContextAsync([], [], []);

        display.GroupNames.ShouldBeEmpty();
        display.FindingKinds.ShouldBeEmpty();
        display.ScenarioStatuses.ShouldBeEmpty();
    }

    private async Task<Guid> AddChainAsync(Action<EscalationChain> configure)
    {
        var shiftStartUtc = _nowUtc.AddDays(3);
        var chain = new EscalationChain
        {
            Id = Guid.NewGuid(),
            Status = EscalationChainStatus.Running,
            Purpose = EscalationChainPurpose.AbsenceCoverage,
            WorkId = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            AbsentClientId = Guid.NewGuid(),
            AbsentClientName = TestPrefix + "absent",
            ShiftStartUtc = shiftStartUtc,
            DeadlineUtc = shiftStartUtc.AddHours(-2),
            Stages = new List<EscalationStage>
            {
                new() { Rank = 1, UserId = "integration-test-first", UserDisplayName = TestPrefix + "first" },
                new() { Rank = 2, UserId = "integration-test-second", UserDisplayName = TestPrefix + "second" }
            }
        };
        configure(chain);

        await using var context = NewContext();
        context.EscalationChains.Add(chain);
        await context.SaveChangesAsync();
        return chain.Id;
    }

    private static async Task<Guid> SeedGroupAsync()
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "group",
            Description = "Escalation intervention list integration test",
            ValidFrom = DateTime.UtcNow.AddYears(-1)
        };
        await using var context = NewContext();
        context.Group.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    private async Task<Guid> SeedConditionAsync(string triggerKind)
    {
        var condition = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = triggerKind,
            Fingerprint = TestPrefix + Guid.NewGuid().ToString("N"),
            Severity = "info",
            DetectedAtUtc = _nowUtc,
            LastSeenAtUtc = _nowUtc
        };
        await using var context = NewContext();
        context.AgentConditions.Add(condition);
        await context.SaveChangesAsync();
        return condition.Id;
    }

    private static async Task<Guid> SeedScenarioAsync(AnalyseScenarioStatus status, bool softDelete = false)
    {
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "scenario",
            FromDate = new DateOnly(2026, 10, 1),
            UntilDate = new DateOnly(2026, 10, 31),
            Token = Guid.NewGuid(),
            CreatedByUser = TestPrefix,
            Status = status
        };
        await using var context = NewContext();
        context.AnalyseScenarios.Add(scenario);
        await context.SaveChangesAsync();

        if (softDelete)
        {
            context.AnalyseScenarios.Remove(scenario);
            await context.SaveChangesAsync();
        }

        return scenario.Id;
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
            "DELETE FROM escalation_stages WHERE escalation_chain_id IN "
            + "(SELECT id FROM escalation_chains WHERE starts_with(absent_client_name, {0}))",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM escalation_chains WHERE starts_with(absent_client_name, {0})",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_conditions WHERE starts_with(fingerprint, {0})",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM analyse_scenarios WHERE starts_with(name, {0})",
            TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"group\" WHERE starts_with(name, {0})",
            TestPrefix);
    }
}
