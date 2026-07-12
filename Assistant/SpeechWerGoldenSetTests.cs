// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live eval of the speech-wer goldset (STT transcription quality). Loads the goldset,
/// transcribes every item whose audio recording is present under
/// Application/Skills/Goldsets/SpeechAudio/ through the configured STT provider, scores
/// word error rate, name accuracy and composite, and persists an EvalRun. Items without
/// a recording are skipped and reported.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.SpeechEval;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Explicit("Performs real STT provider calls (costs money), requires a configured STT API key and recorded goldset audio files, and writes an EvalRun to the real DB on port 5434. Run manually only.")]
[Category("Llm")]
[Category("RealDatabase")]
public class SpeechWerGoldenSetTests
{
    private const string ModelIdEnvironmentVariable = "SPEECHWER_MODEL_ID";
    private const string DefaultModelId = "groq-whisper";

    private SignalRTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [Test]
    public async Task SpeechWerGoldset_TranscribesAvailableRecordingsAndReportsScorecard()
    {
        var modelId = Environment.GetEnvironmentVariable(ModelIdEnvironmentVariable) ?? DefaultModelId;

        using var scope = _factory.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<ISpeechGoldsetLoader>();
        var evalService = scope.ServiceProvider.GetRequiredService<ISpeechWerEvalService>();

        var items = await loader.LoadAsync(SpeechEvalConstants.GoldsetName);
        items.Count.ShouldBeGreaterThan(0);

        var result = await evalService.RunAsync(modelId);

        WriteScorecard(modelId, result);
    }

    private static void WriteScorecard(string modelId, SpeechWerEvalRunResult result)
    {
        var dimensions = result.Dimensions!;

        TestContext.WriteLine($"Goldset:       {SpeechEvalConstants.GoldsetName}");
        TestContext.WriteLine($"Provider:      {modelId}");
        TestContext.WriteLine($"Composite:     {(result.Run == null ? "n/a (nothing measured)" : result.Run.CompositeScore.ToString("F4"))}");
        TestContext.WriteLine($"AvgWer:        {Format(dimensions.AvgWer)}");
        TestContext.WriteLine($"NameAccuracy:  {Format(dimensions.NameAccuracy)}");
        TestContext.WriteLine($"AvgLatencyMs:  {dimensions.AvgLatencyMs:F0}");
        TestContext.WriteLine(
            $"Items:         total={dimensions.ItemsTotal}, measured={dimensions.ItemsMeasured}, skipped={dimensions.ItemsSkipped}");

        if (result.Message != null)
        {
            TestContext.WriteLine($"Message:       {result.Message}");
        }

        foreach (var item in result.Items.Where(i => i.Skipped))
        {
            TestContext.WriteLine($"SKIPPED {item.ItemId}: audio file missing ({item.AudioFile})");
        }

        foreach (var item in result.Items.Where(i => !i.Skipped))
        {
            TestContext.WriteLine(
                $"MEASURED {item.ItemId}: wer={Format(item.Wer)}, nameAccuracy={Format(item.NameAccuracy)}, " +
                $"composite={Format(item.Composite)}, latencyMs={item.LatencyMs:F0}, transcript=\"{item.Transcript}\"");
        }
    }

    private static string Format(double? value) => value?.ToString("F4") ?? "n/a";
}
