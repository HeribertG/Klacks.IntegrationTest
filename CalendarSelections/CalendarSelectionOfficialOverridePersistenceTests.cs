// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end integration tests for the per-CalendarSelection holiday override
/// (SelectedCalendar.OfficialOverride, DB column official_override) against the real PostgreSQL
/// database, exercising the real Mediator handlers, ScheduleMapper, CalendarSelectionRepository,
/// CalendarSelectionUpdateService and DataBaseContext — the parts that unit tests mock away.
/// Proves three things a mocked unit test cannot: (1) OfficialOverride=false survives a real
/// insert + fresh reload (migration + mapping + persistence together); (2) an UPDATE roundtrip
/// changes the value on an already-existing (Country, State) pair (the UpsertSelectedCalendars
/// gap); (3) the real MacroDataProvider downgrades an override=false calendar's holiday to
/// UnofficialHoliday against real seeded CalendarRules while an override=null sibling stays official.
/// All rows created here carry the INTEGRATION_TEST_CalendarOverride_ name prefix and are
/// hard-deleted (raw SQL, prefix-scoped) in SetUp and TearDown; nothing else is ever removed.
/// </summary>

namespace Klacks.IntegrationTest.CalendarSelections;

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.CalendarSelections;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.CalendarSelections;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.CalendarSelections;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.CalendarSelections;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class CalendarSelectionOfficialOverridePersistenceTests
{
    private const string NamePrefix = "INTEGRATION_TEST_CalendarOverride_";
    private const string CleanupLikePattern = NamePrefix + "%";
    private const string Country = "CH";
    private const string State = "BE";
    private const int TestYear = 2026;

    // St. Berchtold's Day (CH/BE, rule "01/02", mandatory) — a fixed-date official holiday, so the
    // date needs no Easter/weekday arithmetic. It is the only CH/BE rule resolving to 2 January.
    private static readonly DateOnly HolidayDate = new(TestYear, 1, 2);

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable(TestHostDatabase.ConnectionStringEnvVar)
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [SetUp]
    public async Task SetUp()
    {
        await HardDeleteTestRowsAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await HardDeleteTestRowsAsync();
    }

    [Test]
    public async Task PostCalendarSelection_OfficialOverrideFalse_IsPersistedAndReloadedFromDatabase()
    {
        var selectionId = Guid.NewGuid();

        await using (var writeContext = NewContext())
        {
            var handler = NewPostHandler(writeContext);
            var resource = NewSelectionResource(selectionId, OfficialOverride: false);

            var created = await handler.Handle(new PostCommand<CalendarSelectionResource>(resource), CancellationToken.None);

            created.ShouldNotBeNull();
            created!.Id.ShouldBe(selectionId);
        }

        await using var reloadContext = NewContext();
        var reloaded = await reloadContext.SelectedCalendar
            .AsNoTracking()
            .SingleAsync(sc => sc.CalendarSelectionId == selectionId);

        reloaded.Country.ShouldBe(Country);
        reloaded.State.ShouldBe(State);
        reloaded.OfficialOverride.ShouldBe(false);
    }

    [Test]
    public async Task PutCalendarSelection_ChangesOverrideOnExistingPair_PersistsNewValueOnReload()
    {
        var selectionId = Guid.NewGuid();

        await using (var writeContext = NewContext())
        {
            var handler = NewPostHandler(writeContext);
            await handler.Handle(
                new PostCommand<CalendarSelectionResource>(NewSelectionResource(selectionId, OfficialOverride: false)),
                CancellationToken.None);
        }

        // Sanity: the pair really was stored as false before the update.
        await using (var midContext = NewContext())
        {
            var before = await midContext.SelectedCalendar
                .AsNoTracking()
                .SingleAsync(sc => sc.CalendarSelectionId == selectionId);
            before.OfficialOverride.ShouldBe(false);
        }

        await using (var updateContext = NewContext())
        {
            var handler = NewPutHandler(updateContext);
            var updateResource = NewSelectionResource(selectionId, OfficialOverride: true);

            var updated = await handler.Handle(new PutCommand<CalendarSelectionResource>(updateResource), CancellationToken.None);
            updated.ShouldNotBeNull();
        }

        await using var reloadContext = NewContext();
        var reloaded = await reloadContext.SelectedCalendar
            .AsNoTracking()
            .Where(sc => sc.CalendarSelectionId == selectionId)
            .ToListAsync();

        reloaded.Count.ShouldBe(1, "the (Country, State) pair must be updated in place, not duplicated");
        reloaded[0].Country.ShouldBe(Country);
        reloaded[0].State.ShouldBe(State);
        reloaded[0].OfficialOverride.ShouldBe(true, "UpsertSelectedCalendars must update the override on an existing pair");
    }

    [Test]
    public async Task MacroDataProvider_OverrideFalseCalendarIsUnofficial_NonOverrideCalendarIsOfficial()
    {
        await using var probeContext = NewContext();
        var hasMandatoryHolidayRule = await probeContext.CalendarRule
            .AsNoTracking()
            .AnyAsync(cr => cr.Country == Country && cr.State == State && cr.IsMandatory);

        if (!hasMandatoryHolidayRule)
        {
            Assert.Ignore($"Requires seeded mandatory {Country}/{State} calendar rules (CH-seed-gating); persistence points 1+2 cover the override without them.");
        }

        var overrideSelectionId = Guid.NewGuid();
        var inheritSelectionId = Guid.NewGuid();

        await using (var writeContext = NewContext())
        {
            var handler = NewPostHandler(writeContext);
            await handler.Handle(
                new PostCommand<CalendarSelectionResource>(NewSelectionResource(overrideSelectionId, OfficialOverride: false)),
                CancellationToken.None);
            await handler.Handle(
                new PostCommand<CalendarSelectionResource>(NewSelectionResource(inheritSelectionId, OfficialOverride: null)),
                CancellationToken.None);
        }

        await using var readContext = NewContext();
        var overrideCache = new HolidayCalculatorCache();
        var inheritCache = new HolidayCalculatorCache();

        var overrideMacroData = await ComputeMacroDataAsync(readContext, overrideCache, overrideSelectionId);
        var inheritMacroData = await ComputeMacroDataAsync(readContext, inheritCache, inheritSelectionId);

        overrideMacroData.Holiday.ShouldBeFalse("override=false must exclude the holiday from macroData.Holiday");
        inheritMacroData.Holiday.ShouldBeTrue("override=null must keep the inherited official holiday");

        var overrideCalculator = await GetCachedCalculatorAsync(overrideCache, overrideSelectionId);
        var inheritCalculator = await GetCachedCalculatorAsync(inheritCache, inheritSelectionId);

        overrideCalculator.IsHoliday(HolidayDate).ShouldBe(HolidayStatus.UnofficialHoliday);
        inheritCalculator.IsHoliday(HolidayDate).ShouldBe(HolidayStatus.OfficialHoliday);
    }

    private async Task<Klacks.Api.Domain.Models.Macros.MacroData> ComputeMacroDataAsync(
        DataBaseContext context,
        HolidayCalculatorCache cache,
        Guid calendarSelectionId)
    {
        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { CalendarSelectionId = calendarSelectionId });

        var effectiveTimeService = Substitute.For<IWorkChangeEffectiveTimeService>();

        var weekConfiguration = Substitute.For<IWeekConfiguration>();
        weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });

        var provider = new MacroDataProvider(context, cache, contractDataProvider, effectiveTimeService, weekConfiguration);

        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = HolidayDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m
        };

        return await provider.GetMacroDataAsync(work);
    }

    private static async Task<IHolidaysListCalculator> GetCachedCalculatorAsync(HolidayCalculatorCache cache, Guid selectionId)
    {
        return await cache.GetOrCreateAsync(
            selectionId,
            TestYear,
            () => Task.FromException<IHolidaysListCalculator>(
                new InvalidOperationException("Calculator must already be cached by MacroDataProvider.")));
    }

    private static CalendarSelectionResource NewSelectionResource(Guid selectionId, bool? OfficialOverride) => new()
    {
        Id = selectionId,
        Name = NamePrefix + selectionId.ToString("N"),
        SelectedCalendars =
        {
            new SelectedCalendarResource
            {
                Id = Guid.NewGuid(),
                CalendarSelectionId = selectionId,
                Country = Country,
                State = State,
                OfficialOverride = OfficialOverride
            }
        }
    };

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private PostCommandHandler NewPostHandler(DataBaseContext context)
    {
        return new PostCommandHandler(
            NewRepository(context),
            new ScheduleMapper(),
            new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>()),
            new HolidayCalculatorCache(),
            Substitute.For<ILogger<PostCommandHandler>>());
    }

    private PutCommandHandler NewPutHandler(DataBaseContext context)
    {
        return new PutCommandHandler(
            NewRepository(context),
            new ScheduleMapper(),
            new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>()),
            new HolidayCalculatorCache(),
            Substitute.For<ILogger<PutCommandHandler>>());
    }

    private static CalendarSelectionRepository NewRepository(DataBaseContext context)
    {
        var updateService = new CalendarSelectionUpdateService(
            context,
            Substitute.For<ILogger<CalendarSelectionUpdateService>>());
        return new CalendarSelectionRepository(
            context,
            Substitute.For<ILogger<CalendarSelection>>(),
            updateService);
    }

    private async Task HardDeleteTestRowsAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM selected_calendar WHERE calendar_selection_id IN " +
            "(SELECT id FROM calendar_selection WHERE name LIKE {0})",
            CleanupLikePattern);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM calendar_selection WHERE name LIKE {0}",
            CleanupLikePattern);
    }
}
