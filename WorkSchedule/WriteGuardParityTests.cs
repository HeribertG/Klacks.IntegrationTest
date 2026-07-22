// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.WorkSchedule;

[TestFixture]
public class WriteGuardParityTests
{
    private const string SeedPrefix = "INTEGRATION_TEST_Guard_";
    private static readonly TimeOnly ShiftStart = new(8, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0);
    private const decimal SeedWorkTime = 8m;

    private SignalRTestWebApplicationFactory _factory = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;

    private Guid _clientId;
    private Guid _replaceClientId;
    private Guid _shiftId;
    private Guid _contractId;
    private Guid _absenceId;
    private Guid _otherAbsenceId;
    private DateOnly _date;
    private readonly List<Guid> _sealedDayIds = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        _clientId = Guid.NewGuid();
        _replaceClientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _contractId = Guid.NewGuid();
        _absenceId = Guid.NewGuid();
        _otherAbsenceId = Guid.NewGuid();
        _sealedDayIds.Clear();

        // A far-future date makes an accidental clash with a real (dev-app) SealedDay on the same day,
        // and thus with the global partial-unique index, practically impossible. A fresh date per test
        // keeps parametrised cases independent even though NUnit runs them sequentially.
        _date = new DateOnly(2200, 1, 1).AddDays(Random.Shared.Next(0, 30000));

