// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Measurement, not a regression test (Explicit): how long the stop of a turn with one write action takes to
/// reach the client, against real Postgres, split in two because the client's grace window of three seconds
/// covers both. (i) Detection: from the moment the cancel request has been handed to the registry until the
/// turn state shows an outcome (the stop was noticed and the tail claimed the turn). (ii) Tail: from that
/// moment until the client has been handed turn_stopped, that is the persistence (history, usage row,
/// correction anchor, cleanup) and the start of the background tasks. Two moments of the stop are measured:
/// while the write skill is still running (the skill is released at once, so its own run time is NOT in the
/// numbers) and while the answer text streams after the write. What is real: the chat service, recorder,
/// repositories, anchor store and Postgres (localhost, warm connection pool). What is not: the model (a stub
/// whose endless stream checks the cancellation every 5 ms, so up to 5 ms of scenario B's detection is the
/// stub's pacing, and a real provider notices a cancellation on its own schedule) and the skill (a stub); the
/// controller and the network are not in it. The detector polls the turn state in a tight loop on its own
/// thread, so it reads the moment the outcome is claimed to within microseconds and burns one core meanwhile.
/// Results are written to the directory in STOPTURN_MEASUREMENT_DIR (default: the temp directory).
/// </summary>

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[TestFixture]
[Explicit("Measurement, not a regression test")]
[Category("Measurement")]
public class StopTurnLatencyMeasurementTests : StopTurnPostgresTestBase
{
    private const int WarmUpRuns = 3;
    private const int MeasuredRuns = 15;
    private const int TextStreamingGraceMs = 100;
    private const string ResultDirectoryVariable = "STOPTURN_MEASUREMENT_DIR";
    private const double MillisecondsPerSecond = 1000.0;

    [Test]
    public async Task StopWhileAWriteSkillRuns_DetectionAndTail()
    {
        var samples = await MeasureAsync(RunWhileTheWriteSkillRunsAsync);

        Report("A_stop_while_write_skill_runs", samples);
    }

    [Test]
    public async Task StopWhileTheAnswerStreamsAfterAWrite_DetectionAndTail()
    {
        var samples = await MeasureAsync(RunWhileTheTextStreamsAsync);

        Report("B_stop_while_text_streams_after_write", samples);
    }

    private async Task<List<Sample>> MeasureAsync(Func<Task<Sample>> oneRun)
    {
        var samples = new List<Sample>();
        for (var run = 0; run < WarmUpRuns + MeasuredRuns; run++)
        {
            Provider.Reset();
            Skills.Reset();
            ConversationKey = Prefix + "conv_" + Guid.NewGuid().ToString("N");
            var sample = await oneRun();
            if (run >= WarmUpRuns)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    private async Task<Sample> RunWhileTheWriteSkillRunsAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Skills.On(WriteSkill, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return ScriptedSkillBridge.Succeeded();
        });
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.Text("never asked for"));

        return await RunOneAsync(entered.Task, () => release.SetResult());
    }

    private async Task<Sample> RunWhileTheTextStreamsAsync()
    {
        var textStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Provider.Enqueue(ScriptedStep.Tools(WriteSkill), ScriptedStep.EndlessText("Working on it"));
        Provider.OnStreamStarted = streamToken =>
        {
            if (Provider.CallCount == 2)
            {
                textStarted.TrySetResult();
            }
        };

        return await RunOneAsync(
            textStarted.Task, onStopRequested: () => { }, beforeStop: () => Task.Delay(TextStreamingGraceMs));
    }

    private async Task<Sample> RunOneAsync(Task readyToStop, Action onStopRequested, Func<Task>? beforeStop = null)
    {
        var registry = Factory.Services.GetRequiredService<IActiveTurnRegistry>();
        var turnId = Guid.NewGuid();
        var stopToken = registry.Register(turnId, UserId);
        try
        {
            using var scope = Factory.Services.CreateScope();
            var state = scope.ServiceProvider.GetRequiredService<TurnRunState>();
            long stoppedChunkAt = 0;
            long doneChunkAt = 0;
            var consumer = Task.Run(async () =>
            {
                await foreach (var chunk in scope.ServiceProvider.GetRequiredService<ILLMService>()
                                   .ProcessStreamAsync(NewContext("run the first test write action", turnId, stopToken)))
                {
                    if (chunk.Type == SseChunkType.TurnStopped)
                    {
                        stoppedChunkAt = Stopwatch.GetTimestamp();
                    }
                    else if (chunk.Type == SseChunkType.Done)
                    {
                        doneChunkAt = Stopwatch.GetTimestamp();
                    }
                }
            });

            await readyToStop.WaitAsync(Patience);
            if (beforeStop != null)
            {
                await beforeStop();
            }

            long detectedAt = 0;
            var detector = Task.Factory.StartNew(
                () =>
                {
                    var wait = new SpinWait();
                    var give = Stopwatch.StartNew();
                    while (state.Outcome == null && give.Elapsed < Patience)
                    {
                        wait.SpinOnce(sleep1Threshold: -1);
                    }

                    detectedAt = Stopwatch.GetTimestamp();
                },
                TaskCreationOptions.LongRunning);

            var requestedAt = Stopwatch.GetTimestamp();
            registry.RequestStop(turnId, UserId).ShouldBe(StopRequestOutcome.Accepted);
            onStopRequested();
            await consumer.WaitAsync(Patience);
            await detector.WaitAsync(Patience);

            state.Outcome.ShouldBe(TurnOutcome.Stopped);
            return new Sample(Ms(requestedAt, detectedAt), Ms(detectedAt, stoppedChunkAt), Ms(requestedAt, stoppedChunkAt),
                Ms(stoppedChunkAt, doneChunkAt));
        }
        finally
        {
            registry.Complete(turnId);
        }
    }

    private static double Ms(long from, long to) => (to - from) * MillisecondsPerSecond / Stopwatch.Frequency;

    private static void Report(string scenario, IReadOnlyList<Sample> samples)
    {
        var text = new StringBuilder();
        text.AppendLine(FormattableString.Invariant($"scenario={scenario} runs={samples.Count} warmup_discarded={WarmUpRuns}"));
        text.AppendLine(Line("detection_ms   (cancel handed to registry -> outcome claimed)", samples.Select(s => s.Detection)));
        text.AppendLine(Line("tail_ms        (outcome claimed -> turn_stopped handed to client)", samples.Select(s => s.Tail)));
        text.AppendLine(Line("total_ms       (cancel -> turn_stopped)", samples.Select(s => s.Total)));
        text.AppendLine(Line("stopped_to_done_ms", samples.Select(s => s.ToDone)));
        text.AppendLine("raw detection/tail/total per run: " + string.Join("; ",
            samples.Select(s => FormattableString.Invariant($"{s.Detection:F1}/{s.Tail:F1}/{s.Total:F1}"))));
        TestContext.Out.WriteLine(text.ToString());

        var directory = Environment.GetEnvironmentVariable(ResultDirectoryVariable) ?? Path.GetTempPath();
        File.WriteAllText(Path.Combine(directory, $"stop-turn-latency-{scenario}.txt"), text.ToString());
        samples.Count.ShouldBe(MeasuredRuns);
    }

    private static string Line(string label, IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        double Percentile(double fraction) => sorted[(int)Math.Ceiling(fraction * sorted.Count) - 1];
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        return string.Create(CultureInfo.InvariantCulture,
            $"{label}: min={sorted[0]:F1} median={median:F1} p90={Percentile(0.9):F1} max={sorted[^1]:F1} mean={sorted.Average():F1}");
    }

    private sealed record Sample(double Detection, double Tail, double Total, double ToDone);
}
