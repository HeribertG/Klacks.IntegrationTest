// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database guard that a variant (key) row of analyse_scenarios never reaches the readers that serve scenarios:
/// the repository lists and candidate lookups, the accepted-sibling names of the staleness check, the clone source
/// resolver and the planning-constraint scenario lookup. Every negative assertion has a scenario row of the same group,
/// dates and creator next to the variant, so a filter that is missing shows up as a wrong result instead of an empty one.
/// Only INTEGRATION_TEST_ rows are written and removed.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class ScenarioVariantIsolationSeamTests
{
    private const string TestPrefix = "INTEGRATION_TEST_VARIANTISO_";
    private const int ClockGapMilliseconds = 20;
    private const int WindowHours = 48;
    private const int OldDays = 10;
    private const int CutoffDays = 1;
    private static readonly DateOnly PeriodFrom = new(2099, 7, 1);
    private static readonly DateOnly PeriodUntil = new(2099, 7, 31);
    private static readonly DateOnly WorkDate = new(2099, 7, 6);

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private AnalyseScenarioRepository _repository = null!;
    private Group _group = null!;
    private Guid _shiftId;

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
        _repository = new AnalyseScenarioRepository(_context, Substitute.For<ILogger<AnalyseScenario>>());
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
    public async Task GetByGroup_ReturnsTheScenarioButNotTheVariant()
    {
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);

        var result = await _repository.GetByGroupAsync(_group.Id);

        result.Select(s => s.Id).ShouldBe(new[] { scenario.Id });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ListVisible_ReturnsTheScenarioButNotTheVariant(bool onlyOpen)
    {
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);

        var result = await _repository.ListVisibleAsync(_group.Id, onlyOpen, new[] { _group.Id });

        result.Select(s => s.Id).ShouldBe(new[] { scenario.Id });
    }

    [Test]
    public async Task AnyOpenVisible_IgnoresAVariantAndSeesTheScenario()
    {
        var since = DateTime.UtcNow.AddHours(-WindowHours);
        var roots = new[] { _group.Id };
        await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);

        (await _repository.AnyOpenVisibleAsync(roots, since)).ShouldBeFalse("a variant is not an open scenario");

        await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);

        (await _repository.AnyOpenVisibleAsync(roots, since)).ShouldBeTrue();
    }

    [Test]
    public async Task GetActiveCreatedBetween_ReturnsTheScenarioButNotTheVariant()
    {
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);

        var result = await _repository.GetActiveCreatedBetweenAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));

        result.Where(s => s.Name.StartsWith(TestPrefix)).Select(s => s.Id).ShouldBe(new[] { scenario.Id });
    }

    [Test]
    public async Task GetActiveCandidate_SkipsAVariantThatIsNewerThanTheScenario()
    {
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario, createdAgo: TimeSpan.FromHours(2));
        await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant, createdAgo: TimeSpan.FromHours(1));

        var result = await _repository.GetActiveCandidateAsync(TestPrefix + "USER", _group.Id, PeriodFrom, PeriodUntil);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(scenario.Id);
    }

    [Test]
    public async Task GetActiveCandidate_OfAVariantOnly_IsNull()
    {
        await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);

        var result = await _repository.GetActiveCandidateAsync(TestPrefix + "USER", _group.Id, PeriodFrom, PeriodUntil);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetStaleCandidates_ReturnsTheScenarioButNotTheVariant()
    {
        var old = TimeSpan.FromDays(OldDays);
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario, createdAgo: old);
        await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant, createdAgo: old);

        var result = await _repository.GetStaleCandidatesAsync(TestPrefix + "USER", DateTime.UtcNow.AddDays(-CutoffDays));

        result.Select(s => s.Id).ShouldBe(new[] { scenario.Id });
    }

    [Test]
    public async Task GetByToken_StillResolvesAVariant()
    {
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);

        var result = await _repository.GetByTokenAsync(variant.Token);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(variant.Id);
    }

    [Test]
    public async Task ResolveCloneSource_RefusesAVariantTokenAndAcceptsAScenarioToken()
    {
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario, grouped: false);
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant, grouped: false);
        var resolver = AnalyseScenarioTestServiceFactory.CreateProvider(_context).GetRequiredService<ScenarioCloneSourceResolver>();

        var (source, _) = await resolver.ResolveCloneSourceAsync(scenario.Token, null, CancellationToken.None);
        source.Token.ShouldBe(scenario.Token);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => resolver.ResolveCloneSourceAsync(variant.Token, null, CancellationToken.None));
        ex.Message.ShouldBe($"Source scenario with token {variant.Token} not found.");
    }

    [Test]
    public async Task ActiveScenarioExists_IsFalseForAVariantToken()
    {
        var scenario = await CreateRowAsync("SCENARIO", AnalyseScenarioKind.Scenario);
        var variant = await CreateRowAsync("VARIANT", AnalyseScenarioKind.Variant);
        var reader = new PlanningConstraintReferenceReader(_context);

        (await reader.ActiveScenarioExistsAsync(scenario.Token)).ShouldBeTrue();
        (await reader.ActiveScenarioExistsAsync(variant.Token)).ShouldBeFalse();
    }

    [Test]
    public async Task AcceptedSiblingNames_DoNotListAConsumedVariant()
    {
        await SeedRealPlanAsync();
        var scenarioService = AnalyseScenarioTestServiceFactory.Create(_context);
        var detector = AnalyseScenarioTestServiceFactory.CreateProvider(_context).GetRequiredService<ScenarioStalenessDetector>();
        var scenario = await CreateClonedScenarioAsync(scenarioService);
        await Task.Delay(ClockGapMilliseconds);
        await CreateRowAsync("SIBLING", AnalyseScenarioKind.Scenario, AnalyseScenarioStatus.Accepted, updatedAgo: TimeSpan.Zero);
        await CreateRowAsync("CONSUMED_KEY", AnalyseScenarioKind.Variant, AnalyseScenarioStatus.Accepted, updatedAgo: TimeSpan.Zero);
        await Task.Delay(ClockGapMilliseconds);
        var realWork = await _context.Work.SingleAsync(w => w.ShiftId == _shiftId && w.AnalyseToken == null);
        realWork.StartTime = new TimeOnly(9, 0);
        await _context.SaveChangesAsync();

        var staleness = await detector.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None);

        staleness.ShouldNotBeNull();
        staleness.AcceptedScenarioNames.ShouldBe(new[] { TestPrefix + "SIBLING" });
    }

    private async Task SeedRealPlanAsync()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "SHIFT",
            Abbreviation = "TST",
            Description = "Variant isolation seam",
            Status = ShiftStatus.OriginalShift,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask,
            Quantity = 1,
            WorkTime = 8m
        };
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Shift.AddAsync(shift);
        await _context.Set<Client>().AddAsync(client);
        await _context.Set<GroupItem>().AddAsync(new GroupItem { Id = Guid.NewGuid(), GroupId = _group.Id, ShiftId = shift.Id });
        await _context.Work.AddAsync(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ShiftId = shift.Id,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m
        });
        await _context.SaveChangesAsync();
        _shiftId = shift.Id;
    }

    private async Task<AnalyseScenario> CreateClonedScenarioAsync(AnalyseScenarioService scenarioService)
    {
        var token = Guid.NewGuid();
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLONED",
            Token = token,
            GroupId = _group.Id,
            FromDate = PeriodFrom,
            UntilDate = PeriodUntil,
            Status = AnalyseScenarioStatus.Active,
            Kind = AnalyseScenarioKind.Scenario,
            CreatedByUser = TestPrefix + "USER"
        };
        await _context.Set<AnalyseScenario>().AddAsync(scenario);
        await scenarioService.CloneScenarioDataAsync(_group.Id, PeriodFrom, PeriodUntil, token, null, null, CancellationToken.None);
        await _context.SaveChangesAsync();
        return scenario;
    }

    private async Task<AnalyseScenario> CreateRowAsync(
        string suffix,
        AnalyseScenarioKind kind,
        AnalyseScenarioStatus status = AnalyseScenarioStatus.Active,
        TimeSpan? createdAgo = null,
        TimeSpan? updatedAgo = null,
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

        var created = DateTime.UtcNow - (createdAgo ?? TimeSpan.Zero);
        DateTime? updated = updatedAgo.HasValue ? DateTime.UtcNow - updatedAgo.Value : null;
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE analyse_scenarios SET create_time = {created}, update_time = {updated} WHERE id = {row.Id}");
        return row;
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
            DELETE FROM work WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%' OR scenario_source_shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%');
            DELETE FROM analyse_scenarios WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