        await SeedBaseFixtureAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        _context?.Dispose();
    }

    [TestCaseSource(nameof(AllPaths))]
    public async Task WriteGuard_DayLock_RefusesRealWriteOnSealedDay_AndPersistsNothing(GuardPath path)
    {
        var ctx = BuildContext();
        if (path.RequiresParentWork)
        {
            ctx.ParentWorkId = await SeedRealWorkAsync(_clientId);
        }
        await SeedActiveGlobalSealAsync(_date);

        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            async () => await path.WriteDayLockProbe(mediator, ctx, CancellationToken.None));
        ex.Message.ShouldContain("sealed",
            Case.Insensitive,
            $"{path.Name}: a real write onto a sealed day must be refused by the day-lock guard");

        var written = await path.CountWrittenAsync(ctx, _context);
        written.ShouldBe(0, $"{path.Name}: nothing may be persisted when the day-lock guard refuses");
    }

    [TestCaseSource(nameof(CollisionPaths))]
    public async Task WriteGuard_StructuralCollision_RefusesRealWrite_AndPersistsNothing(GuardPath path)
    {
        var ctx = BuildContext();
        if (path.RequiresParentWork)
        {
            ctx.ParentWorkId = await SeedRealWorkAsync(_clientId);
        }
        await path.SeedCollisionPrecondition!(ctx, _context);
        var before = await path.CountWrittenAsync(ctx, _context);

        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var ex = await Should.ThrowAsync<ConflictException>(
            async () => await path.WriteCollisionProbe!(mediator, ctx, CancellationToken.None));
        ex.Message.ShouldContain("blocked",
            Case.Insensitive,
            $"{path.Name}: a structurally conflicting real write must be refused by the pre-commit guard");

        var after = await path.CountWrittenAsync(ctx, _context);
        after.ShouldBe(before, $"{path.Name}: the refused write must not add any row beyond the precondition");
    }

    [Test]
    public async Task DayLockGuard_SoftDeletedSeal_DoesNotBlock_AndActiveSealStillBlocks()
    {
        var activeDate = _date.AddDays(1);
        var softDeletedSealId = await SeedActiveGlobalSealAsync(_date);
        await SoftDeleteSealAsync(softDeletedSealId);
        await SeedActiveGlobalSealAsync(activeDate);

        using var scope = _factory.Services.CreateScope();
        var dayLock = scope.ServiceProvider.GetRequiredService<IDayLockService>();

        await Should.ThrowAsync<InvalidRequestException>(
            async () => await dayLock.EnsureNotLockedAsync(activeDate, _clientId, null, CancellationToken.None));

        await Should.NotThrowAsync(
            async () => await dayLock.EnsureNotLockedAsync(_date, _clientId, null, CancellationToken.None));
    }

    [Test]
    public async Task CollisionGuard_SoftDeletedCollidingWork_DoesNotFalselyBlock()
    {
        var collidingWorkId = await SeedRealWorkAsync(_clientId);
        var plannedRow = new PlannedWorkRow(_clientId, _date, ShiftStart, ShiftEnd, _shiftId);

        using (var activeScope = _factory.Services.CreateScope())
        {
            var checker = activeScope.ServiceProvider.GetRequiredService<IPreCommitConflictChecker>();
            var withActiveCollision = await checker.CheckAsync(new[] { plannedRow }, null, CancellationToken.None);
            withActiveCollision.HasHardBlocking.ShouldBeTrue(
                "control: an overlapping active work must produce a structural collision");
        }

        await SoftDeleteWorkAsync(collidingWorkId);

        using (var afterScope = _factory.Services.CreateScope())
        {
            var checker = afterScope.ServiceProvider.GetRequiredService<IPreCommitConflictChecker>();
            var afterSoftDelete = await checker.CheckAsync(new[] { plannedRow }, null, CancellationToken.None);
            afterSoftDelete.HasHardBlocking.ShouldBeFalse(
                "a soft-deleted work must be invisible to the collision guard and must not be revived");
        }
    }

    [Test]
    public async Task BulkBreaks_BreakOverExistingWork_IsAllowed()
    {
        await SeedRealWorkAsync(_clientId);

        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Should.NotThrowAsync(
            async () => await mediator.Send(PlaceBreakCommand(_absenceId, null), CancellationToken.None));

        var placed = await CountActiveBreaksAsync(_absenceId, null);
        placed.ShouldBe(1, "a break may legitimately sit over an existing work (BreakDominatesCollidingWork)");
    }

    [Test]
    public async Task BulkBreaks_OverlappingAbsenceOfDifferentType_IsAllowed()
    {
        await SeedRealBreakAsync(_clientId, _otherAbsenceId, null);

        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Should.NotThrowAsync(
            async () => await mediator.Send(PlaceBreakCommand(_absenceId, null), CancellationToken.None));

        var placed = await CountActiveBreaksAsync(_absenceId, null);
        placed.ShouldBe(1, "an overlapping absence of a different type (e.g. sick during vacation) is allowed");
    }

    [Test]
    public async Task BulkBreaks_SoftDeletedDuplicateAbsence_DoesNotBlock_AndIsNotRevived()
    {
        var duplicateId = await SeedRealBreakAsync(_clientId, _absenceId, null);
        await SoftDeleteBreakAsync(duplicateId);

        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Should.NotThrowAsync(
            async () => await mediator.Send(PlaceBreakCommand(_absenceId, null), CancellationToken.None));

        var placed = await CountActiveBreaksAsync(_absenceId, null);
        placed.ShouldBe(1, "a soft-deleted duplicate absence must not block the write and must not be revived");
    }

    [Test]
    public async Task BulkBreaks_DuplicateAbsence_IsRegimeScoped_ScenarioRefused_RealAllowed()
    {
        var scenarioToken = Guid.NewGuid();
        await SeedRealBreakAsync(_clientId, _absenceId, scenarioToken);

        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Should.NotThrowAsync(
            async () => await mediator.Send(PlaceBreakCommand(_absenceId, null), CancellationToken.None));

        var ex = await Should.ThrowAsync<ConflictException>(
            async () => await mediator.Send(PlaceBreakCommand(_absenceId, scenarioToken), CancellationToken.None));
        ex.Message.ShouldContain("Duplicate absence",
            Case.Insensitive,
            "a duplicate within the same scenario regime must be refused");
    }

    [Test]
    public async Task BreakRecount_IsTokenAware_SeesScenarioWrite_ButKeepsScenarioAndMainScheduleSeparate()
    {
        var scenarioToken = Guid.NewGuid();
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            AbsenceId = _absenceId,
            CurrentDate = _date,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = SeedWorkTime,
            AnalyseToken = scenarioToken,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();

        using var scope = _factory.Services.CreateScope();
        var breakRepository = scope.ServiceProvider.GetRequiredService<IBreakRepository>();
        var clientIds = new[] { _clientId };

        var scenarioMatches = await breakRepository.GetClientIdsWithBreakOnDate(
            clientIds, _date, _absenceId, scenarioToken, CancellationToken.None);
        scenarioMatches.ShouldContain(_clientId,
            "the recount must verify a scenario write when queried with the same analyseToken");

        var mainScheduleMatches = await breakRepository.GetClientIdsWithBreakOnDate(
            clientIds, _date, _absenceId, null, CancellationToken.None);
        mainScheduleMatches.ShouldNotContain(_clientId,
            "a scenario break must not be counted as a main-schedule (token null) write");
    }

    private GuardTestContext BuildContext() => new()
    {
        ClientId = _clientId,
        ReplaceClientId = _replaceClientId,
        ShiftId = _shiftId,
        Date = _date,
        AbsenceId = _absenceId,
    };

    private static IEnumerable<GuardPath> AllPaths()
    {
        yield return SingleWorkPath();
        yield return BulkWorksPath();
        yield return BulkBreaksPath();
        yield return WorkChangePath();
    }

    private static IEnumerable<GuardPath> CollisionPaths() =>
        AllPaths().Where(p => p.EnforcesStructuralCollision);

    private static GuardPath SingleWorkPath() => new()
    {
        Name = "single-work (PostCommand<WorkResource>)",
        EnforcesStructuralCollision = false,
        WriteDayLockProbe = async (mediator, ctx, ct) =>
            await mediator.Send(new PostCommand<WorkResource>(new WorkResource
            {
                ClientId = ctx.ClientId,
                ShiftId = ctx.ShiftId,
                CurrentDate = ctx.Date,
                StartTime = ShiftStart,
                EndTime = ShiftEnd,
                WorkTime = SeedWorkTime,
            }), ct),
        CountWrittenAsync = (ctx, db) => db.Work
            .AsNoTracking()
            .CountAsync(w => w.ClientId == ctx.ClientId && w.CurrentDate == ctx.Date && w.AnalyseToken == null),
    };

    private static GuardPath BulkWorksPath() => new()
    {
        Name = "bulk-works (BulkAddWorksCommand)",
        EnforcesStructuralCollision = false,
        WriteDayLockProbe = async (mediator, ctx, ct) =>
            await mediator.Send(new BulkAddWorksCommand(new BulkAddWorksRequest
            {
                PeriodStart = ctx.Date,
                PeriodEnd = ctx.Date,
                Works = new List<BulkWorkItem>
                {
                    new()
                    {
                        ClientId = ctx.ClientId,
                        ShiftId = ctx.ShiftId,
                        CurrentDate = ctx.Date,
                        StartTime = ShiftStart,
                        EndTime = ShiftEnd,
                        WorkTime = SeedWorkTime,
                    },
                },
            }), ct),
        CountWrittenAsync = (ctx, db) => db.Work
            .AsNoTracking()
            .CountAsync(w => w.ClientId == ctx.ClientId && w.CurrentDate == ctx.Date && w.AnalyseToken == null),
    };

    private static GuardPath BulkBreaksPath()
    {
        Func<IMediator, GuardTestContext, CancellationToken, Task> write = async (mediator, ctx, ct) =>
            await mediator.Send(new BulkAddBreaksCommand(new BulkAddBreaksRequest
            {
                PeriodStart = ctx.Date,
                PeriodEnd = ctx.Date,
                Breaks = new List<BulkBreakItem>
                {
                    new()
                    {
                        ClientId = ctx.ClientId,
                        AbsenceId = ctx.AbsenceId,
                        CurrentDate = ctx.Date,
                        StartTime = ShiftStart,
                        EndTime = ShiftEnd,
                        WorkTime = SeedWorkTime,
                    },
                },
            }), ct);

        return new GuardPath
        {
            Name = "bulk-breaks (BulkAddBreaksCommand)",
            EnforcesStructuralCollision = true,
            WriteDayLockProbe = write,
            WriteCollisionProbe = write,
            // The break structural guard is duplicate-absence: the precondition is an active break of the
            // SAME absence type overlapping the same client and day, not a colliding work.
            SeedCollisionPrecondition = async (ctx, db) =>
            {
                db.Break.Add(new Break
                {
                    Id = Guid.NewGuid(),
                    ClientId = ctx.ClientId,
                    AbsenceId = ctx.AbsenceId,
                    CurrentDate = ctx.Date,
                    StartTime = ShiftStart,
                    EndTime = ShiftEnd,
                    WorkTime = SeedWorkTime,
                    IsDeleted = false,
                });
                await db.SaveChangesAsync();
            },
            CountWrittenAsync = (ctx, db) => db.Break
                .AsNoTracking()
                .CountAsync(b => b.ClientId == ctx.ClientId && b.CurrentDate == ctx.Date && b.AnalyseToken == null),
        };
    }

    private static GuardPath WorkChangePath() => new()
    {
        Name = "work-change (PostCommand<WorkChangeResource>)",
        EnforcesStructuralCollision = true,
        RequiresParentWork = true,
        WriteDayLockProbe = async (mediator, ctx, ct) =>
            await mediator.Send(new PostCommand<WorkChangeResource>(new WorkChangeResource
            {
                WorkId = ctx.ParentWorkId,
                Type = WorkChangeType.CorrectionEnd,
                StartTime = ShiftStart,
                EndTime = ShiftEnd,
                ChangeTime = 0m,
                Description = SeedPrefix + "Correction",
            }), ct),
        WriteCollisionProbe = async (mediator, ctx, ct) =>
            await mediator.Send(new PostCommand<WorkChangeResource>(new WorkChangeResource
            {
                WorkId = ctx.ParentWorkId,
                Type = WorkChangeType.ReplacementWithin,
                ReplaceClientId = ctx.ReplaceClientId,
                StartTime = ShiftStart,
                EndTime = ShiftEnd,
                ChangeTime = 0m,
                Description = SeedPrefix + "Replacement",
            }), ct),
        SeedCollisionPrecondition = async (ctx, db) =>
        {
            db.Work.Add(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = ctx.ReplaceClientId,
                ShiftId = ctx.ShiftId,
                CurrentDate = ctx.Date,
                StartTime = ShiftStart,
                EndTime = ShiftEnd,
                WorkTime = SeedWorkTime,
                IsDeleted = false,
            });
            await db.SaveChangesAsync();
        },
        CountWrittenAsync = (ctx, db) => db.WorkChange
            .AsNoTracking()
            .CountAsync(wc => wc.WorkId == ctx.ParentWorkId),
    };

    private async Task SeedBaseFixtureAsync()
    {
        _context.Client.Add(new Client
        {
            Id = _clientId,
            Name = SeedPrefix + "Client",
            FirstName = "Integration",
            IsDeleted = false,
        });
        _context.Client.Add(new Client
        {
            Id = _replaceClientId,
            Name = SeedPrefix + "ReplaceClient",
            FirstName = "Integration",
            IsDeleted = false,
        });

        _context.Shift.Add(new Shift
        {
            Id = _shiftId,
            Name = SeedPrefix + "Shift",
            StartShift = ShiftStart,
            EndShift = ShiftEnd,
            IsDeleted = false,
        });

        _context.Set<Absence>().Add(new Absence
        {
            Id = _absenceId,
            Name = new MultiLanguage { De = SeedPrefix + "Absence" },
            Abbreviation = new MultiLanguage { De = "ITG" },
            Description = new MultiLanguage { De = SeedPrefix + "Absence" },
            Color = "#cccccc",
            IsDeleted = false,
        });
        _context.Set<Absence>().Add(new Absence
        {
            Id = _otherAbsenceId,
            Name = new MultiLanguage { De = SeedPrefix + "OtherAbsence" },
            Abbreviation = new MultiLanguage { De = "ITG2" },
            Description = new MultiLanguage { De = SeedPrefix + "OtherAbsence" },
            Color = "#dddddd",
            IsDeleted = false,
        });

        _context.Set<Contract>().Add(new Contract
        {
            Id = _contractId,
            Name = SeedPrefix + "Contract",
            GuaranteedHours = 168m,
            FullTime = 100m,
            ValidFrom = new DateTime(2199, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDeleted = false,
        });

        await _context.SaveChangesAsync();

        _context.Set<ClientContract>().Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            ContractId = _contractId,
            FromDate = new DateOnly(2199, 1, 1),
            IsActive = true,
            IsDeleted = false,
        });
        _context.Set<ClientContract>().Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _replaceClientId,
            ContractId = _contractId,
            FromDate = new DateOnly(2199, 1, 1),
            IsActive = true,
            IsDeleted = false,
        });

        await _context.SaveChangesAsync();
    }

    private async Task<Guid> SeedRealWorkAsync(Guid clientId)
    {
        var id = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = _shiftId,
            CurrentDate = _date,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = SeedWorkTime,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private BulkAddBreaksCommand PlaceBreakCommand(Guid absenceId, Guid? analyseToken) =>
        new(new BulkAddBreaksRequest
        {
            PeriodStart = _date,
            PeriodEnd = _date,
            Breaks = new List<BulkBreakItem>
            {
                new()
                {
                    ClientId = _clientId,
                    AbsenceId = absenceId,
                    CurrentDate = _date,
                    StartTime = ShiftStart,
                    EndTime = ShiftEnd,
                    WorkTime = SeedWorkTime,
                    AnalyseToken = analyseToken,
                },
            },
        });

    private Task<int> CountActiveBreaksAsync(Guid absenceId, Guid? analyseToken) => _context.Break
        .AsNoTracking()
        .CountAsync(b => b.ClientId == _clientId
            && b.CurrentDate == _date
            && b.AbsenceId == absenceId
            && b.AnalyseToken == analyseToken);

    private async Task<Guid> SeedRealBreakAsync(Guid clientId, Guid absenceId, Guid? analyseToken)
    {
        var id = Guid.NewGuid();
        _context.Break.Add(new Break
        {
            Id = id,
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = _date,
            StartTime = ShiftStart,
            EndTime = ShiftEnd,
            WorkTime = SeedWorkTime,
            AnalyseToken = analyseToken,
            IsDeleted = false,
        });
        await _context.SaveChangesAsync();
        return id;
    }

    private async Task SoftDeleteBreakAsync(Guid breakId)
    {
        var brk = await _context.Break.FirstAsync(b => b.Id == breakId);
        _context.Break.Remove(brk);
        await _context.SaveChangesAsync();
    }

    private async Task<Guid> SeedActiveGlobalSealAsync(DateOnly date)
    {
        var seal = new SealedDay
        {
            Id = Guid.NewGuid(),
            Date = date,
            GroupId = null,
            Level = WorkLockLevel.Closed,
            Reason = SeedPrefix + "Seal",
            SealedAt = DateTime.UtcNow,
            SealedBy = SeedPrefix + "Test",
            IsDeleted = false,
        };
        _context.SealedDay.Add(seal);
        await _context.SaveChangesAsync();
        _sealedDayIds.Add(seal.Id);
        return seal.Id;
    }

    private async Task SoftDeleteSealAsync(Guid sealId)
    {
        var seal = await _context.SealedDay.FirstAsync(s => s.Id == sealId);
        _context.SealedDay.Remove(seal);
        await _context.SaveChangesAsync();
    }

    private async Task SoftDeleteWorkAsync(Guid workId)
    {
        var work = await _context.Work.FirstAsync(w => w.Id == workId);
        _context.Work.Remove(work);
        await _context.SaveChangesAsync();
    }

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work_change WHERE work_id IN (SELECT id FROM work WHERE client_id IN ({0}, {1})) " +
            "OR replace_client_id IN ({0}, {1})",
            _clientId, _replaceClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"break\" WHERE client_id IN ({0}, {1})", _clientId, _replaceClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM work WHERE client_id IN ({0}, {1})", _clientId, _replaceClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_period_hours WHERE client_id IN ({0}, {1})", _clientId, _replaceClientId);

        foreach (var sealId in _sealedDayIds)
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM sealed_day WHERE id = {0}", sealId);
        }

        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client_contract WHERE client_id IN ({0}, {1})", _clientId, _replaceClientId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM absence WHERE id IN ({0}, {1})", _absenceId, _otherAbsenceId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM shift WHERE id = {0}", _shiftId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM contract WHERE id = {0}", _contractId);
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM client WHERE id IN ({0}, {1})", _clientId, _replaceClientId);
    }

    public sealed class GuardTestContext
    {
        public Guid ClientId { get; set; }

        public Guid ReplaceClientId { get; set; }

        public Guid ShiftId { get; set; }

        public DateOnly Date { get; set; }

        public Guid AbsenceId { get; set; }

        public Guid ParentWorkId { get; set; }
    }

    public sealed class GuardPath
    {
        public required string Name { get; init; }

        public bool EnforcesStructuralCollision { get; init; }

        public bool RequiresParentWork { get; init; }

        public required Func<IMediator, GuardTestContext, CancellationToken, Task> WriteDayLockProbe { get; init; }

        public Func<IMediator, GuardTestContext, CancellationToken, Task>? WriteCollisionProbe { get; init; }

        public Func<GuardTestContext, DataBaseContext, Task>? SeedCollisionPrecondition { get; init; }

        public required Func<GuardTestContext, DataBaseContext, Task<int>> CountWrittenAsync { get; init; }

        public override string ToString() => Name;
    }
}
