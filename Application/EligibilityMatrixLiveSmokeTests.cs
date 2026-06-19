// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live smoke against the real dev database: runs the EligibilityMatrixBuilder over the actual
/// shift-required-qualification and client-qualification rows, proving the EF wiring (bulk loads +
/// includes) works against Postgres and that the derived severity invariant holds — every ineligible
/// (veto) triple carries a hard Error gap, and no warning-only triple blocks.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Application;

[TestFixture]
[Category("RealDatabase")]
public class EligibilityMatrixLiveSmokeTests
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

    [Test, Explicit("Live smoke against the real dev DB (port 5434) — proves matrix wiring + severity invariant.")]
    public async Task BuildAsync_AgainstRealData_ProducesMatrixWithSeverityInvariant()
    {
        var date = new DateOnly(2026, 6, 15);

        var gatedShiftIds = await _context.ShiftRequiredQualification
            .Where(srq => !srq.IsDeleted)
            .Select(srq => srq.ShiftId)
            .Distinct()
            .ToListAsync();
        gatedShiftIds.ShouldNotBeEmpty("dev DB is expected to have shift_required_qualification rows");

        var agentIds = await _context.Client
            .Where(c => !c.IsDeleted)
            .Select(c => c.Id)
            .Take(200)
            .ToListAsync();

        var slots = gatedShiftIds.Select(id => new EligibilitySlot(id, date)).ToList();

        var builder = new EligibilityMatrixBuilder(
            new ClientQualificationRepository(_context, Substitute.For<ILogger<ClientQualification>>()),
            new ShiftRequiredQualificationRepository(_context, Substitute.For<ILogger<ShiftRequiredQualification>>()));

        var matrix = await builder.BuildAsync(agentIds, slots);

        var errorGaps = matrix.Gaps.SelectMany(kv => kv.Value).Count(g => g.Severity == QualificationGapSeverity.Error);
        var warningGaps = matrix.Gaps.SelectMany(kv => kv.Value).Count(g => g.Severity == QualificationGapSeverity.Warning);
        TestContext.Out.WriteLine(
            $"agents={agentIds.Count} gatedShifts={gatedShiftIds.Count} ineligible={matrix.Ineligible.Count} " +
            $"gapTriples={matrix.Gaps.Count} errorGaps={errorGaps} warningGaps={warningGaps} qualInfos={matrix.QualificationInfo.Count}");

        // Invariant 1: every ineligible (veto) triple is backed by at least one hard Error gap.
        foreach (var triple in matrix.Ineligible)
        {
            matrix.Gaps.ContainsKey(triple).ShouldBeTrue($"ineligible triple {triple} must have gaps");
            matrix.Gaps[triple].ShouldContain(g => g.Severity == QualificationGapSeverity.Error,
                $"ineligible triple {triple} must carry an Error gap");
        }

        // Invariant 2: a triple whose gaps are all warnings must NOT block (severity steuert Veto).
        foreach (var (triple, gaps) in matrix.Gaps)
        {
            if (gaps.All(g => g.Severity == QualificationGapSeverity.Warning))
            {
                matrix.Ineligible.ShouldNotContain(triple,
                    $"warning-only triple {triple} must stay assignable");
            }
        }
    }
}
