// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests of UiExplainOfferService (T5) against the real PostgreSQL database, with the real dispatch and
/// preference repositories: a reaction is stored as a ledger-only counter row (no inbox message), the dosage mute lands
/// in agent_trigger_preferences and is read back by the next state query, two reactions of one user never collide on
/// the unique dedup index, the "later" and "shown" counts are computed by the database, and one account's answers never
/// change another account's state; mixed sequences of shown and later mute at the second later or the third shown, and a
/// muted topic stores no further rows except the one answer to the offer whose "shown" muted it. Only the explanation level is stubbed (beginner).
/// SHARED-DATABASE SAFETY: every fixture user id carries the prefix INTEGRATION_TEST_, and cleanup deletes only rows
/// whose user_id carries that prefix, in both tables.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.ExplainOffers;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.ExplainOffers;

[TestFixture]
[Category("RealDatabase")]
public class UiExplainOfferServicePersistenceTests
{
    private const string TestPrefix = "INTEGRATION_TEST_";
    private const string UserA = TestPrefix + "UIOFFER_A";
    private const string UserB = TestPrefix + "UIOFFER_B";
    private const string Topic = UiExplainOfferTopics.ShiftHolidayOptionsKey;
    private const string Kind = UiExplainOfferTopics.TriggerKindPrefix + UiExplainOfferTopics.ShiftHolidayOptionsKey;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task CleanupAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_trigger_dispatches WHERE user_id LIKE {0}", TestPrefix + "%");
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM agent_trigger_preferences WHERE user_id LIKE {0}", TestPrefix + "%");
    }

    [Test]
    public async Task Accepted_StoresALedgerOnlyRow_AndMutesTheAccount()
    {
        await using (var context = NewContext())
        {
            var sut = NewService(context);
            (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeTrue();

            (await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown)).ShouldBeTrue();
            (await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Accepted)).ShouldBeTrue();
        }

        await using (var context = NewContext())
        {
            var rows = await context.AgentTriggerDispatches.AsNoTracking()
                .Where(row => row.UserId == UserA)
                .ToListAsync();
            rows.Count.ShouldBe(2);
            rows.ShouldAllBe(row => row.TriggerKind == Kind && row.ContentKey == null && row.NextReminderAtUtc == null);
            rows.Select(row => row.Reaction).ShouldBe([ProactiveReaction.None, ProactiveReaction.Helpful], ignoreOrder: true);

            var dispatches = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);
            (await dispatches.ListForUserAsync(UserA, unreadOnly: false, take: 50)).ShouldBeEmpty();
            (await dispatches.CountUnreadAsync(UserA)).ShouldBe(0);

            (await NewService(context).GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeFalse();
        }
    }

    [Test]
    public async Task OneAccountsAnswer_NeverChangesAnotherAccount()
    {
        await using var context = NewContext();
        var sut = NewService(context);

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Declined);

        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeFalse();
        (await sut.GetStateAsync(UserB, Topic)).ShouldOffer.ShouldBeTrue();
        (await context.AgentTriggerDispatches.AsNoTracking().CountAsync(row => row.UserId == UserB)).ShouldBe(0);
    }

    [Test]
    public async Task SecondLater_Mutes_FirstDoesNot()
    {
        await using var context = NewContext();
        var sut = NewService(context);

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Later);
        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeTrue();

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Later);
        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task ThirdUnansweredShown_Mutes_AndTheRowsNeverCollide()
    {
        await using var context = NewContext();
        var sut = NewService(context);

        for (var shown = 1; shown < UiExplainOfferDefaults.MaxShownBeforeMute; shown++)
        {
            await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown);
            (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeTrue();
        }

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown);

        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeFalse();
        (await context.AgentTriggerDispatches.AsNoTracking().CountAsync(row => row.UserId == UserA))
            .ShouldBe(UiExplainOfferDefaults.MaxShownBeforeMute);
    }

    [Test]
    public async Task ShownLaterShownLater_MutesAtTheSecondLater()
    {
        await using var context = NewContext();
        var sut = NewService(context);

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown);
        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Later);
        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown);
        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeTrue();

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Later);

        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task ShownLaterShownUnansweredShown_MutesAtTheThirdShown()
    {
        await using var context = NewContext();
        var sut = NewService(context);

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown);
        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Later);
        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown);
        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeTrue();

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown);

        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeFalse();
    }

    [Test]
    public async Task MutedTopic_WritesNoFurtherRows()
    {
        await using var context = NewContext();
        var sut = NewService(context);

        await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Declined);
        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeFalse();

        foreach (var outcome in UiExplainOfferOutcomes.All)
        {
            (await sut.RecordReactionAsync(UserA, Topic, outcome)).ShouldBeTrue();
        }

        (await context.AgentTriggerDispatches.AsNoTracking().CountAsync(row => row.UserId == UserA)).ShouldBe(1);
    }

    [Test]
    public async Task AnswerToTheMutingThirdShown_IsStoredOnce_AndNothingAfterIt()
    {
        await using var context = NewContext();
        var sut = NewService(context);

        for (var shown = 0; shown < UiExplainOfferDefaults.MaxShownBeforeMute; shown++)
        {
            await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown);
        }

        (await sut.GetStateAsync(UserA, Topic)).ShouldOffer.ShouldBeFalse();

        (await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Accepted)).ShouldBeTrue();
        (await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Declined)).ShouldBeTrue();
        (await sut.RecordReactionAsync(UserA, Topic, UiExplainOfferOutcomes.Shown)).ShouldBeTrue();

        var rows = await context.AgentTriggerDispatches.AsNoTracking()
            .Where(row => row.UserId == UserA)
            .ToListAsync();
        rows.Count.ShouldBe(UiExplainOfferDefaults.MaxShownBeforeMute + 1);
        rows.Count(row => row.Reaction == ProactiveReaction.Helpful).ShouldBe(1);
        rows.Count(row => row.Reaction == ProactiveReaction.Dismissed).ShouldBe(0);
    }

    private static UiExplainOfferService NewService(DataBaseContext context)
    {
        var governance = Substitute.For<IProactiveGovernanceResolver>();
        governance.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(false);
        var depth = Substitute.For<IExplainDepthResolver>();
        depth.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ExplainDepthProfile(ExplainExperienceLevel.Beginner, false));

        return new UiExplainOfferService(
            new ProactiveTriggerDispatchRepository(context, TimeProvider.System),
            new PersistentAgentTriggerPreferenceService(new AgentTriggerPreferenceRepository(context), TimeProvider.System),
            new ExplainOffersSwitch(Substitute.For<ISettingsReader>(), governance),
            depth,
            TimeProvider.System,
            NullLogger<UiExplainOfferService>.Instance);
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }
}
