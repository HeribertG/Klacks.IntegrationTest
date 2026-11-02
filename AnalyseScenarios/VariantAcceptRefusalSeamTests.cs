// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Real-database checks of an accept with keys whose commit does not simply go through. The sibling race is driven
/// deterministically: the key is consumed through a second context after the gates passed, so the accept stages everything
/// and loses in the conditional key update. A lost race and a cancellation inside the commit roll back and leave the shared
/// context empty, so a later save in the scope writes nothing: the real plan, the key rows, the scenario and the key itself
/// stay as they were. A lost commit acknowledgement on a context with EnableRetryOnFailure is verified against the stored
/// scenario and reports the accept that went through instead of a 409. Only INTEGRATION_TEST_ rows are written and removed;
/// all dates lie in 2099.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class VariantAcceptRefusalSeamTests : VariantPromotionSeamTestBase
{
    private const string RivalActor = TestPrefix + "RIVAL";
    private const int MaxCommitRetries = 2;
    private static readonly DateTime RivalStamp = new(2099, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Test]
    public async Task LostKeyRace_RollsBack_AndLeavesNothingForALaterSave()
    {
        var accept = await CreateAcceptCaseAsync();
        var handler = AcceptHandler(_scenarioService, afterTheGates: () => ConsumeKeyInASecondContextAsync(accept.Variant.Id));

        var ex = await Should.ThrowAsync<ScenarioVariantConsumedException>(() => handler.Handle(
            new AcceptAnalyseScenarioCommand(accept.ScenarioId), CancellationToken.None));

        ex.VariantNames.ShouldBe(new[] { accept.Variant.Name });
        await AssertNothingLeftToWriteAsync(_context);
        await AssertScenarioUntouchedAsync(accept);

        await using var reader = NewContext();
        var key = await reader.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == accept.Variant.Id);
        key.Status.ShouldBe(AnalyseScenarioStatus.Accepted);
        key.UpdateTime.ShouldBe(RivalStamp);
        key.CurrentUserUpdated.ShouldBe(RivalActor);
    }

    [Test]
    public async Task CancellationInsideTheKeyCommit_RollsBack_AndLeavesNothingForALaterSave()
    {
        var accept = await CreateAcceptCaseAsync();
        using var cancellation = new CancellationTokenSource();
        await using var acceptContext = NewRetryingContext(new CancelOnTransactionStartInterceptor(cancellation));
        await using var provider = AnalyseScenarioTestServiceFactory.CreateProvider(acceptContext);
        var handler = AcceptHandler(provider.GetRequiredService<IAnalyseScenarioService>(), acceptContext);

        await Should.ThrowAsync<OperationCanceledException>(() => handler.Handle(
            new AcceptAnalyseScenarioCommand(accept.ScenarioId), cancellation.Token));

        await AssertNothingLeftToWriteAsync(acceptContext);
        await AssertScenarioUntouchedAsync(accept);

        await using var reader = NewContext();
        (await reader.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == accept.Variant.Id)).Status
            .ShouldBe(AnalyseScenarioStatus.Active);
    }

    [Test]
    public async Task LostCommitAcknowledgement_IsVerified_AndReportsTheAcceptThatWentThrough()
    {
        var accept = await CreateAcceptCaseAsync();
        var interceptor = new LostCommitAcknowledgementInterceptor();
        await using var acceptContext = NewRetryingContext(interceptor);
        await using var provider = AnalyseScenarioTestServiceFactory.CreateProvider(acceptContext);
        var handler = AcceptHandler(
            provider.GetRequiredService<IAnalyseScenarioService>(),
            acceptContext,
            afterTheGates: () =>
            {
                interceptor.Arm();
                return Task.CompletedTask;
            });

        var result = await handler.Handle(new AcceptAnalyseScenarioCommand(accept.ScenarioId), CancellationToken.None);

        result.Accepted.ShouldBeTrue();
        interceptor.LostAcknowledgements.ShouldBe(1);
        acceptContext.ChangeTracker.HasChanges().ShouldBeFalse();

        await using var reader = NewContext();
        (await reader.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == accept.ScenarioId)).Status
            .ShouldBe(AnalyseScenarioStatus.Accepted);
        (await reader.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == accept.Variant.Id)).Status
            .ShouldBe(AnalyseScenarioStatus.Accepted);
        (await reader.Shift.AsNoTracking().Where(s => accept.KeyRows.Contains(s.Id)).ToListAsync())
            .ShouldAllBe(row => row.AnalyseToken == null);
        var promoted = await reader.Work.AsNoTracking().SingleAsync(w => w.Id == accept.ScenarioWork);
        promoted.AnalyseToken.ShouldBeNull();
        promoted.ShiftId.ShouldBe(accept.KeyPlannable);
    }

    private async Task<AcceptCase> CreateAcceptCaseAsync()
    {
        var variant = await CreateVariantAsync("KEY");
        var order = await CreateOrderAsync("ORDER", variant.Token);
        var real = await CreateOrderAsync("REAL", null);
        var realWork = await AddRealWorkAsync(real.Plannable.Id);
        var scenario = await CreateScenarioAsync(variant.Id);
        var scenarioWork = await AddScenarioWorkAsync(scenario.Token, order.Plannable.Id, SecondWorkDate);

        return new AcceptCase(
            variant,
            scenario.Id,
            scenario.Token,
            order.Plannable.Id,
            [order.SealedOrder.Id, order.Plannable.Id],
            scenarioWork,
            realWork);
    }

    private async Task<Guid> AddRealWorkAsync(Guid shiftId)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _worker.Id,
            ShiftId = shiftId,
            CurrentDate = WorkDate,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = WorkHours
        };
        await _context.Work.AddAsync(work);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return work.Id;
    }

    private async Task ConsumeKeyInASecondContextAsync(Guid variantId)
    {
        await using var rival = NewContext();
        await rival.Set<AnalyseScenario>()
            .Where(s => s.Id == variantId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, AnalyseScenarioStatus.Accepted)
                .SetProperty(s => s.UpdateTime, RivalStamp)
                .SetProperty(s => s.CurrentUserUpdated, RivalActor));
    }

    private static async Task AssertNothingLeftToWriteAsync(DataBaseContext context)
    {
        context.ChangeTracker.Entries().ShouldBeEmpty();
        (await context.SaveChangesAsync()).ShouldBe(0);
    }

    private async Task AssertScenarioUntouchedAsync(AcceptCase accept)
    {
        await using var reader = NewContext();

        (await reader.Set<AnalyseScenario>().AsNoTracking().SingleAsync(s => s.Id == accept.ScenarioId)).Status
            .ShouldBe(AnalyseScenarioStatus.Active);
        var realWork = await reader.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == accept.RealWork);
        realWork.IsDeleted.ShouldBeFalse();
        var scenarioWork = await reader.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == accept.ScenarioWork);
        scenarioWork.IsDeleted.ShouldBeFalse();
        scenarioWork.AnalyseToken.ShouldBe(accept.ScenarioToken);

        var keyRows = await reader.Shift.IgnoreQueryFilters().AsNoTracking()
            .Where(s => accept.KeyRows.Contains(s.Id))
            .ToListAsync();
        keyRows.Count.ShouldBe(accept.KeyRows.Count);
        keyRows.ShouldAllBe(row => row.AnalyseToken == accept.Variant.Token && !row.IsDeleted);
        var keyGroupItems = await reader.Set<GroupItem>().IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.ShiftId != null && accept.KeyRows.Contains(item.ShiftId.Value))
            .ToListAsync();
        keyGroupItems.ShouldNotBeEmpty();
        keyGroupItems.ShouldAllBe(item => item.AnalyseToken == accept.Variant.Token);

        (await reader.Shift.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(s => s.AnalyseToken == accept.ScenarioToken && !s.IsDeleted)).ShouldBeGreaterThan(0);
    }

    private DataBaseContext NewRetryingContext(IInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.EnableRetryOnFailure(MaxCommitRetries))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private sealed record AcceptCase(
        AnalyseScenario Variant,
        Guid ScenarioId,
        Guid ScenarioToken,
        Guid KeyPlannable,
        IReadOnlyList<Guid> KeyRows,
        Guid ScenarioWork,
        Guid RealWork);
}
