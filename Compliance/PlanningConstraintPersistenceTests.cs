// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Planning-constraint persistence and loading against the REAL PostgreSQL database (Npgsql, never
/// InMemory - several query shapes only fail on the real provider): the migration (planning_constraint
/// table and indexes, CounterRule origin/approval_status defaults for rows written without them), the
/// status / validity / AnalyseToken (IS NOT DISTINCT FROM) filter of the approved-for-period query, the
/// group-membership query behind Group scopes (validity overlap, soft delete, scenario memberships), the
/// carry-in Work query, the proposal expiry bulk update, and the loader composed from the real
/// repositories. No host is booted (no background service can start): one direct DbContext, every row
/// written inside a single transaction that is ALWAYS rolled back in TearDown; every seeded name carries the
/// INTEGRATION_TEST_ prefix as a second line of defense.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Compliance;

[TestFixture]
[Category("RealDatabase")]
public class PlanningConstraintPersistenceTests
{
    private const string TestMarker = "INTEGRATION_TEST_PLANNING_CONSTRAINT_";
    private const string MaxRunJson = """{"schemaVersion":1,"kind":"Night","maxRun":3}""";
    private const string FairnessJson = """{"schemaVersion":1,"metric":"NightDays","window":"PlanPeriod","maxSpread":2}""";

    private static readonly DateOnly From = new(2091, 3, 1);
    private static readonly DateOnly Until = new(2091, 3, 31);

    private DataBaseContext _context = null!;
    private IDbContextTransaction _transaction = null!;
    private PlanningConstraintRepository _repository = null!;
    private PlanningRuleDataReader _dataReader = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _transaction = await _context.Database.BeginTransactionAsync();
        _repository = new PlanningConstraintRepository(_context);
        _dataReader = new PlanningRuleDataReader(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }

        _context?.Dispose();
    }

    [Test]
    public async Task Migration_CreatesTableAndIndexes_AndCounterRuleRowsWithoutTheNewColumnsStayAdminApproved()
    {
        var indexes = await _context.Database
            .SqlQueryRaw<string>("SELECT indexname AS value FROM pg_indexes WHERE tablename = 'planning_constraint'")
            .ToListAsync();
        indexes.ShouldContain("pk_planning_constraint");
        indexes.Count.ShouldBeGreaterThanOrEqualTo(5, string.Join(", ", indexes));

        var legacyId = Guid.NewGuid();
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO counter_rule (id, event_type, period, threshold, is_deleted) VALUES ({0}, 1, 3, 25, false)",
            legacyId);

        var legacy = await _context.CounterRule.AsNoTracking().SingleAsync(r => r.Id == legacyId);
        legacy.Origin.ShouldBe(RuleOrigin.Admin);
        legacy.ApprovalStatus.ShouldBe(RuleApprovalStatus.Approved);
        legacy.SourceText.ShouldBeNull();

        var approved = await new CounterRuleRepository(_context).GetAllApprovedAsync();
        approved.ShouldContain(r => r.Id == legacyId);
    }

    [Test]
    public async Task CounterRule_ExplicitProposedStatus_IsStored_AndNotApproved()
    {
        var proposed = new CounterRule
        {
            Id = Guid.NewGuid(),
            EventType = CounterEventType.NightShift,
            Period = CounterPeriod.Year,
            Threshold = 25,
            Origin = RuleOrigin.LlmProposal,
            ApprovalStatus = RuleApprovalStatus.Proposed,
            SourceText = TestMarker + "source",
        };
        _context.CounterRule.Add(proposed);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var stored = await _context.CounterRule.AsNoTracking().SingleAsync(r => r.Id == proposed.Id);
        stored.ApprovalStatus.ShouldBe(RuleApprovalStatus.Proposed, "a non-default value must never be replaced by the DB default");
        stored.Origin.ShouldBe(RuleOrigin.LlmProposal);
        (await new CounterRuleRepository(_context).GetAllApprovedAsync()).ShouldNotContain(r => r.Id == proposed.Id);
    }

    [Test]
    public async Task ApprovedForPeriod_FiltersStatusValidityDeletionAndAnalyseToken()
    {
        var token = Guid.NewGuid();
        var real = await AddConstraintAsync(RuleApprovalStatus.Approved);
        var openEnded = await AddConstraintAsync(RuleApprovalStatus.Approved, validFrom: From.AddDays(-400));
        var overlapping = await AddConstraintAsync(RuleApprovalStatus.Approved, validFrom: Until, validUntil: Until.AddDays(10));
        var scenario = await AddConstraintAsync(RuleApprovalStatus.Approved, analyseToken: token);
        var proposed = await AddConstraintAsync(RuleApprovalStatus.Proposed);
        var rejected = await AddConstraintAsync(RuleApprovalStatus.Rejected);
        var revoked = await AddConstraintAsync(RuleApprovalStatus.Revoked);
        var before = await AddConstraintAsync(RuleApprovalStatus.Approved, validFrom: From.AddDays(-30), validUntil: From.AddDays(-1));
        var after = await AddConstraintAsync(RuleApprovalStatus.Approved, validFrom: Until.AddDays(1));
        var deleted = await AddConstraintAsync(RuleApprovalStatus.Approved);
        _context.PlanningConstraint.Remove(deleted);
        await _context.SaveChangesAsync();
        var mine = new HashSet<Guid> { real.Id, openEnded.Id, overlapping.Id, scenario.Id, proposed.Id, rejected.Id, revoked.Id, before.Id, after.Id, deleted.Id };

        var realPlan = (await _repository.GetApprovedForPeriodAsync(From, Until, null)).Where(c => mine.Contains(c.Id)).Select(c => c.Id);
        var scenarioPlan = (await _repository.GetApprovedForPeriodAsync(From, Until, token)).Where(c => mine.Contains(c.Id)).Select(c => c.Id);

        realPlan.ShouldBe([real.Id, openEnded.Id, overlapping.Id], ignoreOrder: true);
        scenarioPlan.ShouldBe([scenario.Id]);
    }

    [Test]
    public async Task ListAsync_WithProposedStatus_IsThePendingList()
    {
        var proposed = await AddConstraintAsync(RuleApprovalStatus.Proposed);
        var approved = await AddConstraintAsync(RuleApprovalStatus.Approved);

        var pending = await _repository.ListAsync(RuleApprovalStatus.Proposed);

        pending.ShouldContain(c => c.Id == proposed.Id);
        pending.ShouldNotContain(c => c.Id == approved.Id);
    }

    [Test]
    public async Task ExpireProposed_RejectsOnlyProposalsOlderThanTheLifetime()
    {
        var old = await AddConstraintAsync(RuleApprovalStatus.Proposed);
        var fresh = await AddConstraintAsync(RuleApprovalStatus.Proposed);
        var oldApproved = await AddConstraintAsync(RuleApprovalStatus.Approved);
        var nowUtc = DateTime.UtcNow;
        var oldCreate = nowUtc.AddDays(-(PlanningConstraintDefaults.ProposalLifetimeDays + 1));
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE planning_constraint SET create_time = {0} WHERE id IN ({1}, {2})", oldCreate, old.Id, oldApproved.Id);

        var expired = await _repository.ExpireProposedAsync(nowUtc - PlanningConstraintLifecycle.ProposalLifetime, nowUtc);

        expired.ShouldBeGreaterThanOrEqualTo(1);
        _context.ChangeTracker.Clear();
        (await StatusOfAsync(old.Id)).ShouldBe(RuleApprovalStatus.Rejected);
        (await StatusOfAsync(fresh.Id)).ShouldBe(RuleApprovalStatus.Proposed);
        (await StatusOfAsync(oldApproved.Id)).ShouldBe(RuleApprovalStatus.Approved);
        (await _context.PlanningConstraint.AsNoTracking().SingleAsync(c => c.Id == old.Id)).CurrentUserUpdated
            .ShouldBe(PlanningConstraintDefaults.ExpirySweepActor);
    }

    [Test]
    public async Task GroupMemberships_RespectValidityDeletionAndScenario()
    {
        var token = Guid.NewGuid();
        var group = await AddGroupAsync("group", parent: null);
        var inPeriod = await AddClientAsync("in-period");
        var outside = await AddClientAsync("outside");
        var deletedMember = await AddClientAsync("deleted");
        var borrowed = await AddClientAsync("borrowed");
        await AddMembershipAsync(group, inPeriod, From.AddDays(-10), null, null);
        await AddMembershipAsync(group, outside, From.AddDays(-60), From.AddDays(-1), null);
        var removed = await AddMembershipAsync(group, deletedMember, null, null, null);
        await AddMembershipAsync(group, borrowed, From, Until, token);
        _context.GroupItem.Remove(removed);
        await _context.SaveChangesAsync();
        var clients = new[] { inPeriod, outside, deletedMember, borrowed };

        var realPlan = await _dataReader.GetGroupMembershipsAsync([group], clients, From, Until, null);
        var scenarioPlan = await _dataReader.GetGroupMembershipsAsync([group], clients, From, Until, token);

        realPlan.Select(m => m.ClientId).ShouldBe([inPeriod]);
        scenarioPlan.Select(m => m.ClientId).ShouldBe([inPeriod, borrowed], ignoreOrder: true);
    }

    [Test]
    public async Task WorkSpans_FilterDeletionRangeAndAnalyseToken()
    {
        var token = Guid.NewGuid();
        var client = await AddClientAsync("worker");
        var shift = await AddShiftAsync();
        var inRange = await AddWorkAsync(client, shift, From.AddDays(-5), null);
        await AddWorkAsync(client, shift, From.AddDays(-5), token);
        await AddWorkAsync(client, shift, From.AddDays(-40), null);
        var deleted = await AddWorkAsync(client, shift, From.AddDays(-4), null);
        _context.Work.Remove(deleted);
        await _context.SaveChangesAsync();

        var real = await _dataReader.GetWorkSpansAsync([client], From.AddDays(-10), From.AddDays(-1), null);
        var scenario = await _dataReader.GetWorkSpansAsync([client], From.AddDays(-10), From.AddDays(-1), token);

        real.ShouldHaveSingleItem().Date.ShouldBe(inRange.CurrentDate);
        scenario.ShouldHaveSingleItem().Date.ShouldBe(From.AddDays(-5));
    }

    [Test]
    public async Task Loader_ResolvesGroupScopeThroughSubgroups_AndIgnoresProposedRows()
    {
        var group = await AddGroupAsync("parent", parent: null);
        var subgroup = await AddGroupAsync("child", parent: group);
        var member = await AddClientAsync("sub-member");
        var outsider = await AddClientAsync("outsider");
        await AddMembershipAsync(subgroup, member, null, null, null);
        var fairness = await AddConstraintAsync(
            RuleApprovalStatus.Approved, kind: PlanningConstraintKind.TeamFairness, severity: PlanningConstraintSeverity.Soft,
            scopeType: PlanningConstraintScopeType.Group, scopeId: group, json: FairnessJson);
        var proposal = await AddConstraintAsync(RuleApprovalStatus.Proposed);
        var loader = BuildLoader();

        var rules = await loader.LoadAsync([member, outsider], From, Until, null);

        rules.ShouldNotContain(r => r.RuleId == proposal.Id);
        var rule = rules.Single(r => r.RuleId == fairness.Id).ShouldBeOfType<TeamFairnessRule>();
        rule.AgentScope.ShouldBe(new HashSet<string> { member.ToString() }, ignoreOrder: true);
    }

    private PlanningRuleSetLoader BuildLoader()
    {
        var enforcement = Substitute.For<IComplianceEnforcementResolver>();
        enforcement.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);
        var contracts = Substitute.For<IClientContractDataProvider>();
        contracts.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());
        var hierarchy = new GroupClientService(
            _context, new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }), NullLogger<GroupClientService>.Instance);

        return new PlanningRuleSetLoader(
            new CounterRuleRepository(_context),
            _repository,
            new PlanningConstraintValidator(),
            enforcement,
            contracts,
            hierarchy,
            _dataReader,
            new PlanningRuleCarryInLoader(_dataReader),
            NullLogger<PlanningRuleSetLoader>.Instance);
    }

    private async Task<RuleApprovalStatus> StatusOfAsync(Guid id) =>
        (await _context.PlanningConstraint.AsNoTracking().SingleAsync(c => c.Id == id)).ApprovalStatus;

    private async Task<PlanningConstraint> AddConstraintAsync(
        RuleApprovalStatus status,
        DateOnly? validFrom = null,
        DateOnly? validUntil = null,
        Guid? analyseToken = null,
        PlanningConstraintKind kind = PlanningConstraintKind.MaxConsecutiveOfKind,
        PlanningConstraintSeverity severity = PlanningConstraintSeverity.Hard,
        PlanningConstraintScopeType scopeType = PlanningConstraintScopeType.Global,
        Guid? scopeId = null,
        string json = MaxRunJson)
    {
        var constraint = new PlanningConstraint
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Severity = severity,
            Weight = 1d,
            ScopeType = scopeType,
            ScopeId = scopeId,
            ParametersJson = json,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Origin = RuleOrigin.Admin,
            ApprovalStatus = status,
            Paraphrase = TestMarker + status,
            AnalyseToken = analyseToken,
        };
        _context.PlanningConstraint.Add(constraint);
        await _context.SaveChangesAsync();
        return constraint;
    }

    private async Task<Guid> AddClientAsync(string suffix)
    {
        var client = new Client { Id = Guid.NewGuid(), FirstName = TestMarker + suffix, Name = "ConstraintProbe", Type = EntityTypeEnum.Employee };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();
        return client.Id;
    }

    private async Task<Guid> AddGroupAsync(string suffix, Guid? parent)
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestMarker + suffix,
            ValidFrom = From.AddYears(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Parent = parent,
            Root = parent,
            Lft = 0,
            Rgt = 0,
        };
        _context.Group.Add(group);
        await _context.SaveChangesAsync();
        return group.Id;
    }

    private async Task<GroupItem> AddMembershipAsync(Guid groupId, Guid clientId, DateOnly? validFrom, DateOnly? validUntil, Guid? analyseToken)
    {
        var item = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ClientId = clientId,
            ValidFrom = validFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ValidUntil = validUntil?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            AnalyseToken = analyseToken,
        };
        _context.GroupItem.Add(item);
        await _context.SaveChangesAsync();
        return item;
    }

    private async Task<Guid> AddShiftAsync()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestMarker + "shift",
            Abbreviation = "IPC",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = From.AddYears(-1),
            StartShift = new TimeOnly(22, 0),
            EndShift = new TimeOnly(6, 0),
        };
        _context.Shift.Add(shift);
        await _context.SaveChangesAsync();
        return shift.Id;
    }

    private async Task<Work> AddWorkAsync(Guid clientId, Guid shiftId, DateOnly date, Guid? analyseToken)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(22, 0),
            EndTime = new TimeOnly(6, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
            AnalyseToken = analyseToken,
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        return work;
    }
}
