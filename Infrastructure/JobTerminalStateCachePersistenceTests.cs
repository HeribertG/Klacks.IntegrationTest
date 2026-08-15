// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for JobTerminalStateCache against the real PostgreSQL database. They prove the two
/// properties an in-memory provider cannot prove: a terminal state written by one API instance is
/// readable by another, and "first terminal state wins" actually holds, because that rule rests on the
/// unique index on job_id plus the DbUpdateException catch rather than on any in-process bookkeeping.
/// EF InMemory ignores unique indexes, so the matching unit tests pass without ever exercising the rule.
/// Cleanup deletes ONLY rows this fixture created - the dev app shares this database.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Infrastructure;

[TestFixture]
[Category("RealDatabase")]
public class JobTerminalStateCachePersistenceTests
{
    private const string TestPrefix = "INTEGRATION_TEST_JOBSTATE_";

    private string _connectionString = null!;
    private readonly List<Guid> _createdJobIds = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await using var context = NewContext();
        await CleanupAsync(context);
        _createdJobIds.Clear();
    }

    [Test]
    public async Task TerminalStateStoredByOneInstance_IsReadableByAnother()
    {
        var jobId = NewJobId();
        var storingInstance = NewCache(out _);
        var pollingInstance = NewCache(out _);

        await storingInstance.StoreCompletedAsync(jobId, ResultFor(jobId));

        var state = await pollingInstance.TryGetAsync(jobId);

        state.Found.ShouldBeTrue();
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Reason.ShouldBeNull();
        state.Result.ShouldNotBeNull();
        state.Result.JobId.ShouldBe(jobId);
        state.Result.TokenCount.ShouldBe(7);
        state.Result.AvailableShiftSlots.ShouldBe(9);
        state.Result.SubScoreJson.ShouldBe(MarkerFor(jobId));
    }

    [Test]
    public async Task FailureArrivingAfterCompletion_DoesNotOverwriteIt_EnforcedByTheDatabase()
    {
        var jobId = NewJobId();
        var completingInstance = NewCache(out _);
        var lateFailureInstance = NewCache(out var lateFailureLog);

        await completingInstance.StoreCompletedAsync(jobId, ResultFor(jobId));
        await lateFailureInstance.StoreFailedAsync(jobId, TestPrefix + "late failure");

        // Exactly one row: the unique index on job_id rejected the second insert. Without this the test
        // would rebuild the very trap it exists to close - two rows survive when the index is gone, and
        // FirstOrDefault has no ORDER BY, so the status assertion alone would pass or fail by luck.
        (await CountRowsAsync(jobId)).ShouldBe(1);

        // The duplicate must be handled as the expected, benign outcome by the DbUpdateException catch,
        // not by the outer safety net that swallows and logs everything else.
        lateFailureLog.Errors.ShouldBeEmpty();

        var state = await lateFailureInstance.TryGetAsync(jobId);
        state.Found.ShouldBeTrue();
        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        state.Reason.ShouldBeNull();
        state.Result.ShouldNotBeNull();
        state.Result.JobId.ShouldBe(jobId);
    }

    private Guid NewJobId()
    {
        var jobId = Guid.NewGuid();
        _createdJobIds.Add(jobId);
        return jobId;
    }

    private static string MarkerFor(Guid jobId) => TestPrefix + jobId;

    private static WizardJobResultDto ResultFor(Guid jobId) => new(
        JobId: jobId,
        FinalHardViolations: 0,
        FinalStage1Completion: 1.0,
        TokenCount: 7,
        AvailableShiftSlots: 9,
        Tokens: [],
        Awards: [],
        Escalations: [],
        QualificationGaps: [],
        TimedOut: false,
        SubScoreJson: MarkerFor(jobId),
        Stage0Violations: 0);

    private async Task<int> CountRowsAsync(Guid jobId)
    {
        await using var context = NewContext();
        return await context.JobTerminalStates.AsNoTracking().CountAsync(r => r.JobId == jobId);
    }

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    /// <summary>
    /// Builds one cache over its own scope factory, standing in for a separate API instance: nothing is
    /// shared between two of these but the database itself.
    /// </summary>
    private JobTerminalStateCache<WizardJobResultDto> NewCache(out RecordingLogger log)
    {
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(DataBaseContext)).Returns(_ => NewContext());
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        log = new RecordingLogger();
        return new JobTerminalStateCache<WizardJobResultDto>(scopeFactory, log);
    }

    private async Task CleanupAsync(DataBaseContext context)
    {
        // JobIds are Guids and carry no prefix, so the tracked ids of this run are the authoritative
        // filter. The two prefix filters only mop up rows left behind by a crashed earlier run; both
        // patterns are this fixture's own marker, so neither can reach dev-app or production data.
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM job_terminal_states WHERE job_id = ANY({0}) OR reason LIKE {1} OR result_json LIKE {2}",
            _createdJobIds.ToArray(),
            TestPrefix + "%",
            "%" + TestPrefix + "%");
    }

    private sealed class RecordingLogger : ILogger<JobTerminalStateCache<WizardJobResultDto>>
    {
        public List<string> Errors { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
