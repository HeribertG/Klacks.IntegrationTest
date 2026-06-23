// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

/// <summary>
/// End-to-end live smoke for the engine-driven cover_absence flow against the integration database
/// (port 5434). It drives the real mediator pipeline (snapshot build, pure engine, scenario clone, Break
/// + Replacement WorkChanges) for a seeded absence and asserts a scenario with the recorded absence is
/// created, then cleans the scenario data up. Explicit because it mutates the database.
/// </summary>
[TestFixture]
public sealed class CoverAbsenceRecoveryLiveTests : WizardHarnessTestBase
{
    private sealed record Fixture(Guid GroupId, Guid ClientId, DateOnly Date, Guid AbsenceId);

    private async Task<Fixture?> FindFixtureAsync()
    {
        var absenceId = await Context.Set<Absence>()
            .OrderBy(a => a.Id).Select(a => a.Id).FirstOrDefaultAsync();
        if (absenceId == Guid.Empty)
        {
            return null;
        }

        var row = await (
                from w in Context.Set<Work>()
                join s in Context.Set<Shift>() on w.ShiftId equals s.Id
                join gi in Context.Set<GroupItem>() on w.ShiftId equals gi.ShiftId
                where w.AnalyseToken == null && gi.Group != null
                orderby w.CurrentDate descending
                select new { w.ClientId, Root = gi.Group!.Root ?? gi.Group.Id, w.CurrentDate })
            .FirstOrDefaultAsync();

        return row is null ? null : new Fixture(row.Root, row.ClientId, row.CurrentDate, absenceId);
    }

    [Test]
    [Explicit("Mutates the integration database; run on demand.")]
    public async Task CoverAbsence_creates_scenario_with_recorded_absence()
    {
        var fixture = await FindFixtureAsync();
        if (fixture is null)
        {
            Assert.Ignore("No seeded absence/work fixture available on this database.");
            return;
        }

        CoverAbsenceOutcome outcome;
        using (var scope = CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            outcome = await mediator.Send(
                new CoverAbsenceCommand(fixture.ClientId, fixture.Date, fixture.GroupId, fixture.AbsenceId),
                CancellationToken.None);
        }

        try
        {
            outcome.ScenarioId.ShouldNotBe(Guid.Empty);
            outcome.Token.ShouldNotBe(Guid.Empty);
            outcome.ScenarioName.ShouldNotBeNullOrWhiteSpace();

            var breakRecorded = await Context.Set<Break>()
                .AnyAsync(b => b.AnalyseToken == outcome.Token && b.ClientId == fixture.ClientId);
            breakRecorded.ShouldBeTrue("the absence Break must be recorded under the scenario token");
        }
        finally
        {
            await CleanUpAsync(outcome.Token, outcome.ScenarioId);
        }
    }

    private async Task CleanUpAsync(Guid token, Guid scenarioId)
    {
        using var scope = CreateScope();
        var scenarioService = scope.ServiceProvider.GetRequiredService<IAnalyseScenarioService>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await scenarioService.SoftDeleteScenarioDataAsync(token, CancellationToken.None);
        await unitOfWork.CompleteAsync();

        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();
        var scenario = await context.Set<AnalyseScenario>().FindAsync(scenarioId);
        if (scenario is not null)
        {
            context.Remove(scenario);
            await context.SaveChangesAsync();
        }
    }
}
