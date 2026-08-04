// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Isolates why the cross-encoder rerank pass dominates the retrieval stage and measures what the
/// available knobs actually buy. Replicates the exact tokenization and tensor layout of
/// <see cref="Klacks.Api.KnowledgeIndex.Infrastructure.Onnx.OnnxRerankerProvider"/> so the variants
/// differ only in ONNX session options, batch size and sequence cap. Reads the model files from the
/// same cache directory the application uses; touches no database and no application host.
/// Reporting only, never asserts on timings.
/// </summary>

using System.Diagnostics;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NUnit.Framework;
using Shouldly;
using Tokenizers.DotNet;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Explicit("CPU benchmark against the cached ONNX reranker model. Reporting only, run manually.")]
[Category("RealDatabase")]
public class KnowledgeIndexRerankerThroughputTests
{
    private const long PadTokenId = 1;
    private const string Query = "Lege einen neuen Mitarbeiter Hans Muster an";
    private const int Repetitions = 3;

    private string _modelPath = null!;
    private string _tokenizerPath = null!;
    private Tokenizer _tokenizer = null!;
    private string[] _candidates = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("KnowledgeIndex__ModelsRoot"),
            Path.Combine(Path.GetTempPath(), "klacks-test-models"),
            Path.Combine(localAppData, "Klacks", "models"),
            Path.Combine(localAppData, "Klacks", KnowledgeIndexConstants.ModelsCacheSubdirectory),
            Path.Combine(localAppData, KnowledgeIndexConstants.ModelsCacheSubdirectory),
        };

        var dir = roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .Select(r => Path.Combine(r!, KnowledgeIndexConstants.RerankerModelName))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, KnowledgeIndexConstants.RerankerModelFileName)));

        if (dir is null)
            Assert.Ignore($"Reranker model not found in any known cache root. Set KnowledgeIndex__ModelsRoot.");

        _modelPath = Path.Combine(dir!, KnowledgeIndexConstants.RerankerModelFileName);
        _tokenizerPath = Path.Combine(dir!, KnowledgeIndexConstants.RerankerTokenizerFileName);
        _tokenizer = new Tokenizer(vocabPath: _tokenizerPath);
        _candidates = BuildCandidates();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _tokenizer?.Dispose();
    }

    /// <summary>
    /// Reproduces the length distribution of the real index texts (name, description, parameters,
    /// keywords and the 25-language synonym list) as measured in knowledge_index on 2026-08-03:
    /// 478 rows, median 903 chars, max 3542.
    /// </summary>
    private static string[] BuildCandidates()
    {
        var lengths = new[] { 3542, 2100, 1500, 1200, 1050, 980, 950, 930, 915, 903, 890, 870, 850, 820, 780, 740, 700, 650, 600, 540, 480, 410, 350, 220, 95 };
        var filler = "create employee mitarbeiter anlegen employé collaboratore empleado pracownik zaměstnanec munkavállaló medarbejder werknemer ";
        return lengths.Select(len =>
        {
            var text = string.Concat(Enumerable.Repeat(filler, len / filler.Length + 1));
            return text[..len];
        }).ToArray();
    }

    [Test]
    public void RerankerVariants_WallClockComparison()
    {
        TestContext.WriteLine($"Processor count: {Environment.ProcessorCount}");
        TestContext.WriteLine($"Model: {_modelPath}");
        TestContext.WriteLine($"Candidates: {_candidates.Length}, chars min={_candidates.Min(c => c.Length)} max={_candidates.Max(c => c.Length)}");

        var tokenLengths = _candidates
            .Select(c => _tokenizer.Encode(Query + " </s></s> " + c).Length)
            .ToArray();
        TestContext.WriteLine(
            $"Token lengths (uncapped): min={tokenLengths.Min()} median={tokenLengths.OrderBy(x => x).ElementAt(tokenLengths.Length / 2)} " +
            $"max={tokenLengths.Max()} — cap is 512, and the batch pads every row to the batch maximum");
        TestContext.WriteLine(string.Empty);

        var frugal = () => new SessionOptions
        {
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
        };

        var throughput = () => new SessionOptions
        {
            EnableCpuMemArena = true,
            EnableMemoryPattern = true,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Environment.ProcessorCount,
        };

        Measure("A production before (frugal, 1 thread, batch 16, cap 512)", frugal, 16, 512, sorted: false);
        Measure("B + all cores + ORT_ENABLE_ALL", throughput, 16, 512, sorted: false);
        Measure("C B + sequence cap 256", throughput, 16, 256, sorted: false);
        Measure("D B + sequence cap 128", throughput, 16, 128, sorted: false);
        Measure("E B + batch 4, cap 512", throughput, 4, 512, sorted: false);
        Measure("F B + batch 4, cap 256", throughput, 4, 256, sorted: false);
        Measure("G frugal + cap 256 (threading unchanged)", frugal, 16, 256, sorted: false);

        // Length bucketing: identical inputs and identical logits, only the batch composition changes,
        // so any gain here is free of quality cost. The batch size decides how tight the length groups
        // get, which is what the sweep below is for.
        Measure("H B + length-sorted, batch 16 (shipped)", throughput, 16, 512, sorted: true);
        Measure("I B + length-sorted, batch 8", throughput, 8, 512, sorted: true);
        Measure("J B + length-sorted, batch 4", throughput, 4, 512, sorted: true);
        Measure("K B + length-sorted, batch 2", throughput, 2, 512, sorted: true);
        Measure("L frugal + length-sorted, batch 4", frugal, 4, 512, sorted: true);

        Assert.Pass();
    }

    private void Measure(string label, Func<SessionOptions> optionsFactory, int batchSize, int maxSequenceLength, bool sorted)
    {
        using var options = optionsFactory();
        using var session = new InferenceSession(_modelPath, options);

        RunAll(session, batchSize, maxSequenceLength, sorted);

        var samples = new List<long>();
        for (var i = 0; i < Repetitions; i++)
        {
            var watch = Stopwatch.StartNew();
            RunAll(session, batchSize, maxSequenceLength, sorted);
            watch.Stop();
            samples.Add(watch.ElapsedMilliseconds);
        }

        TestContext.WriteLine($"{label}: min={samples.Min()} median={samples.OrderBy(x => x).ElementAt(samples.Count / 2)} max={samples.Max()} ms");
    }

    private void RunAll(InferenceSession session, int batchSize, int maxSequenceLength, bool sorted)
    {
        var candidates = sorted
            ? _candidates.OrderBy(c => _tokenizer.Encode(Query + " </s></s> " + c).Length).ToArray()
            : _candidates;

        for (var start = 0; start < candidates.Length; start += batchSize)
        {
            var end = Math.Min(start + batchSize, candidates.Length);
            var chunk = candidates[start..end];
            RunBatch(session, chunk, maxSequenceLength);
        }
    }

    private void RunBatch(InferenceSession session, IReadOnlyList<string> candidates, int maxSequenceLength)
    {
        var pairs = candidates.Select(c => Query + " </s></s> " + c).ToArray();
        var encoded = pairs
            .Select(p => _tokenizer.Encode(p).Select(id => (long)id).Take(maxSequenceLength).ToArray())
            .ToArray();

        var maxLen = encoded.Max(e => e.Length);
        var batchSize = candidates.Count;

        var inputIds = new long[batchSize * maxLen];
        var attentionMask = new long[batchSize * maxLen];

        for (var i = 0; i < batchSize; i++)
        {
            for (var j = 0; j < encoded[i].Length; j++)
            {
                inputIds[i * maxLen + j] = encoded[i][j];
                attentionMask[i * maxLen + j] = 1;
            }

            for (var j = encoded[i].Length; j < maxLen; j++)
            {
                inputIds[i * maxLen + j] = PadTokenId;
            }
        }

        var dims = new[] { batchSize, maxLen };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dims)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, dims))
        };

        using var outputs = session.Run(inputs);
        outputs.First(o => o.Name == "logits").AsTensor<float>().Length.ShouldBeGreaterThan(0);
    }
}
