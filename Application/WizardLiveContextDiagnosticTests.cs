// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Diagnostic harness reproducing the BE/Bern June live wizard run against the real dev
/// database: builds the genuine wizard context (provider, snapshot, shifts, constraints)
/// and runs the GA so the effective contract days, agent flags and resulting plan become
/// visible. Explains why specific days stay unplanned in the UI run.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Application;

[TestFixture]
[Category("RealDatabase")]
public class WizardLiveContextDiagnosticTests
{
    private DataBaseContext _context = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _context.Dispose();

    [Test, Explicit("Diagnostic against the real dev DB — dumps the BE/Bern June wizard context and plan.")]
    public async Task Diagnose_BernJuneLiveContext()
    {
        var from = new DateOnly(2026, 6, 1);
        var until = new DateOnly(2026, 6, 30);

        var clients = await _context.Client
            .Where(c => c.Name == "Gasparoli" || c.Name == "Frey")
            .Select(c => new { c.Id, c.Name, c.FirstName })
            .ToListAsync();

        foreach (var c in clients)
        {
            var contracts = await _context.ClientContract
                .Where(cc => cc.ClientId == c.Id)
                .Select(cc => $"{cc.FromDate:yyyy-MM-dd}..{(cc.UntilDate.HasValue ? cc.UntilDate.Value.ToString("yyyy-MM-dd") : "open")} active={cc.IsActive}")
                .ToListAsync();
            TestContext.Out.WriteLine(
                $"CLIENT {c.Name} {c.FirstName,-13} id={c.Id} contracts=[{string.Join(" | ", contracts)}]");
        }

        // Exactly the three clients visible in the BE/Bern schedule view: duplicates exist,
        // the visible generation is the one whose contracts start 2026-06-06.
        var heribert2 = Guid.Parse("019e9b7f-255c-79cf-9f44-d0cafc26f900");
        var marieAnne = Guid.Parse("019e9b7f-cb8b-70e7-aff3-acb795b15bbe");
        var frey = Guid.Parse("019e9b81-d9cc-74d8-95e8-066b221b4adc");
        var agentIds = new List<Guid> { heribert2, frey, marieAnne };
        TestContext.Out.WriteLine($"matched agents: {agentIds.Count} (live trio, contracts from 06-06)");

        var shiftIds = await _context.Shift
            .Where(s => s.AnalyseToken == null
                && (s.Abbreviation == "F10" || s.Abbreviation == "S10" || s.Abbreviation == "N10"))
            .Select(s => s.Id)
            .ToListAsync();
        TestContext.Out.WriteLine($"shiftIds found: {shiftIds.Count}");

        var provider = new ClientContractDataProvider(_context);
        var agentBuilder = new WizardAgentSnapshotBuilder(provider);
        var shiftBuilder = new WizardShiftBuilder(_context);
        var hardBuilder = new WizardHardConstraintBuilder(_context);
        var periodHours = Substitute.For<IPeriodHoursService>();
        periodHours.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((from, until));
        periodHours
            .GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<Guid, Klacks.Api.Domain.DTOs.Schedules.PeriodHoursResource>());

        var eligibilityBuilder = Substitute.For<IEligibilityMatrixBuilder>();
        eligibilityBuilder
            .BuildAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<EligibilitySlot>>(), Arg.Any<CancellationToken>())
            .Returns(EligibilityMatrix.Empty);
        var availabilityService = Substitute.For<IAvailabilityIneligibilityService>();
        availabilityService
            .GetAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<AvailabilityShiftSlot>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<(string, Guid, DateOnly)>)new HashSet<(string, Guid, DateOnly)>());
        var builder = new WizardContextBuilder(agentBuilder, shiftBuilder, hardBuilder, periodHours, provider, eligibilityBuilder, availabilityService);
        var ctx = await builder.BuildContextAsync(
            new WizardContextRequest(from, until, agentIds, shiftIds, null),
            CancellationToken.None);

        TestContext.Out.WriteLine($"agents={ctx.Agents.Count} shifts(slots)={ctx.Shifts.Count} contractDays={ctx.ContractDays.Count}");
        TestContext.Out.WriteLine($"breakBlockers={ctx.BreakBlockers.Count} commands={ctx.ScheduleCommands.Count} prefs={ctx.ShiftPreferences.Count}");
        TestContext.Out.WriteLine($"lockedWorks={ctx.LockedWorks.Count} existingWorks={ctx.ExistingWorkBlockers.Count} boundaryLocked={ctx.BoundaryLockedWorks.Count} boundaryExisting={ctx.BoundaryExistingWorkBlockers.Count}");

        foreach (var agent in ctx.Agents)
        {
            var name = clients.First(c => c.Id.ToString() == agent.Id);
            TestContext.Out.WriteLine(
                $"AGENT {name.FirstName,-13} guaranteed={agent.GuaranteedHours,5} max={agent.MaximumHours,5} shiftWork={agent.PerformsShiftWork} Mo={agent.WorkOnMonday} Sa={agent.WorkOnSaturday} So={agent.WorkOnSunday} maxWorkDays={agent.MaxWorkDays} minRestDays={agent.MinRestDays} minRest={agent.MinRestHours}h");

            var days = ctx.ContractDays
                .Where(d => d.AgentId == agent.Id && d.Date <= new DateOnly(2026, 6, 8))
                .OrderBy(d => d.Date)
                .Select(d => $"{d.Date:dd}:{(d.WorksOnDay ? "1" : "0")}");
            TestContext.Out.WriteLine($"  contractDays 01.-08.06: {string.Join(" ", days)}");
        }

        var slotDates = ctx.Shifts.Select(s => s.Date).Distinct().OrderBy(d => d).ToList();
        TestContext.Out.WriteLine($"slot dates: {slotDates.First()} .. {slotDates.Last()} ({slotDates.Count} days)");

        var loop = TokenEvolutionLoop.Create();
        var best = loop.Run(ctx, new TokenEvolutionConfig { RandomSeed = 42 });

        TestContext.Out.WriteLine($"PLAN tokens={best.Tokens.Count}/{ctx.Shifts.Count} hardViol={best.FitnessStage0}");
        foreach (var agent in ctx.Agents)
        {
            var name = clients.First(c => c.Id.ToString() == agent.Id);
            var tokens = best.Tokens.Where(t => t.AgentId == agent.Id).ToList();
            var early = tokens.Count(t => t.ShiftTypeIndex == 0);
            var late = tokens.Count(t => t.ShiftTypeIndex == 1);
            var night = tokens.Count(t => t.ShiftTypeIndex == 2);
            TestContext.Out.WriteLine($"  {name.FirstName,-13} tokens={tokens.Count} E/L/N={early}/{late}/{night} hours={tokens.Sum(t => (double)t.TotalHours)}");
        }

        var firstFive = best.Tokens.Count(t => t.Date < new DateOnly(2026, 6, 6));
        TestContext.Out.WriteLine($"tokens on 01.-05.06: {firstFive}");
    }
}
