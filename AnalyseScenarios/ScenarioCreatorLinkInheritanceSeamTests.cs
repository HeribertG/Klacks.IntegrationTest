// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks that a scenario built on a scenario carrying a variant (key) carries the same links, for every
/// creator that clones from a source scenario. The creators are not constructed here: each test drives the real
/// coordinator seam (IAnalyseScenarioService, real collaborators, real database) with the exact arguments that creator
/// passes - WizardApplyService and HarmonizerApplyService (inherited unchanged by HolisticHarmonizerApplyService) pass the
/// source token of their plan, CoverAbsenceCommandHandler the token of its source scenario, ProposePlanCommandHandler no
/// source token at all, so its scenario starts from the real plan and carries no links. Discarding a scenario removes its
/// links only. A child in a subgroup carries only the keys inside its group hierarchy, and of a key it cannot carry it takes
/// neither the clones of the source nor their works, on the group path and on the path that lists the source's shifts.
/// Only INTEGRATION_TEST_ rows are written and removed; all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class ScenarioCreatorLinkInheritanceSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_LINKINHERIT_";
    private static readonly DateOnly PeriodFrom = new(2099, 10, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 10, 31);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ServiceProvider _provider = null!;
    private IAnalyseScenarioService _service = null!;
    private ScenarioVariantRepository _variantRepository = null!;
    private Group _group = null!;
    private Client _client = null!;
    private AnalyseScenario _variant = null!;
    private AnalyseScenario _source = null!;
    private Shift _keyShift = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _provider = AnalyseScenarioTestServiceFactory.CreateProvider(_context);
        _service = _provider.GetRequiredService<IAnalyseScenarioService>();
        _variantRepository = new ScenarioVariantRepository(_context);

        _group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "GROUP",
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = null
        };
        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Group>().AddAsync(_group);
        await _context.Set<Client>().AddAsync(_client);
        await _context.SaveChangesAsync();

        _variant = NewScenario("KEY", AnalyseScenarioKind.Variant);
        _source = NewScenario("SOURCE", AnalyseScenarioKind.Scenario);
        await _context.Set<AnalyseScenario>().AddRangeAsync(_variant, _source);
        await _context.SaveChangesAsync();

        _keyShift = NewShift("KEYSHIFT", _variant.Token);
        await _context.Shift.AddAsync(_keyShift);
        await _context.SaveChangesAsync();

        await _service.CloneScenarioDataAsync(
            _group.Id, PeriodFrom, PeriodUntil, _source.Token, new[] { _keyShift.Id }, null, CancellationToken.None,
            new HashSet<Guid> { _variant.Token });
        await _variantRepository.AddLinksAsync(_source.Token, new[] { _variant.Id });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _provider.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task WizardApply_OnAKeyScenario_GivesTheChildTheSameLinks()
    {
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataAsync(
            _group.Id, PeriodFrom, PeriodUntil, childToken, additionalShiftIds: null, _source.Token, CancellationToken.None);

        await AssertChildCarriesTheLinksAsync(childToken);
    }

    [Test]
    public async Task HarmonizerApply_OnAKeyScenario_GivesTheChildTheSameLinks()
    {
        var childToken = Guid.NewGuid();
        var sourceShiftIds = await SourceShiftIdsAsync();

        await _service.CloneScenarioDataWithMapsAsync(
            _group.Id, PeriodFrom, PeriodUntil, childToken, sourceShiftIds, _source.Token, CancellationToken.None);

        await AssertChildCarriesTheLinksAsync(childToken);
    }

    [Test]
    public async Task HolisticHarmonizerApply_OnAKeyScenario_GivesTheChildTheSameLinks()
    {
        var childToken = Guid.NewGuid();
        var sourceShiftIds = await SourceShiftIdsAsync();

        await _service.CloneScenarioDataWithMapsAsync(
            _group.Id, PeriodFrom, PeriodUntil, childToken, sourceShiftIds, _source.Token, CancellationToken.None);

        await AssertChildCarriesTheLinksAsync(childToken);
    }

    [Test]
    public async Task CoverAbsence_OnAKeyScenario_GivesTheChildTheSameLinks()
    {
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataWithMapsAsync(
            _group.Id, PeriodFrom, PeriodUntil, childToken, additionalShiftIds: null, _source.Token, CancellationToken.None);

        await AssertChildCarriesTheLinksAsync(childToken);
    }

    [Test]
    public async Task ProposePlan_WithoutASourceToken_StartsFromTheRealPlan_AndCarriesNoLinks()
    {
        var realShift = NewShift("REAL", null);
        await _context.Shift.AddAsync(realShift);
        await _context.SaveChangesAsync();
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataAsync(
            _group.Id, PeriodFrom, PeriodUntil, childToken, new[] { realShift.Id }, sourceToken: null, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _variantRepository.GetVariantIdsAsync(childToken)).ShouldBeEmpty();
        (await _context.Shift.AsNoTracking().CountAsync(s => s.AnalyseToken == childToken && s.ScenarioSourceShiftId == realShift.Id))
            .ShouldBe(1);
    }

    [Test]
    public async Task Discarding_TheChild_RemovesItsLinks_AndLeavesTheSourceLinksAndTheKeyShifts()
    {
        var childToken = Guid.NewGuid();
        await _service.CloneScenarioDataAsync(
            _group.Id, PeriodFrom, PeriodUntil, childToken, additionalShiftIds: null, _source.Token, CancellationToken.None);
        await AssertChildCarriesTheLinksAsync(childToken);

        await _service.SoftDeleteScenarioDataAsync(childToken, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _variantRepository.GetVariantIdsAsync(childToken)).ShouldBeEmpty();
        (await _variantRepository.GetVariantIdsAsync(_source.Token)).ShouldBe(new[] { _variant.Id });
        (await _context.Shift.AsNoTracking().CountAsync(s => s.AnalyseToken == _variant.Token && !s.IsDeleted)).ShouldBe(1);
    }

    [Test]
    public async Task ChildInASubgroup_ByTheGroupPath_DropsAKeyOfTheParent_WithItsClonesAndWorks()
    {
        var chain = await SeedChainThroughSubgroupAsync(keyGroupIsSubgroup: false);
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataAsync(
            chain.Subgroup.Id, PeriodFrom, PeriodUntil, childToken, additionalShiftIds: null, chain.Source.Token, CancellationToken.None);

        await AssertChildLeftTheKeyBehindAsync(chain, childToken);
    }

    [Test]
    public async Task ChildInASubgroup_ByTheListedPath_DropsAKeyOfTheParent_WithItsClonesAndWorks()
    {
        var chain = await SeedChainThroughSubgroupAsync(keyGroupIsSubgroup: false);
        var childToken = Guid.NewGuid();
        var listed = await _context.Shift.AsNoTracking()
            .Where(s => s.AnalyseToken == chain.Source.Token && !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync();

        await _service.CloneScenarioDataWithMapsAsync(
            chain.Subgroup.Id, PeriodFrom, PeriodUntil, childToken, listed, chain.Source.Token, CancellationToken.None);

        await AssertChildLeftTheKeyBehindAsync(chain, childToken);
    }

    [Test]
    public async Task ChildInASubgroup_KeepsAKeyOfThatSubgroup_WithItsClonesAndWorks()
    {
        var chain = await SeedChainThroughSubgroupAsync(keyGroupIsSubgroup: true);
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataAsync(
            chain.Subgroup.Id, PeriodFrom, PeriodUntil, childToken, additionalShiftIds: null, chain.Source.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _variantRepository.GetVariantIdsAsync(childToken)).ShouldBe(new[] { chain.Key.Id });
        var clone = await _context.Shift.AsNoTracking()
            .SingleAsync(s => s.AnalyseToken == childToken && s.ScenarioSourceShiftId == chain.KeyShift.Id);
        (await _context.Work.AsNoTracking().CountAsync(w => w.AnalyseToken == childToken && w.ShiftId == clone.Id)).ShouldBe(1);
    }

    [Test]
    public async Task ChildInASubgroup_LeavesTheOpenRequestsOfADroppedKeyBehind_AndKeepsTheOthers()
    {
        var chain = await SeedChainThroughSubgroupAsync(keyGroupIsSubgroup: false);
        await AddOpenRequestAsync(chain.Source.Token, chain.KeyShift.Id);
        await AddOpenRequestAsync(chain.Source.Token, chain.RealShift.Id);
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataAsync(
            chain.Subgroup.Id, PeriodFrom, PeriodUntil, childToken, additionalShiftIds: null, chain.Source.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var requests = await _context.ReplacementRequests.AsNoTracking().Where(r => r.AnalyseToken == childToken).ToListAsync();
        requests.Select(r => r.ShiftId).ShouldBe(new[] { chain.RealShift.Id });
    }

    [Test]
    public async Task ChildInASubgroup_KeepsTheOpenRequestsOfAKeyItCarries()
    {
        var chain = await SeedChainThroughSubgroupAsync(keyGroupIsSubgroup: true);
        await AddOpenRequestAsync(chain.Source.Token, chain.KeyShift.Id);
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataAsync(
            chain.Subgroup.Id, PeriodFrom, PeriodUntil, childToken, additionalShiftIds: null, chain.Source.Token, CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var requests = await _context.ReplacementRequests.AsNoTracking().Where(r => r.AnalyseToken == childToken).ToListAsync();
        requests.Select(r => r.ShiftId).ShouldBe(new[] { chain.KeyShift.Id });
    }

    private async Task AddOpenRequestAsync(Guid sourceToken, Guid shiftId)
    {
        await _context.ReplacementRequests.AddAsync(new ReplacementRequest
        {
            Id = Guid.NewGuid(),
            AbsentClientId = _client.Id,
            CandidateClientId = _client.Id,
            ShiftId = shiftId,
            Date = PeriodFrom.AddDays(1),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            Source = ReplacementRequestSource.ManualReplacement,
            Outcome = ReplacementRequestOutcome.Requested,
            ReportedAtUtc = new DateTime(2099, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            ShiftStartUtc = new DateTime(2099, 10, 2, 8, 0, 0, DateTimeKind.Utc),
            AnalyseToken = sourceToken
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task AssertChildLeftTheKeyBehindAsync(SubgroupChain chain, Guid childToken)
    {
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _variantRepository.GetVariantIdsAsync(childToken)).ShouldBeEmpty();
        var childShifts = await _context.Shift.AsNoTracking().Where(s => s.AnalyseToken == childToken).ToListAsync();
        childShifts.Select(s => s.ScenarioSourceShiftId).ShouldBe(new Guid?[] { chain.RealShift.Id });
        var childWorks = await _context.Work.AsNoTracking().Where(w => w.AnalyseToken == childToken).ToListAsync();
        childWorks.Select(w => w.ShiftId).ShouldBe(new[] { childShifts.Single().Id });
    }

    private async Task<SubgroupChain> SeedChainThroughSubgroupAsync(bool keyGroupIsSubgroup)
    {
        var subgroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SUBGROUP",
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = _group.Id,
            Root = _group.Id
        };
        await _context.Set<Group>().AddAsync(subgroup);
        await _context.SaveChangesAsync();

        var key = NewScenario("CHAINKEY", AnalyseScenarioKind.Variant);
        key.GroupId = keyGroupIsSubgroup ? subgroup.Id : _group.Id;
        var source = NewScenario("CHAINSOURCE", AnalyseScenarioKind.Scenario);
        await _context.Set<AnalyseScenario>().AddRangeAsync(key, source);
        var keyShift = NewShift("CHAINKEYSHIFT", key.Token);
        keyShift.GroupItems.Single().GroupId = subgroup.Id;
        var realShift = NewShift("CHAINREAL", null);
        realShift.GroupItems.Single().GroupId = subgroup.Id;
        await _context.Shift.AddRangeAsync(keyShift, realShift);
        await _context.SaveChangesAsync();

        await _service.CloneScenarioDataAsync(
            _group.Id, PeriodFrom, PeriodUntil, source.Token, new[] { keyShift.Id }, null, CancellationToken.None,
            new HashSet<Guid> { key.Token });
        await _variantRepository.AddLinksAsync(source.Token, new[] { key.Id });
        await _context.SaveChangesAsync();

        var sourceClones = await _context.Shift.AsNoTracking()
            .Where(s => s.AnalyseToken == source.Token && s.ScenarioSourceShiftId != null)
            .Select(s => s.Id)
            .ToListAsync();
        foreach (var cloneId in sourceClones)
        {
            await _context.Work.AddAsync(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = _client.Id,
                ShiftId = cloneId,
                CurrentDate = PeriodFrom.AddDays(1),
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(16, 0),
                WorkTime = 8m,
                AnalyseToken = source.Token
            });
        }

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return new SubgroupChain(subgroup, key, source, keyShift, realShift);
    }

    private async Task AssertChildCarriesTheLinksAsync(Guid childToken)
    {
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _variantRepository.GetVariantIdsAsync(childToken)).ShouldBe(new[] { _variant.Id });
        (await _variantRepository.GetVariantIdsAsync(_source.Token)).ShouldBe(new[] { _variant.Id });
        (await _context.Set<AnalyseScenarioVariant>().AsNoTracking().CountAsync(link => link.ScenarioToken == childToken))
            .ShouldBe(1);
    }

    private Task<List<Guid>> SourceShiftIdsAsync()
    {
        return _context.Shift.AsNoTracking()
            .Where(s => s.AnalyseToken == _source.Token && !s.IsDeleted)
            .Select(s => s.Id)
            .ToListAsync();
    }

    private AnalyseScenario NewScenario(string suffix, AnalyseScenarioKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        GroupId = _group.Id,
        Token = Guid.NewGuid(),
        FromDate = PeriodFrom,
        UntilDate = PeriodUntil,
        Status = AnalyseScenarioStatus.Active,
        Kind = kind,
        CreatedByUser = TestPrefix + "USER"
    };

    private Shift NewShift(string suffix, Guid? token)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Creator link inheritance seam",
            Status = ShiftStatus.SealedOrder,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            ClientId = _client.Id,
            AnalyseToken = token,
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = _group.Id, ShiftId = id, AnalyseToken = token }]
        };
    }

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM replacement_requests WHERE absent_client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM analyse_scenario_variants WHERE variant_id IN (SELECT id FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift_expenses WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL, original_id = NULL, parent_id = NULL, root_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed record SubgroupChain(Group Subgroup, AnalyseScenario Key, AnalyseScenario Source, Shift KeyShift, Shift RealShift);
}
