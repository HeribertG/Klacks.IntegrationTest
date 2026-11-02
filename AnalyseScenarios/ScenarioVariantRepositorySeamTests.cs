// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of ScenarioVariantRepository: the active-scenario lookup joins link and scenario by token and
/// respects Status and is_deleted on both sides, link reads skip
/// soft-deleted links and soft-deleted variants, the group list leaves scenarios out, and the service counts (SealedOrder
/// shifts only, not their OriginalShift or SplitShift copies) group by variant token in one query. Every negative assertion has a scenario row of the same group next to the variant, so a
/// missing Kind filter shows up as a wrong result instead of an empty one. Only INTEGRATION_TEST_ rows are written and removed.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class ScenarioVariantRepositorySeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARIANTREPO_";
    private const int ExpectedShiftCount = 2;
    private static readonly DateOnly PeriodFrom = new(2099, 8, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 8, 31);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ScenarioVariantRepository _repository = null!;
    private Group _group = null!;

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
        _repository = new ScenarioVariantRepository(_context);
        _group = new Group
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "GROUP",
            Description = string.Empty,
            ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Parent = null
        };
        await _context.Set<Group>().AddAsync(_group);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task HasActiveScenario_IsTrueOnlyWhileAnActiveScenarioCarriesTheVariant()
    {
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);
        var accepted = await CreateRowAsync("ACCEPTED", AnalyseScenarioKind.Scenario, AnalyseScenarioStatus.Accepted);
        var active = await CreateRowAsync("ACTIVE", AnalyseScenarioKind.Scenario);

        (await _repository.HasActiveScenarioAsync(variant.Id)).ShouldBeFalse("no link yet");

        await LinkAsync(accepted.Token, variant.Id);
        (await _repository.HasActiveScenarioAsync(variant.Id)).ShouldBeFalse("only an accepted scenario carries it");

        await LinkAsync(active.Token, variant.Id);
        (await _repository.HasActiveScenarioAsync(variant.Id)).ShouldBeTrue();
    }

    [Test]
    public async Task HasActiveScenario_IgnoresASoftDeletedLinkAndASoftDeletedScenario()
    {
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);
        var deletedLinkScenario = await CreateRowAsync("LINK_DELETED", AnalyseScenarioKind.Scenario);
        var deletedScenario = await CreateRowAsync("SCENARIO_DELETED", AnalyseScenarioKind.Scenario);
        var deletedLink = await LinkAsync(deletedLinkScenario.Token, variant.Id);
        await LinkAsync(deletedScenario.Token, variant.Id);
        await SoftDeleteAsync("analyse_scenario_variants", deletedLink.Id);
        await SoftDeleteAsync("analyse_scenarios", deletedScenario.Id);

        (await _repository.HasActiveScenarioAsync(variant.Id)).ShouldBeFalse();
    }

    [Test]
    public async Task HasActiveScenario_IgnoresALinkedVariantRowThatIsNotAVariant()
    {
        var notAVariant = await CreateRowAsync("SCENARIO_AS_TARGET", AnalyseScenarioKind.Scenario);
        var active = await CreateRowAsync("ACTIVE", AnalyseScenarioKind.Scenario);
        await LinkAsync(active.Token, notAVariant.Id);

        (await _repository.HasActiveScenarioAsync(notAVariant.Id)).ShouldBeFalse();
    }

    [Test]
    public async Task LinkReads_SkipSoftDeletedLinksAndSoftDeletedVariants()
    {
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        var kept = await CreateRowAsync("KEPT", AnalyseScenarioKind.Variant);
        var linkDeleted = await CreateRowAsync("LINK_DELETED", AnalyseScenarioKind.Variant);
        var variantDeleted = await CreateRowAsync("VARIANT_DELETED", AnalyseScenarioKind.Variant);
        await LinkAsync(scenario.Token, kept.Id);
        var deletedLink = await LinkAsync(scenario.Token, linkDeleted.Id);
        await LinkAsync(scenario.Token, variantDeleted.Id);
        await SoftDeleteAsync("analyse_scenario_variants", deletedLink.Id);
        await SoftDeleteAsync("analyse_scenarios", variantDeleted.Id);

        var ids = await _repository.GetVariantIdsAsync(scenario.Token);
        var variants = await _repository.GetVariantsOfScenarioAsync(scenario.Token);

        ids.ShouldBe(new[] { kept.Id });
        variants.Select(v => v.Id).ShouldBe(new[] { kept.Id });
    }

    [Test]
    public async Task AddLinks_StagesOnlyTheMissingLinksAndNothingIsSavedByTheRepository()
    {
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        var first = await CreateRowAsync("FIRST", AnalyseScenarioKind.Variant);
        var second = await CreateRowAsync("SECOND", AnalyseScenarioKind.Variant);
        await LinkAsync(scenario.Token, first.Id);

        await _repository.AddLinksAsync(scenario.Token, new[] { first.Id, second.Id, second.Id });

        await using (var other = NewContext())
        {
            (await other.Set<AnalyseScenarioVariant>().CountAsync(l => l.ScenarioToken == scenario.Token))
                .ShouldBe(1, "AddLinks must stage only");
        }

        await _context.SaveChangesAsync();

        var ids = await _repository.GetVariantIdsAsync(scenario.Token);
        ids.OrderBy(id => id).ShouldBe(new[] { first.Id, second.Id }.OrderBy(id => id));
    }

    [Test]
    public async Task ListByGroup_ReturnsTheVariantsOfTheGroupOrderedByNameAndNoScenario()
    {
        var beta = await CreateRowAsync("B_VARIANT", AnalyseScenarioKind.Variant);
        var alpha = await CreateRowAsync("A_VARIANT", AnalyseScenarioKind.Variant);
        await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        var otherGroupVariant = await CreateRowAsync("OTHER_GROUP", AnalyseScenarioKind.Variant, grouped: false);

        var result = await _repository.ListByGroupsAsync([_group.Id]);

        result.Select(v => v.Id).ShouldBe(new[] { alpha.Id, beta.Id });
        result.ShouldNotContain(v => v.Id == otherGroupVariant.Id);
    }

    [Test]
    public async Task ListByGroup_SkipsASoftDeletedVariantButKeepsAConsumedOne()
    {
        var consumed = await CreateRowAsync("CONSUMED", AnalyseScenarioKind.Variant, AnalyseScenarioStatus.Accepted);
        var deleted = await CreateRowAsync("DELETED", AnalyseScenarioKind.Variant);
        await SoftDeleteAsync("analyse_scenarios", deleted.Id);

        var result = await _repository.ListByGroupsAsync([_group.Id]);

        result.Select(v => v.Id).ShouldBe(new[] { consumed.Id });
    }

    [Test]
    public async Task GetVariant_ResolvesAVariantButNotAScenarioOrADeletedRow()
    {
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        var deleted = await CreateRowAsync("DELETED", AnalyseScenarioKind.Variant);
        await SoftDeleteAsync("analyse_scenarios", deleted.Id);

        (await _repository.GetVariantAsync(variant.Id))!.Id.ShouldBe(variant.Id);
        (await _repository.GetVariantAsync(scenario.Id)).ShouldBeNull();
        (await _repository.GetVariantAsync(deleted.Id)).ShouldBeNull();
    }

    [Test]
    public async Task TheListReads_TrackNothing_SoASaveCannotWriteThroughThem()
    {
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        await LinkAsync(scenario.Token, variant.Id);
        _context.ChangeTracker.Clear();

        await _repository.ListByGroupsAsync([_group.Id]);
        await _repository.GetVariantsOfScenarioAsync(scenario.Token);
        await _repository.GetVariantsByIdsAsync(new[] { variant.Id });

        _context.ChangeTracker.Entries<AnalyseScenario>().ShouldBeEmpty();
    }

    [Test]
    public async Task GetVariantsByIds_ReturnsOnlyVariantsInNameOrder()
    {
        var beta = await CreateRowAsync("B_VARIANT", AnalyseScenarioKind.Variant);
        var alpha = await CreateRowAsync("A_VARIANT", AnalyseScenarioKind.Variant);
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);

        var result = await _repository.GetVariantsByIdsAsync(new[] { beta.Id, scenario.Id, alpha.Id });

        result.Select(v => v.Id).ShouldBe(new[] { alpha.Id, beta.Id });
        (await _repository.GetVariantsByIdsAsync(Array.Empty<Guid>())).ShouldBeEmpty();
    }

    [Test]
    public async Task CountShiftsByVariantToken_CountsLiveServicesPerTokenAndReportsZeroForEmptyKeys()
    {
        var withShifts = await CreateRowAsync("WITH_SHIFTS", AnalyseScenarioKind.Variant);
        var empty = await CreateRowAsync("EMPTY", AnalyseScenarioKind.Variant);
        await CreateShiftAsync("S1", withShifts.Token);
        await CreateShiftAsync("S2", withShifts.Token);
        var deletedShift = await CreateShiftAsync("S3", withShifts.Token);
        await CreateShiftAsync("S4", null);
        await CreateShiftAsync("S5", withShifts.Token, ShiftStatus.OriginalShift);
        await CreateShiftAsync("S6", withShifts.Token, ShiftStatus.SplitShift);
        await SoftDeleteAsync("shift", deletedShift.Id);

        var counts = await _repository.CountShiftsByVariantTokenAsync(new[] { withShifts.Token, empty.Token });

        counts[withShifts.Token].ShouldBe(ExpectedShiftCount);
        counts[empty.Token].ShouldBe(0);
        counts.Count.ShouldBe(ExpectedShiftCount);
    }

    [Test]
    public async Task HasShiftsOutsidePeriod_LooksAtEveryLiveRowOfTheKey_AndOnlyAtThose()
    {
        var variant = await CreateRowAsync("PERIOD", AnalyseScenarioKind.Variant);
        var other = await CreateRowAsync("PERIOD_OTHER", AnalyseScenarioKind.Variant);
        var from = new DateOnly(2099, 8, 10);
        var until = new DateOnly(2099, 8, 20);
        var order = await CreateShiftAsync("OUT_ORDER", variant.Token);
        var piece = await CreateShiftAsync("OUT_PIECE", variant.Token, ShiftStatus.SplitShift);
        var foreign = await CreateShiftAsync("OUT_FOREIGN", other.Token);
        var real = await CreateShiftAsync("OUT_REAL", null);
        await SetDatesAsync(order, from, until);
        await SetDatesAsync(piece, from, until);
        await SetDatesAsync(foreign, from, null);
        await SetDatesAsync(real, from, null);

        (await _repository.HasShiftsOutsidePeriodAsync(variant.Token, from, until)).ShouldBeFalse();
        (await _repository.HasShiftsOutsidePeriodAsync(variant.Token, from.AddDays(1), until)).ShouldBeTrue();
        (await _repository.HasShiftsOutsidePeriodAsync(variant.Token, from, until.AddDays(-1))).ShouldBeTrue();

        await SetDatesAsync(piece, from, null);
        (await _repository.HasShiftsOutsidePeriodAsync(variant.Token, from, until)).ShouldBeTrue();

        await SoftDeleteAsync("shift", piece.Id);
        (await _repository.HasShiftsOutsidePeriodAsync(variant.Token, from, until)).ShouldBeFalse();
    }

    [Test]
    public async Task GetSealedShifts_ReturnsLiveServicesOfTheTokenOrderedByNameThenId()
    {
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);
        var other = await CreateRowAsync("OTHER", AnalyseScenarioKind.Variant);
        var second = await CreateShiftAsync("B_SECOND", variant.Token);
        var first = await CreateShiftAsync("A_FIRST", variant.Token);
        await CreateShiftAsync("C_COPY", variant.Token, ShiftStatus.OriginalShift);
        await CreateShiftAsync("D_OTHER_KEY", other.Token);
        await CreateShiftAsync("E_REAL", null);
        var deleted = await CreateShiftAsync("F_DELETED", variant.Token);
        await SoftDeleteAsync("shift", deleted.Id);

        var result = await _repository.GetSealedShiftsAsync(variant.Token);

        result.Select(s => s.Id).ShouldBe(new[] { first.Id, second.Id });
    }

    [Test]
    public async Task SoftDeleteFinishedScenarioLinks_StagesOnlyLinksOfScenariosThatAreNotActive()
    {
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);
        var otherVariant = await CreateRowAsync("OTHER_VARIANT", AnalyseScenarioKind.Variant);
        var accepted = await CreateRowAsync("ACCEPTED", AnalyseScenarioKind.Scenario, AnalyseScenarioStatus.Accepted);
        var rejected = await CreateRowAsync("REJECTED", AnalyseScenarioKind.Scenario, AnalyseScenarioStatus.Rejected);
        var active = await CreateRowAsync("ACTIVE", AnalyseScenarioKind.Scenario);
        var acceptedLink = await LinkAsync(accepted.Token, variant.Id);
        var rejectedLink = await LinkAsync(rejected.Token, variant.Id);
        var activeLink = await LinkAsync(active.Token, variant.Id);
        var otherLink = await LinkAsync(accepted.Token, otherVariant.Id);

        await _repository.SoftDeleteFinishedScenarioLinksAsync(variant.Id);

        await using (var reader = NewContext())
        {
            (await reader.Set<AnalyseScenarioVariant>().CountAsync(l => l.VariantId == variant.Id))
                .ShouldBe(3, "the repository must only stage");
        }

        await _context.SaveChangesAsync();

        await using (var reader = NewContext())
        {
            var live = await reader.Set<AnalyseScenarioVariant>()
                .Where(l => new[] { acceptedLink.Id, rejectedLink.Id, activeLink.Id, otherLink.Id }.Contains(l.Id))
                .Select(l => l.Id)
                .ToListAsync();
            live.ShouldBe(new[] { activeLink.Id, otherLink.Id }, ignoreOrder: true);
        }
    }

    private async Task<AnalyseScenarioVariant> LinkAsync(Guid scenarioToken, Guid variantId)
    {
        var link = new AnalyseScenarioVariant { Id = Guid.NewGuid(), ScenarioToken = scenarioToken, VariantId = variantId };
        await _context.Set<AnalyseScenarioVariant>().AddAsync(link);
        await _context.SaveChangesAsync();
        return link;
    }

    private async Task<Shift> CreateShiftAsync(
        string suffix, Guid? analyseToken, ShiftStatus status = ShiftStatus.SealedOrder)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Variant repository seam",
            Status = status,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m,
            AnalyseToken = analyseToken
        };
        await _context.Shift.AddAsync(shift);
        await _context.SaveChangesAsync();
        return shift;
    }

    private async Task SetDatesAsync(Shift shift, DateOnly from, DateOnly? until)
    {
        shift.FromDate = from;
        shift.UntilDate = until;
        await _context.SaveChangesAsync();
    }

    private async Task<AnalyseScenario> CreateRowAsync(
        string suffix,
        AnalyseScenarioKind kind,
        AnalyseScenarioStatus status = AnalyseScenarioStatus.Active,
        bool grouped = true)
    {
        var row = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + suffix,
            GroupId = grouped ? _group.Id : null,
            Token = Guid.NewGuid(),
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = status,
            Kind = kind,
            CreatedByUser = TestPrefix + "USER"
        };
        await _context.Set<AnalyseScenario>().AddAsync(row);
        await _context.SaveChangesAsync();
        return row;
    }

    private async Task SoftDeleteAsync(string table, Guid id)
    {
        var sql = $"UPDATE {table} SET is_deleted = true, deleted_time = now() WHERE id = {{0}}";
        await _context.Database.ExecuteSqlRawAsync(sql, id);
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
            DELETE FROM analyse_scenario_variants
                WHERE variant_id IN (SELECT id FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%')
                OR scenario_token IN (SELECT token FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
