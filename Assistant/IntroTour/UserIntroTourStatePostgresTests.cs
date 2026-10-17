// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// user_intro_tour_states against the shared integration database: EnsureExistsAsync inserts once and never
/// overwrites, the jsonb station list round-trips through the real IntroTourStateService (a reassigned list is
/// detected and persisted), and deleting the account cascades to its row. Every row is keyed by an account id with
/// the INTEGRATION_TEST_INTRO_ prefix and the cleanup deletes only those.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.IntroTour;

[TestFixture]
[Category("RealDatabase")]
public class UserIntroTourStatePostgresTests
{
    private const string TestPrefix = "INTEGRATION_TEST_INTRO_";
    private static readonly DateTime Now = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

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

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task EnsureExists_InsertsOnce_AndNeverOverwritesAnExistingRow()
    {
        var userId = await SeedUserAsync();

        await using (var context = NewContext())
        {
            var repository = new UserIntroTourStateRepository(context);
            await repository.EnsureExistsAsync(userId, IntroTourStatus.Pending, Now);
            await repository.EnsureExistsAsync(userId, IntroTourStatus.Completed, Now.AddHours(1));
        }

        await using (var context = NewContext())
        {
            var rows = await context.UserIntroTourStates.AsNoTracking().Where(s => s.UserId == userId).ToListAsync();
            rows.Count.ShouldBe(1);
            rows[0].Status.ShouldBe(IntroTourStatus.Pending);
            rows[0].CompletedStations.ShouldBeEmpty();
            rows[0].SnoozeCount.ShouldBe(0);
            rows[0].CreateTime.ShouldBe(Now);
        }
    }

    [Test]
    public async Task TheService_PersistsStatusStationsAndTimestamps_ThroughTheRealRepository()
    {
        var userId = await SeedUserAsync();

        await using (var context = NewContext())
        {
            var service = NewService(context);
            await service.ApplyAsync(userId, IntroTourDefaults.OfferedEvent, null);
            await service.ApplyAsync(userId, IntroTourStatus.InProgress, "schedule");
            await service.ApplyAsync(userId, null, "shifts");
        }

        await using (var context = NewContext())
        {
            var row = await new UserIntroTourStateRepository(context).GetNoTrackingAsync(userId);
            row.ShouldNotBeNull();
            row!.Status.ShouldBe(IntroTourStatus.InProgress);
            row.CompletedStations.ShouldBe(new[] { "schedule", "shifts" });
            row.LastOfferedAt.ShouldBe(Now);
            row.UpdateTime.ShouldBe(Now);

            var slice = await NewService(context).GetStateAsync(userId);
            slice!.ShowCard.ShouldBeTrue();
            slice.CompletedStations.ShouldBe(new[] { "schedule", "shifts" });
        }

        await using (var context = NewContext())
        {
            await NewService(context).ApplyAsync(userId, IntroTourStatus.Completed, null);
            await NewService(context).ApplyAsync(userId, IntroTourStatus.InProgress, null);
        }

        await using (var context = NewContext())
        {
            var row = await new UserIntroTourStateRepository(context).GetNoTrackingAsync(userId);
            row!.Status.ShouldBe(IntroTourStatus.InProgress);
            row.CompletedStations.ShouldBeEmpty();
        }
    }

    [Test]
    public async Task DeletingTheAccount_RemovesItsIntroRow()
    {
        var userId = await SeedUserAsync();
        await using (var context = NewContext())
        {
            await new UserIntroTourStateRepository(context).EnsureExistsAsync(userId, IntroTourStatus.Pending, Now);
        }

        await using (var context = NewContext())
        {
            await context.Database.ExecuteSqlRawAsync("DELETE FROM \"AspNetUsers\" WHERE id = {0}", userId);
        }

        await using (var context = NewContext())
        {
            (await context.UserIntroTourStates.AsNoTracking().AnyAsync(s => s.UserId == userId)).ShouldBeFalse();
        }
    }

    [Test]
    public async Task EnsureExists_ForAnUnknownAccount_IsRefusedByTheForeignKey()
    {
        await using var context = NewContext();

        await Should.ThrowAsync<Npgsql.PostgresException>(
            () => new UserIntroTourStateRepository(context).EnsureExistsAsync(
                TestPrefix + "missing-" + Guid.NewGuid().ToString("N"), IntroTourStatus.Pending, Now));
    }

    private static IntroTourStateService NewService(DataBaseContext context)
    {
        var llmRepository = Substitute.For<Klacks.Api.Domain.Interfaces.Assistant.ILLMRepository>();
        llmRepository.GetProvidersAsync().Returns(new List<Klacks.Api.Domain.Models.Assistant.LLMProvider>());
        return new IntroTourStateService(
            new UserIntroTourStateRepository(context),
            new UnitOfWork(context, Substitute.For<ILogger<UnitOfWork>>()),
            llmRepository,
            new FixedTimeProvider(Now));
    }

    private static async Task<string> SeedUserAsync()
    {
        var id = TestPrefix + Guid.NewGuid().ToString("N");
        var email = $"{id}@example.invalid";
        await using var context = NewContext();
        context.AppUser.Add(new AppUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            FirstName = TestPrefix,
            LastName = "Intro",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        await context.SaveChangesAsync();
        return id;
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
            "DELETE FROM user_intro_tour_states WHERE starts_with(user_id, {0})", TestPrefix);
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"AspNetUsers\" WHERE starts_with(id, {0})", TestPrefix);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTime _now;

        public FixedTimeProvider(DateTime now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }
}
