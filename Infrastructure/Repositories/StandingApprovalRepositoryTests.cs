// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The standing-approval lookup against the real PostgreSQL database - the part no in-memory test can
/// prove, because the rule that decides whether a grant applies is an expression tree that has to
/// TRANSLATE (StandingApprovalPolicy.ActiveAt) rather than run in memory. Four properties are pinned
/// here: an expired and a revoked grant are invisible to the lookup; the scope is matched by exact
/// GroupId equality, so a grant for the ungrouped bucket never reaches a grouped finding and vice versa
/// (the governance lookup's group-to-installation fallback must NOT exist here); the newest grant wins
/// when a race produced two; and a revocation is staged, not committed, by the repository itself.
///
/// Every row carries the INTEGRATION_TEST_STANDING_ prefix in trigger_kind and cleanup deletes ONLY rows
/// with that prefix - never by business-plausible values.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Infrastructure.Repositories;

[TestFixture]
[Category("RealDatabase")]
public class StandingApprovalRepositoryTests
{
    private const string TestPrefix = "INTEGRATION_TEST_STANDING_";
    private const string Kind = TestPrefix + "kind";
    private const int Budget = 7;

    private static readonly DateTime NowUtc = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GranterUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GroupId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await CleanupAsync();

    [TearDown]
    public async Task TearDown() => await CleanupAsync();

    [Test]
    public async Task AnActiveGrant_IsFoundForItsOwnKindAndGroup()
    {
        var grant = await GivenGrantAsync(GroupId, NowUtc.AddDays(-1), NowUtc.AddDays(29));

        await using var context = NewContext();
        var found = await new StandingApprovalRepository(context)
            .FindActiveAsync(Kind, GroupId, NowUtc);

        found.ShouldNotBeNull();
        found!.Id.ShouldBe(grant.Id);
        found.DailyBudget.ShouldBe(Budget);
    }

    [Test]
    public async Task AnExpiredGrant_IsNotFound()
    {
        await GivenGrantAsync(GroupId, NowUtc.AddDays(-40), NowUtc.AddMinutes(-1));

        await using var context = NewContext();
        (await new StandingApprovalRepository(context).FindActiveAsync(Kind, GroupId, NowUtc)).ShouldBeNull();
    }

    [Test]
    public async Task ARevokedGrant_IsNotFound()
    {
        await GivenGrantAsync(GroupId, NowUtc.AddDays(-1), NowUtc.AddDays(29), NowUtc.AddHours(-2));

        await using var context = NewContext();
        (await new StandingApprovalRepository(context).FindActiveAsync(Kind, GroupId, NowUtc)).ShouldBeNull();
    }

    /// <summary>
    /// The highest-risk property of the whole feature: a null GroupId is the ungrouped bucket, NOT a
    /// wildcard. If this ever starts passing in both directions, one grant has become unattended autonomy
    /// for the entire installation.
    /// </summary>
    [Test]
    public async Task TheScope_IsMatchedExactly_WithNoFallbackBetweenGroupAndInstallation()
    {
        await GivenGrantAsync(null, NowUtc.AddDays(-1), NowUtc.AddDays(29));

        await using var context = NewContext();
        var repository = new StandingApprovalRepository(context);

        (await repository.FindActiveAsync(Kind, GroupId, NowUtc))
            .ShouldBeNull("A grant for the ungrouped bucket must never cover a grouped finding.");
        (await repository.FindActiveAsync(Kind, null, NowUtc)).ShouldNotBeNull();
    }

    [Test]
    public async Task TheNewestGrant_WinsWhenTwoAreActiveForTheSameScope()
    {
        await GivenGrantAsync(GroupId, NowUtc.AddDays(-5), NowUtc.AddDays(25));
        var newer = await GivenGrantAsync(GroupId, NowUtc.AddDays(-1), NowUtc.AddDays(29));

        await using var context = NewContext();
        var found = await new StandingApprovalRepository(context).FindActiveAsync(Kind, GroupId, NowUtc);

        found!.Id.ShouldBe(newer.Id);
    }

    /// <summary>
    /// The repository is stage-only: without the caller's SaveChanges nothing is written. Pins that the
    /// grant handlers' IUnitOfWork.CompleteAsync is load-bearing rather than decorative.
    /// </summary>
    [Test]
    public async Task Revoking_IsOnlyStaged_AndNeedsTheCallersCommit()
    {
        var grant = await GivenGrantAsync(GroupId, NowUtc.AddDays(-1), NowUtc.AddDays(29));

        await using (var uncommitted = NewContext())
        {
            (await new StandingApprovalRepository(uncommitted)
                .TryRevokeAsync(grant.Id, GranterUserId, NowUtc)).ShouldBeTrue();
        }

        await using (var verify = NewContext())
        {
            (await verify.StandingApprovals.AsNoTracking().SingleAsync(row => row.Id == grant.Id))
                .RevokedAtUtc.ShouldBeNull("Nothing may reach the database without the caller's commit.");
        }

        await using (var committed = NewContext())
        {
            var repository = new StandingApprovalRepository(committed);
            (await repository.TryRevokeAsync(grant.Id, GranterUserId, NowUtc)).ShouldBeTrue();
            await committed.SaveChangesAsync();

            (await repository.TryRevokeAsync(grant.Id, GranterUserId, NowUtc))
                .ShouldBeFalse("A second revocation finds nothing left to withdraw.");
        }

        await using var final = NewContext();
        var stored = await final.StandingApprovals.AsNoTracking().SingleAsync(row => row.Id == grant.Id);
        stored.RevokedAtUtc.ShouldBe(NowUtc);
        stored.RevokedByUserId.ShouldBe(GranterUserId);
    }

    private async Task<StandingApproval> GivenGrantAsync(
        Guid? groupId, DateTime grantedAtUtc, DateTime expiresAtUtc, DateTime? revokedAtUtc = null)
    {
        var approval = new StandingApproval
        {
            Id = Guid.NewGuid(),
            TriggerKind = Kind,
            GroupId = groupId,
            GrantedByUserId = GranterUserId,
            GrantedAtUtc = grantedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            DailyBudget = Budget,
            RevokedAtUtc = revokedAtUtc,
            RevokedByUserId = revokedAtUtc is null ? null : GranterUserId
        };

        await using var context = NewContext();
        context.StandingApprovals.Add(approval);
        await context.SaveChangesAsync();

        return approval;
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
            "DELETE FROM agent_standing_approval WHERE trigger_kind LIKE {0}", TestPrefix + "%");
    }
}
