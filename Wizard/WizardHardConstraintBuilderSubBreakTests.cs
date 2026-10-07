// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// V3 (2026-10-07) on PostgreSQL: a container sub-break (Break with ParentWorkId) is part of the container work, not
/// an absence, so Wizard 1 must not turn it into a CoreBreakBlocker that closes the whole day. Runs the production
/// WizardHardConstraintBuilder query against the real provider (the in-memory provider cannot prove translation).
/// Far-future dates isolate the seeded rows; everything is keyed by the INTEGRATION_TEST_ prefix and removed again.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Break = Klacks.Api.Domain.Models.Schedules.Break;
using Shift = Klacks.Api.Domain.Models.Schedules.Shift;
using Work = Klacks.Api.Domain.Models.Schedules.Work;

namespace Klacks.IntegrationTest.Wizard;

[TestFixture]
[Category("RealDatabase")]
public class WizardHardConstraintBuilderSubBreakTests
{
    private const string TestPrefix = "INTEGRATION_TEST_SUBBREAK_";
    private static readonly DateOnly PeriodFrom = new(2099, 9, 7);
    private static readonly DateOnly PeriodUntil = new(2099, 9, 11);
    private static readonly DateOnly ContainerDay = new(2099, 9, 8);
    private static readonly DateOnly AbsenceDay = new(2099, 9, 9);

    private DataBaseContext _context = null!;

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    private async Task CleanupAsync()
    {
        var sql = $@"
            DELETE FROM ""break"" WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM absence WHERE name->>'de' LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await _context.Database.ExecuteSqlRawAsync(sql);
    }

    [Test]
    public async Task ContainerSubBreak_DoesNotBecomeABreakBlocker_WhileARealAbsenceDoes()
    {
        var client = new Client { Id = Guid.NewGuid(), Name = TestPrefix + "Agent", FirstName = "Test", Company = string.Empty };
        var shift = new Shift
        {
            Id = Guid.NewGuid(), Name = TestPrefix + "Container", Abbreviation = "TST", Description = "Sub-break test",
            Status = ShiftStatus.OriginalShift, FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(7, 0), EndShift = new TimeOnly(19, 0),
            IsMonday = true, IsTuesday = true, IsWednesday = true, IsThursday = true, IsFriday = true,
            ShiftType = ShiftType.IsTask, Quantity = 1, WorkTime = 12m,
        };
        var container = new Work
        {
            Id = Guid.NewGuid(), ClientId = client.Id, ShiftId = shift.Id, CurrentDate = ContainerDay,
            StartTime = new TimeOnly(7, 0), EndTime = new TimeOnly(19, 0), WorkTime = 12m, LockLevel = WorkLockLevel.None,
        };
        var absence = new Absence { Id = Guid.NewGuid(), Name = new MultiLanguage { De = TestPrefix + "Pause" } };
        _context.Client.Add(client);
        _context.Shift.Add(shift);
        _context.Work.Add(container);
        _context.Absence.Add(absence);
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(), ClientId = client.Id, CurrentDate = ContainerDay, ParentWorkId = container.Id,
            AbsenceId = absence.Id, StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(12, 30), WorkTime = 0m,
        });
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(), ClientId = client.Id, CurrentDate = AbsenceDay,
            AbsenceId = absence.Id, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(17, 0), WorkTime = 8m,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var keywords = Substitute.For<IScheduleCommandKeywordProvider>();
        keywords.GetAsync(Arg.Any<CancellationToken>()).Returns(new ScheduleCommandKeywordSet
        {
            FreeToken = "FREE", NegFreeToken = "-FREE", EarlyToken = "EARLY", NegEarlyToken = "-EARLY",
            LateToken = "LATE", NegLateToken = "-LATE", NightToken = "NIGHT", NegNightToken = "-NIGHT",
        });
        var sut = new WizardHardConstraintBuilder(_context, keywords);

        var result = await sut.BuildAsync([client.Id], PeriodFrom, PeriodUntil, null, CancellationToken.None);

        var blocker = result.BreakBlockers.ShouldHaveSingleItem("only the real absence closes a day; the container pause is part of the work");
        blocker.FromInclusive.ShouldBe(AbsenceDay);
    }
}
