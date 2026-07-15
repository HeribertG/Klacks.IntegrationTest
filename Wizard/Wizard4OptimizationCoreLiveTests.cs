// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Read-only live verification of the Wizard-4 engine core against the real test database (port 5434):
/// builds a genuine BitmapInput + CoreWizardContext for a real period/agents and runs the anytime
/// optimisation core. Proves the W4 pipeline survives real-data context shapes and that keep-if-better
/// holds (best never worse than the snapshot). Writes nothing, so no fixture/cleanup is needed.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Wizard4;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
public class Wizard4OptimizationCoreLiveTests
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

    [Test, Explicit("Read-only run of the W4 engine core against the real test DB (port 5434).")]
    public async Task Optimize_RunsOnRealData_AndNeverReturnsWorseThanSnapshot()
    {
        var anchor = await _context.Work.AsNoTracking()
            .Where(w => w.AnalyseToken == null && !w.IsDeleted)
            .OrderByDescending(w => w.CurrentDate)
            .Select(w => w.CurrentDate)
            .FirstOrDefaultAsync();

        if (anchor == default)
        {
            Assert.Ignore("No real works in the test DB to optimise.");
        }

        var from = new DateOnly(anchor.Year, anchor.Month, 1);
        var until = from.AddMonths(1).AddDays(-1);

        var agentIds = await _context.Work.AsNoTracking()
            .Where(w => w.AnalyseToken == null && !w.IsDeleted && w.CurrentDate >= from && w.CurrentDate <= until)
            .Select(w => w.ClientId)
            .Distinct()
            .Take(12)
            .ToListAsync();
        agentIds.Count.ShouldBeGreaterThan(0);
        TestContext.Out.WriteLine($"period {from}..{until}, agents={agentIds.Count}");

        var provider = new ClientContractDataProvider(_context);
        var periodHours = Substitute.For<IPeriodHoursService>();
        periodHours.GetPeriodBoundariesAsync(Arg.Any<DateOnly>()).Returns((from, until));
        periodHours
            .GetPeriodHoursAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>())
            .Returns(new Dictionary<Guid, Klacks.Api.Domain.DTOs.Schedules.PeriodHoursResource>());
        var eligibilityBuilder = Substitute.For<IEligibilityMatrixBuilder>();
        eligibilityBuilder
            .BuildAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<EligibilitySlot>>(),
                Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(),
                Arg.Any<CancellationToken>())
            .Returns(EligibilityMatrix.Empty);

        var availabilityService = Substitute.For<IAvailabilityIneligibilityService>();
        availabilityService
            .GetAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<AvailabilityShiftSlot>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<(string, Guid, DateOnly)>)new HashSet<(string, Guid, DateOnly)>());
        var wizardBuilder = new WizardContextBuilder(
            new WizardAgentSnapshotBuilder(provider),
            new WizardShiftBuilder(_context),
            new WizardHardConstraintBuilder(_context),
            periodHours,
            provider,
            eligibilityBuilder,
            availabilityService);
        var objectiveContext = await wizardBuilder.BuildContextAsync(
            new WizardContextRequest(from, until, agentIds, null, null), CancellationToken.None);

        var harmonizerBuilder = new HarmonizerContextBuilder(
            _context,
            provider,
            new WorkSofteningRepository(_context),
            eligibilityBuilder,
            availabilityService);
        var bitmapInput = await harmonizerBuilder.BuildContextAsync(
            new HarmonizerContextRequest(from, until, agentIds, null), CancellationToken.None);

        var seed = RowSorter.Sort(BitmapBuilder.Build(bitmapInput));
        var validator = new DomainAwareReplaceValidator(
            bitmapInput.Availability, bitmapInput.BoundaryAssignments, bitmapInput.IneligibleAssignments);

        var result = new Wizard4OptimizationCore().Optimize(
            seed, objectiveContext, validator, new HarmonizerEvolutionConfig(Seed: 42, MaxGenerations: 6));

        TestContext.Out.WriteLine(
            $"baseline={result.BaselineScalar:F4} best={result.BestFitness:F4} " +
            $"gate(mandQual={result.Best.Gate.MandatoryQualMissing},legality={result.Best.Gate.Legality}," +
            $"under={result.Best.Gate.UnderSupply},over={result.Best.Gate.OverSupply})");

        result.ShouldNotBeNull();
        result.BestFitness.ShouldBeGreaterThanOrEqualTo(result.BaselineScalar - 1e-9);
        result.BestBitmap.RowCount.ShouldBe(seed.RowCount);
        result.Best.Scalar.ShouldBeInRange(0.0, 1.0);
    }
}
